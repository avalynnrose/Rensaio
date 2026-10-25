using System.Collections.Concurrent;

namespace Rensaio.RPC.Runtime;

/// <summary>
/// In-process hub for the RPC host's structured log lines (RpcLogLine.ToLine format).
///
/// The child is now launched detached with NO stdout/stderr redirection, so logs can no
/// longer be captured by the backend via a pipe. Instead the logger provider publishes each
/// line here, and the backend subscribes over gRPC (ControlService.StreamLogs). A bounded
/// ring keeps recent lines so a late subscriber can replay a small backlog.
/// </summary>
public sealed class RpcLogHub
{
    private const int Capacity = 2000;

    private readonly object _sync = new();
    private readonly Queue<string> _ring = new(Capacity);
    private event Action<string>? Published;

    /// <summary>Publishes one log line to all current subscribers and the ring.</summary>
    public void Publish(string line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        Action<string>? handlers;
        lock (_sync)
        {
            _ring.Enqueue(line);
            while (_ring.Count > Capacity)
                _ring.Dequeue();
            handlers = Published;
        }

        handlers?.Invoke(line);
    }

    /// <summary>Snapshot of the most recent lines (up to <paramref name="count"/>).</summary>
    public List<string> Snapshot(int count)
    {
        lock (_sync)
        {
            if (count <= 0 || count > _ring.Count)
                count = _ring.Count;
            return _ring.Skip(_ring.Count - count).ToList();
        }
    }

    /// <summary>Subscribes to future lines; dispose to unsubscribe.</summary>
    public IDisposable Subscribe(Action<string> handler)
    {
        lock (_sync)
            Published += handler;
        return new Subscription(this, handler);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly RpcLogHub _hub;
        private Action<string>? _handler;

        public Subscription(RpcLogHub hub, Action<string> handler)
        {
            _hub = hub;
            _handler = handler;
        }

        public void Dispose()
        {
            Action<string>? h = _handler;
            if (h == null)
                return;
            _handler = null;
            lock (_hub._sync)
                _hub.Published -= h;
        }
    }
}
