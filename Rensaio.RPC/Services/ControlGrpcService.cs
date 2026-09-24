using Grpc.Core;
using Microsoft.Extensions.Hosting;
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
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ControlGrpcService> _logger;

    public ControlGrpcService(
        RpcStartupToken token,
        RpcReadiness readiness,
        ExtensionLruService lru,
        IHostApplicationLifetime lifetime,
        ILogger<ControlGrpcService> logger)
    {
        _token = token;
        _readiness = readiness;
        _lru = lru;
        _lifetime = lifetime;
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
}
