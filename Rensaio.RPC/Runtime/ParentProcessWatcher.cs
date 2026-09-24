using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rensaio.RPC.Runtime;

/// <summary>
/// Watches the spawning backend and shuts this host down the moment it dies.
///
/// Why this is necessary: the backend normally shuts us down gracefully
/// (ControlService.PrepareShutdown). But when the parent is terminated abruptly —
/// pressing Stop in an IDE debugger, a crash, <c>taskkill /F</c>, <c>kill -9</c> —
/// it cannot send that request, and this child would be orphaned, holding the
/// IKVM/CEF resources indefinitely.
///
/// Two independent signals are watched, so the child never depends on the parent
/// saying goodbye:
///
///   1. stdin EOF (PRIMARY). The supervisor spawns us with stdin redirected and holds
///      the write end open without ever writing. When the parent dies by ANY means,
///      the OS closes that handle and our stdin read returns EOF. This is portable
///      (Windows/macOS/Linux), race-free, and unaffected by PID reuse.
///
///   2. Parent process exit (FALLBACK). Only active when <c>--parentpid</c> was
///      supplied. Weaker: on Linux PIDs recycle quickly, so this can watch the wrong
///      process. It is kept only in case stdin is not actually piped (e.g. a custom
///      launch), and never blocks the primary signal.
///
/// When stdin is not redirected (e.g. running the RPC directly in a terminal) the
/// stdin watch would read from the console and could block or see EOF spuriously, so
/// it is only armed when <c>--watch-parent</c> is passed — which the supervisor does
/// precisely when it has redirected stdin.
/// </summary>
public sealed class ParentProcessWatcher : BackgroundService
{
    /// <summary>How long to allow graceful shutdown before forcing exit.</summary>
    private const int GracefulExitSeconds = 5;

    private readonly ParentProcessId _parentPid;
    private readonly ParentWatchOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ParentProcessWatcher> _logger;

    public ParentProcessWatcher(
        ParentProcessId parentPid,
        ParentWatchOptions options,
        IHostApplicationLifetime lifetime,
        ILogger<ParentProcessWatcher> logger)
    {
        _parentPid = parentPid;
        _options = options;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new List<Task>(2);

        if (_options.WatchStdin)
            tasks.Add(WatchStdinAsync(stoppingToken));

        if (_parentPid.Value is int pid && pid > 0)
            tasks.Add(WatchProcessAsync(pid, stoppingToken));

        if (tasks.Count == 0)
        {
            _logger.LogInformation("No parent-death signal configured; parent watchdog is inert.");
            return;
        }

        // Whichever signal fires first wins; the other keeps running until shutdown.
        await Task.WhenAny(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Primary signal: exits when the inherited stdin pipe reaches EOF, which happens
    /// exactly when the parent's write handle is closed (i.e. when it dies).
    /// </summary>
    private async Task WatchStdinAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Watching the inherited stdin pipe for parent death (EOF).");

        try
        {
            using var stdin = Console.OpenStandardInput();
            var buffer = new byte[1];

            while (!stoppingToken.IsCancellationRequested)
            {
                int read = await stdin.ReadAsync(buffer.AsMemory(0, 1), stoppingToken).ConfigureAwait(false);
                if (read == 0)
                {
                    _logger.LogWarning("stdin reached EOF; the parent process is gone. Shutting down.");
                    await BeginTerminationAsync().ConfigureAwait(false);
                    return;
                }

                // Unexpected input on a watch-only pipe: ignore rather than misinterpret.
                _logger.LogDebug("Ignoring unexpected byte on the parent-watch pipe.");
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            // A broken pipe also means the parent is gone.
            _logger.LogWarning(ex, "stdin watch failed; treating as parent death.");
            await BeginTerminationAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fallback signal: exits when the recorded parent pid terminates. Kept because it
    /// works even when stdin is not piped, and avoids the PID-reuse weakness by only
    /// being used alongside the primary signal.
    /// </summary>
    private async Task WatchProcessAsync(int pid, CancellationToken stoppingToken)
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            _logger.LogWarning("Parent process {Pid} is already gone; shutting down.", pid);
            await BeginTerminationAsync().ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("Also watching parent process {Pid} as a fallback.", pid);

        try
        {
            await parent.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            parent.Dispose();
        }

        _logger.LogWarning("Parent process {Pid} terminated; shutting down.", pid);
        await BeginTerminationAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Requests a graceful stop, then forces exit if the runtime refuses to unwind.
    ///
    /// The escalation is required: the IKVM/CEF runtime holds foreground threads
    /// (JCEF's message pump, KCEF native threads) that can prevent the process from
    /// exiting even after StopApplication. Without it the child can be left alive —
    /// which is the very symptom this watchdog exists to eliminate.
    /// </summary>
    private async Task BeginTerminationAsync()
    {
        _lifetime.StopApplication();

        await Task.Delay(TimeSpan.FromSeconds(GracefulExitSeconds)).ConfigureAwait(false);
        _logger.LogWarning(
            "RPC host did not exit within {Seconds}s of the parent's death; forcing process exit.",
            GracefulExitSeconds);
        Environment.Exit(0);
    }
}

/// <summary>Inline holder for the parent process id so it can be injected.</summary>
public sealed class ParentProcessId
{
    public ParentProcessId(int? value) => Value = value;

    /// <summary>Null when the RPC was started without a parent id.</summary>
    public int? Value { get; }
}

/// <summary>
/// Whether the stdin-EOF watch is armed. The supervisor sets it only when it has
/// actually redirected our stdin, so an interactive/test launch never mistakes a
/// console read for parent death.
/// </summary>
public sealed class ParentWatchOptions
{
    public ParentWatchOptions(bool watchStdin) => WatchStdin = watchStdin;

    public bool WatchStdin { get; }
}
