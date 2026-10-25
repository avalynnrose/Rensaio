using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rensaio.RPC.Contracts;
using Rensaio.RPC.Contracts.V1;

namespace RensaioBackend.Services.Rpc;

/// <summary>
/// Owns the Rensaio.RPC child process lifecycle.
///
/// Runs inside RensaioBackend so it works identically in both deployment modes:
///   * Desktop/tray: the tray starts the backend host; the backend spawns RPC.
///   * Docker: the entrypoint runs the backend as the container main process;
///     the backend spawns RPC inside the same container (inheriting the IKVM
///     LD_LIBRARY_PATH and Xvfb display).
///
/// Responsibilities: locate the RPC executable next to the backend, spawn it
/// with a per-start token and negotiated loopback port (the port comes back on
/// stdout), pipe its logs, auto-restart on unexpected exit with backoff, do a
/// graceful shutdown on stop, and expose a readiness gate callers can await so a
/// call during a restart blocks until the server is back online.
/// </summary>
public sealed class RpcProcessSupervisor : BackgroundService, IRpcReadinessGate, IRpcProcessSupervisor
{
    private readonly ILogger<RpcProcessSupervisor> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeSpan _handshakeTimeout = TimeSpan.FromMinutes(2);

    /// <inheritdoc />
    public event Action? ProcessRestarted;

    /// <inheritdoc />
    public event Action? ProcessStopped;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskCompletionSource _readyTcs = NewTcs();
    private Process? _process;
    private int _port;
    private string _token = string.Empty;
    private volatile bool _stopping;

