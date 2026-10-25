using System.Diagnostics;
using System.Runtime.CompilerServices;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Rensaio.RPC.Contracts;
using Rensaio.RPC.Contracts.V1;
using Rensaio.RPC.Runtime;

namespace Rensaio.RPC.Services;

/// <summary>
/// Lifecycle and health surface. The backend uses Ping/GetStatus for readiness
/// and telemetry, and PrepareShutdown for a graceful stop during host shutdown.
/// </summary>
public sealed class ControlGrpcService : ControlService.ControlServiceBase
{
    private readonly RpcStartupToken _token;
    private readonly RpcReadiness _readiness;
    private readonly ExtensionLruService _lru;
    private readonly RpcLogHub _logHub;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IBridgeManager _bridge;
    private readonly ILogger<ControlGrpcService> _logger;

    public ControlGrpcService(
        RpcStartupToken token,
        RpcReadiness readiness,
        ExtensionLruService lru,
        RpcLogHub logHub,
        IHostApplicationLifetime lifetime,
        IBridgeManager bridge,
        ILogger<ControlGrpcService> logger)
    {
        _token = token;
        _readiness = readiness;
        _lru = lru;
        _logHub = logHub;
        _lifetime = lifetime;
        _bridge = bridge;
        _logger = logger;
    }

    public override Task<PingReply> Ping(PingRequest request, ServerCallContext context)
    {
        GrpcGuard.EnsureAuthorized(_token, context);
        return Task.FromResult(new PingReply
        {
            Ready = _readiness.IsReady && _readiness.Failure == null,
            ProtocolVersion = Contracts.RpcHandshake.ProtocolVersion,
            RpcVersion = Program.RpcVersion,
        });
    }

    public override Task<StatusReply> GetStatus(StatusRequest request, ServerCallContext context)
    {
        GrpcGuard.EnsureAuthorized(_token, context);
        var stats = _lru.GetStats();
        return Task.FromResult(new StatusReply
        {
            Ready = _readiness.IsReady,
            LoadedExtensions = stats.LoadedCount,
            LruCapacity = _lru.Capacity,
            LruEvictions = stats.IdleEvictions + stats.OverCapacityEvictions,
            UptimeSeconds = (long)_readiness.Uptime.TotalSeconds,
            RpcVersion = Program.RpcVersion,
        });
    }

    /// <summary>
    /// Returns the bridge-global preferences the RPC host currently holds (FlareSolverr,
    /// SocksProxy, Browser, interceptors) so the backend can reconcile/seed its own copy.
    /// </summary>
    public override async Task<JsonResult> GetPreferences(BridgePreferencesRequest request, ServerCallContext context)
    {
        GrpcGuard.EnsureAuthorized(_token, context);
        Preferences prefs = await _bridge.GetPreferencesAsync(context.CancellationToken).ConfigureAwait(false);
        return new JsonResult { Json = RpcJson.Serialize(prefs) };
    }

    /// <summary>
    /// Applies the backend's preferences to the RUNNING bridge. This is the live push that the
    /// in-process build used to get for free: without it the host only ever saw whatever
    /// <c>preferences.json</c> it loaded at startup, so a FlareSolverr toggle (or a missing file)
    /// left Cloudflare challenges unsolvable.
    /// </summary>
    public override async Task<Empty> SetPreferences(SetBridgePreferencesRequest request, ServerCallContext context)
    {
        GrpcGuard.EnsureAuthorized(_token, context);
        Preferences? prefs = RpcJson.Deserialize<Preferences>(request.PreferencesJson)
            ?? throw new SourceNotFoundException("Missing preferences payload.");

        await _bridge.SetPreferencesAsync(prefs, context.CancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Bridge preferences applied (FlareSolverr enabled={Enabled}, url={Url}, browser provider={Provider}).",
            prefs.FlareSolverr?.Enabled, prefs.FlareSolverr?.Url, prefs.Browser?.Provider);
        return new Empty();
    }

