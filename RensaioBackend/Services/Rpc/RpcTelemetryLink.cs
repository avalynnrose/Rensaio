using System.Diagnostics;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rensaio.RPC.Contracts.V1;

namespace RensaioBackend.Services.Rpc;

/// <summary>
/// Bridges the child's telemetry to the backend over gRPC:
///  - <c>StreamLogs</c>: re-emits each RpcLogLine through the backend's Serilog pipeline
///    (replacing the old stdout pipe now that the child is launched detached).
///  - <c>WatchParent</c>: holds a stream open so the child dies with the backend.
///
/// Reconnects on every RPC (re)start via the supervisor's <c>ProcessRestarted</c> event.
/// </summary>
public sealed class RpcTelemetryLink : BackgroundService
{
    private readonly IRpcReadinessGate _gate;
    private readonly ILogger<RpcTelemetryLink> _logger;
    private readonly StdoutLogPipe _logPipe;

    public RpcTelemetryLink(
        IRpcReadinessGate gate, ILoggerFactory loggerFactory, ILogger<RpcTelemetryLink> logger)
    {
        _gate = gate;
        _logger = logger;
        // Reuse the exact stdout-pipe parsing/typing logic to re-emit over Serilog.
        _logPipe = new StdoutLogPipe(loggerFactory.CreateLogger<StdoutLogPipe>(), "RPC");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait until a process incarnation is listening.
                await _gate.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);
                int port = _gate.Port;
                if (port <= 0)
                {
                    await Task.Delay(500, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                GrpcChannel channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
                var client = new ControlService.ControlServiceClient(channel);
                Grpc.Core.Metadata headers = _gate.BuildAuthMetadata();

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

                // Log stream: re-emit through Serilog. Ends when the child exits/restarts.
                Task logs = PumpLogsAsync(client, headers, linked.Token);

                // Advertise the backend as the parent so the child dies with us.
                Task liveness = WatchParentAsync(client, headers, linked.Token);

                // If either stream ends (child died/restarted), reconnect on the new port.
                await Task.WhenAny(logs, liveness).ConfigureAwait(false);
                linked.Cancel();

                await Task.WhenAll(Swallow(logs), Swallow(liveness)).ConfigureAwait(false);
                channel.Dispose();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RPC telemetry link error; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task PumpLogsAsync(
        ControlService.ControlServiceClient client, Grpc.Core.Metadata headers, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using AsyncServerStreamingCall<LogLineMessage> call =
                    client.StreamLogs(new LogStreamRequest { Backlog = 200 }, headers, cancellationToken: token);

                await foreach (LogLineMessage msg in call.ResponseStream.ReadAllAsync(token).ConfigureAwait(false))
                {
                    _logPipe.Emit(msg.Line);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
            {
                // Child gone/restarting — let the outer loop reconnect.
                return;
            }
        }
    }

    private async Task WatchParentAsync(
        ControlService.ControlServiceClient client, Grpc.Core.Metadata headers, CancellationToken token)
    {
        try
        {
            long startTicks = 0;
            try { startTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks; } catch { }

            using AsyncServerStreamingCall<Empty> call = client.WatchParent(
                new WatchParentRequest { ParentPid = Environment.ProcessId, ParentStartUtcTicks = startTicks },
                headers, cancellationToken: token);

            await foreach (Empty _ in call.ResponseStream.ReadAllAsync(token).ConfigureAwait(false))
            {
                // keepalive ticks; nothing to do
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown/restart */ }
        catch (RpcException) { /* child gone/restarting */ }
    }

    private static async Task Swallow(Task t)
    {
        try { await t.ConfigureAwait(false); } catch { /* ignore */ }
    }
}
