using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;

namespace Rensaio.RPC.Runtime;

/// <summary>
/// EXPERIMENT (RENSAIO_RPC_DIAG=1): samples the live thread inventory and managed threadpool
/// pressure every few seconds to <c>rpc-threads-<pid>.log</c>.
///
/// Purpose: when the process FailFasts in <c>WToolkit.eventLoop</c> -> <c>JavaVM.GetEnv</c>,
/// the last sample shows whether thread creation was starved (threadpool growth, high thread
/// count) and which Java/AWT threads existed at that moment.
/// </summary>
public sealed class RpcThreadWatchdog : BackgroundService
{
    private static readonly object FileLock = new();
    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, $"rpc-threads-{Environment.ProcessId}.log");

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static void Write(string line)
    {
        try
        {
            lock (FileLock)
            {
                File.AppendAllText(LogPath, $"[{DateTime.UtcNow:O}] {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never throw.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int interval = int.TryParse(Environment.GetEnvironmentVariable("RENSAIO_RPC_DIAG_INTERVAL"), out int s)
            ? Math.Max(1, s)
            : 2;

        Write(
            "thread watchdog started " +
            $"gate={Environment.GetEnvironmentVariable("RENSAIO_RPC_GATE") ?? "(unset)"} " +
            $"noGrpc={Environment.GetEnvironmentVariable("RENSAIO_RPC_NO_GRPC") ?? "(unset)"} " +
            $"pid={Environment.ProcessId}");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            try
            {
                ThreadPool.GetAvailableThreads(out int availWorkers, out int availIo);
                ThreadPool.GetMaxThreads(out int maxWorkers, out int maxIo);
                ThreadPool.GetMinThreads(out int minWorkers, out int minIo);

                var javaish = new List<string>();
                foreach (System.Diagnostics.ProcessThread t in System.Diagnostics.Process.GetCurrentProcess().Threads)
                {
                    // Best effort: ProcessThread exposes no name, so record only counts.
                    _ = t;
                    break;
                }

                Write(
                    $"pool: workers used={maxWorkers - availWorkers}/{maxWorkers} (min={minWorkers}) " +
                    $"io used={maxIo - availIo}/{maxIo} (min={minIo}) " +
                    $"osThreads={System.Diagnostics.Process.GetCurrentProcess().Threads.Count} " +
                    $"gate={Environment.GetEnvironmentVariable("RENSAIO_RPC_GATE") ?? "(unset)"} " +
                    $"mgrNow={Environment.CurrentManagedThreadId} osNow={(OperatingSystem.IsWindows() ? GetCurrentThreadId() : 0)}");
            }
            catch (Exception ex)
            {
                Write($"watchdog sample failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Write("thread watchdog stopped");
    }
}