    public override Task<Empty> PrepareShutdown(ShutdownRequest request, ServerCallContext context)
    {
        GrpcGuard.EnsureAuthorized(_token, context);
        _logger.LogInformation("Graceful shutdown requested: {Reason}", request.Reason);

        // Trigger host shutdown after the response is flushed, so the backend
        // observes a clean acknowledgement before the process exits.
        _ = Task.Run(async () =>
        {
            await Task.Delay(200).ConfigureAwait(false);
            _lifetime.StopApplication();
        });

        return Task.FromResult(new Empty());
    }

    /// <summary>
    /// Streams structured log lines to the backend. Replays a small backlog, then pushes
    /// live lines. Replaces the stdout/stderr pipe now that the child is launched detached.
    /// </summary>
    public override async Task StreamLogs(
        LogStreamRequest request, IServerStreamWriter<LogLineMessage> responseStream, ServerCallContext context)
    {
        GrpcGuard.EnsureAuthorized(_token, context);
        CancellationToken token = context.CancellationToken;

        // Live buffer + a channel-free wait: use a simple async queue via the hub.
        var queue = System.Threading.Channels.Channel.CreateUnbounded<string>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });

        foreach (string back in _logHub.Snapshot(request.Backlog))
        {
            if (token.IsCancellationRequested)
                return;
            await responseStream.WriteAsync(new LogLineMessage { Line = back }).ConfigureAwait(false);
        }

        using IDisposable sub = _logHub.Subscribe(line => queue.Writer.TryWrite(line));

        try
        {
            await foreach (string line in queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(new LogLineMessage { Line = line }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Subscriber went away (backend restart) — normal.
        }
    }

    /// <summary>
    /// Liveness stream for die-with-parent. Never completes normally; the stream ends (or the
    /// call faults) exactly when the parent/backend process dies, so the child can shut down.
    /// The parent's start time guards against PID reuse.
    /// </summary>
    public override async Task WatchParent(
        WatchParentRequest request, IServerStreamWriter<Empty> responseStream, ServerCallContext context)
    {
        GrpcGuard.EnsureAuthorized(_token, context);
        CancellationToken token = context.CancellationToken;

        Process parent;
        try
        {
            parent = Process.GetProcessById(request.ParentPid);
            // PID-reuse guard: if a different process now owns the pid, treat the parent as gone.
            if (request.ParentStartUtcTicks > 0 &&
                parent.StartTime.ToUniversalTime().Ticks != request.ParentStartUtcTicks)
            {
                _logger.LogWarning("WatchParent: pid {Pid} is a different process (recycled); shutting down.", request.ParentPid);
                await BeginTerminationAsync().ConfigureAwait(false);
                return;
            }
        }
        catch (ArgumentException)
        {
            _logger.LogWarning("WatchParent: parent {Pid} is already gone; shutting down.", request.ParentPid);
            await BeginTerminationAsync().ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("WatchParent: watching backend {Pid} for liveness.", request.ParentPid);

        try
        {
            // Keep the stream open (keepalive) until the parent exits or the caller cancels.
            using var keepAlive = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (!token.IsCancellationRequested)
            {
                if (parent.HasExited)
                {
                    _logger.LogWarning("WatchParent: parent {Pid} exited; shutting down.", request.ParentPid);
                    await BeginTerminationAsync().ConfigureAwait(false);
                    return;
                }
                // A write keeps HTTP/2 idle timeouts from closing the stream.
                await responseStream.WriteAsync(new Empty()).ConfigureAwait(false);
                await keepAlive.WaitForNextTickAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // A dropped stream is NORMAL (backend reconnect, HTTP/2 idle, client restart) and
            // MUST NOT terminate the child. If the backend truly died, either the parent-pid
            // watch or the next request is what should decide; here we simply stop watching.
            _logger.LogInformation("WatchParent stream ended; no longer watching (child stays alive).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WatchParent stream faulted; no longer watching.");
        }
        finally
        {
            parent.Dispose();
        }
    }

    /// <summary>Graceful stop, then force exit (IKVM/CEF can hold foreground threads).</summary>
    private async Task BeginTerminationAsync()
    {
        _lifetime.StopApplication();
        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Environment.Exit(0);
    }
}