    public RpcProcessSupervisor(ILogger<RpcProcessSupervisor> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Current loopback port, or 0 when not yet negotiated.</summary>
    public int Port => Volatile.Read(ref _port);

    /// <summary>Per-start token the client must send on every call.</summary>
    public string Token => _token;

    /// <summary>
    /// Completes when RPC is reachable and ready. Callers await this before
    /// issuing calls so a call during a restart blocks instead of failing.
    /// </summary>
    public Task Ready => _readyTcs.Task;

    /// <summary>
    /// Builds a gRPC channel bound to the current port and a call invoker that
    /// injects the per-start token header on every call. Returns null until the
    /// first handshake completes.
    /// </summary>
    public GrpcChannel? TryCreateChannel()
    {
        int port = Port;
        if (port <= 0)
            return null;

        return GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
    }

    /// <summary>gRPC metadata key carrying the per-start token.</summary>
    public const string TokenHeader = "rens-rpc-token";

    /// <summary>Metadata carrying the per-start token on every call.</summary>
    public Grpc.Core.Metadata BuildAuthMetadata()
    {
        var metadata = new Grpc.Core.Metadata();
        if (!string.IsNullOrEmpty(_token))
            metadata.Add(TokenHeader, _token);
        return metadata;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            int attempt = 0;
            while (!stoppingToken.IsCancellationRequested && !_stopping)
            {
                bool ok = await TryStartOnceAsync(stoppingToken).ConfigureAwait(false);
                if (ok)
                    break;

                attempt++;
                TimeSpan backoff = ComputeBackoff(attempt);
                _logger.LogWarning("Rensaio.RPC failed to start (attempt {Attempt}); retrying in {Delay}s.",
                    attempt, backoff.TotalSeconds);
                try
                {
                    await Task.Delay(backoff, stoppingToken).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            // Process is up; wait for it to exit, then decide whether to restart.
            Process? proc = _process;
            if (proc == null)
                break;

            try
            {
                await proc.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_stopping || stoppingToken.IsCancellationRequested)
                break;

            _logger.LogWarning("Rensaio.RPC exited unexpectedly (code {Code}); restarting.", proc.ExitCode);
            ResetReadiness();
            // Tell the client its bridge-readiness gate is stale: the restarted
            // process must re-initialize IKVM/Mihon before calls are valid again.
            try { ProcessRestarted?.Invoke(); } catch { /* listener must not break the loop */ }
        }

        try { ProcessStopped?.Invoke(); } catch { /* listener must not break shutdown */ }
        _logger.LogInformation("RPC process supervisor stopping.");
    }

    /// <summary>
    /// Spawns the process, awaits the handshake, and wires log pumping. Returns
    /// true when the handshake succeeded and the process is alive.
    /// </summary>
    private async Task<bool> TryStartOnceAsync(CancellationToken token)
    {
        string? exePath = LocateRpcExecutable();
        if (exePath == null)
        {
            _logger.LogError("Rensaio.RPC executable not found next to the backend. Expected 'Rensaio.RPC' beside {Base}.",
                AppContext.BaseDirectory);
            return false;
        }

        _token = Guid.NewGuid().ToString("N");
        var handshakeTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Both the tray/backend and the RPC child run from the SAME directory
        // (AppContext.BaseDirectory = where the tray/backend exe lives). The child still runs
        // detached with its own console and NO stdio redirection; readiness is conveyed via a
        // handshake FILE it writes; logs flow back over gRPC (ControlService.StreamLogs).
        string runDir = AppContext.BaseDirectory;
        string handshakeFile = Path.Combine(runDir, "rensaio-rpc-ready.txt");
        try { File.Delete(handshakeFile); } catch { /* best effort */ }

        // Build the argument list; it is handed to ProcessStartInfo.ArgumentList below
        // (UseShellExecute=false), which handles quoting/paths-with-spaces correctly.
        var argList = new List<string>
        {
            $"--token={_token}",
            // Parent-liveness fallback (stdin EOF is gone now). Also drives die-with-parent.
            $"--parentpid={Environment.ProcessId}",
            $"--handshake-file={handshakeFile}",
        };
        if (!string.IsNullOrWhiteSpace(BridgeFolder))
            argList.Add($"--paths:bridge={BridgeFolder}");
        if (!string.IsNullOrWhiteSpace(TempFolder))
            argList.Add($"--paths:temp={TempFolder}");

        var psi = new ProcessStartInfo(exePath)
        {
            // Silent app: never surface a console window. The child is a console-subsystem
            // executable, so if it were launched with a console (or from a parent that has
            // one) Windows would allocate a fresh, visible console. CreateNoWindow only
            // takes effect with UseShellExecute=false, so the shell is disabled here.
            UseShellExecute = false,
            CreateNoWindow = true,
            // Detached/standalone: no stdout/stderr/stdin pipes. Logs flow back over gRPC
            // (ControlService.StreamLogs); readiness is the handshake FILE.
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            RedirectStandardInput = false,
            // Run from the SAME directory as the tray/backend.
            WorkingDirectory = runDir,
        };
        // UseShellExecute=false re-enables ArgumentList, so arguments are passed verbatim
        // (no manual command-line quoting required).
        foreach (string arg in argList)
            psi.ArgumentList.Add(arg);

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Rensaio.RPC.");
            return false;
        }

        _process = proc;

        // Poll the handshake file for readiness (no stdout). Logs come over gRPC.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (File.Exists(handshakeFile))
                    {
                        string text = await File.ReadAllTextAsync(handshakeFile, token).ConfigureAwait(false);
                        if (RpcHandshake.TryParseReadyLine(text.Trim(), out int port, out int proto))
                        {
                            if (proto == RpcHandshake.ProtocolVersion)
                                handshakeTcs.TrySetResult(port);
                            else
                                _logger.LogWarning("RPC protocol mismatch: RPC={Rpc} backend={Backend}.", proto, RpcHandshake.ProtocolVersion);
                            break;
                        }
                    }
                    await Task.Delay(150, token).ConfigureAwait(false);
                }
            }
            catch { /* cancelled / IO */ }
        }, token);

        Task completed = await Task.WhenAny(handshakeTcs.Task, Task.Delay(_handshakeTimeout, token), proc.WaitForExitAsync(token))
            .ConfigureAwait(false);

        if (completed == handshakeTcs.Task)
        {
            int port = await handshakeTcs.Task.ConfigureAwait(false);
            Volatile.Write(ref _port, port);
            _logger.LogInformation("Rensaio.RPC ready on 127.0.0.1:{Port} (pid {Pid}).", port, proc.Id);

            // The backend is now able to build a channel; unblock callers.
            MarkReady();
            return true;
        }

        if (completed == proc.WaitForExitAsync(token))
        {
            _logger.LogError("Rensaio.RPC exited before handshake (code {Code}).", proc.ExitCode);
        }
        else
        {
            _logger.LogError("Rensaio.RPC did not report readiness within {Timeout}.", _handshakeTimeout);
        }

        TryKill(proc);
        return false;
    }

    /// <summary>
    /// Host shutdown hook. BackgroundService.StopAsync cancels the stopping token
    /// first, so ExecuteAsync exits its loop; we then perform the graceful RPC
    /// shutdown (PrepareShutdown, then wait, then kill as a last resort).
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await GracefulShutdownAsync(DefaultShutdownTimeout).ConfigureAwait(false);
        _logger.LogInformation("RPC process supervisor stopped.");
    }

    /// <summary>How long to wait for RPC to exit after a graceful shutdown request.</summary>
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Gracefully stops the RPC process and waits for exit.</summary>
    public async Task GracefulShutdownAsync(TimeSpan timeout)
    {
        _stopping = true;
        Process? proc = _process;
        if (proc == null)
            return;

        try
        {
            int port = Port;
            if (port > 0)
            {
                using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
                var client = new ControlService.ControlServiceClient(channel);
                var headers = BuildAuthMetadata();
                await client.PrepareShutdownAsync(
                    new ShutdownRequest { Reason = "backend stopping" }, headers).ResponseAsync
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graceful RPC shutdown request failed; falling back to process kill.");
        }

        try
        {
            if (!proc.HasExited)
                await proc.WaitForExitAsync(new CancellationTokenSource(timeout).Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("RPC did not exit within {Timeout}; killing.", timeout);
            TryKill(proc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error waiting for RPC exit.");
            TryKill(proc);
        }
    }

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch { /* best effort */ }
    }

    private static TimeSpan ComputeBackoff(int attempt)
    {
        int shift = Math.Min(attempt, 5);
        double seconds = 2 * (1 << shift);
        double jitter = Random.Shared.NextDouble() * 0.5;
        return TimeSpan.FromSeconds(Math.Min(seconds + jitter, 60));
    }

    private static TaskCompletionSource NewTcs()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void MarkReady() => _readyTcs.TrySetResult();

    private void ResetReadiness()
    {
        Volatile.Write(ref _port, 0);
        _readyTcs = NewTcs();
    }

    /// <summary>Extension/temp folders passed through to RPC. Set by DI/host.</summary>
    public string? BridgeFolder { get; set; }

    /// <summary>Temp folder passed through to RPC. Set by DI/host.</summary>
    public string? TempFolder { get; set; }

    /// <summary>
    /// Finds the RPC executable published next to the backend. There is a single child host
    /// (<c>Rensaio.RPC</c>) shared by every deployment (Docker and the desktop tray); the
    /// Avalonia-hosted <c>Rensaio.DesktopRPC</c> variant was removed with JCEF.
    ///
    /// An explicit override wins so the executable location is testable:
    ///   RENSAIO_RPC_EXE=desktop|headless  (or a full/relative executable path).
    /// </summary>
    private string? LocateRpcExecutable()
    {
        string baseDir = AppContext.BaseDirectory;

        // Explicit override: a name token or an explicit path.
        string? overrideValue = Environment.GetEnvironmentVariable("RENSAIO_RPC_EXE");
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            string exeOverride = OperatingSystem.IsWindows() && !overrideValue.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? overrideValue + ".exe"
                : overrideValue;
            var forced = ResolveCandidate(baseDir, exeOverride);
            if (forced != null)
                return forced;
        }

        // Single child host: Rensaio.RPC. No OS branching.
        string name = OperatingSystem.IsWindows() ? "Rensaio.RPC.exe" : "Rensaio.RPC";

        string? found = ResolveCandidate(baseDir, name);
        if (found == null)
        {
            _logger.LogError(
                "RPC executable '{Name}' not found beside the backend ({Base}) or in ./rpc; " +
                "the child will not start.", name, baseDir);
        }
        return found;
    }

    /// <summary>
    /// Resolves an executable by name or explicit path, probing beside the backend and in
    /// the "rpc" subfolder (the layout produced by Directory.Build.targets / build scripts).
    /// </summary>
    private static string? ResolveCandidate(string baseDir, string nameOrPath)
    {
        // Explicit absolute/relative path.
        if (nameOrPath.Contains(Path.DirectorySeparatorChar) || nameOrPath.Contains('/')
            || nameOrPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            // Reject bare ".exe" tokens that are really service names; try as a path first.
            if (Path.IsPathRooted(nameOrPath) && File.Exists(nameOrPath))
                return nameOrPath;
        }

        string candidate = Path.Combine(baseDir, nameOrPath);
        if (File.Exists(candidate))
            return candidate;

        string sub = Path.Combine(baseDir, "rpc", nameOrPath);
        if (File.Exists(sub))
            return sub;

        return null;
    }

}

/// <summary>
/// Readiness surface used by the RPC client so calls during a restart block
/// until the server is back online rather than failing fast.
/// </summary>
public interface IRpcReadinessGate
{
    Task Ready { get; }
    int Port { get; }
    string Token { get; }
    GrpcChannel? TryCreateChannel();
    Grpc.Core.Metadata BuildAuthMetadata();
}

/// <summary>
/// Lifecycle notifications the RPC client subscribes to so it can distinguish a
/// process that is merely LISTENING from one whose bridge is actually initialized,
/// and reset its bridge-readiness gate when the process is replaced.
/// </summary>
public interface IRpcProcessSupervisor
{
    /// <summary>Raised after the process exited unexpectedly and is about to be respawned.</summary>
    event Action ProcessRestarted;

    /// <summary>Raised when the process is being stopped deliberately (host shutdown).</summary>
    event Action ProcessStopped;
}
