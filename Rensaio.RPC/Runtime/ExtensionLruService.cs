using Microsoft.Extensions.Options;
using Mihon.ExtensionsBridge.Core.Abstractions;
using Rensaio.RPC.Configuration;

namespace Rensaio.RPC.Runtime;

/// <summary>
/// Drives the extension LRU policy inside the RPC process.
///
/// The heavy lifting lives in
/// <see cref="IInternalExtensionManager.SweepIdleInteropsAsync(TimeSpan, int)"/>,
/// which already evicts idle interops and enforces a cap using a
/// reference-counted gate that drains in-flight calls before unloading. This
/// service only supplies the configured thresholds on a timer and exposes the
/// resulting telemetry. No Mihon/IKVM locking is modified.
/// </summary>
public sealed class ExtensionLruService : BackgroundService
{
    private readonly IInternalExtensionManager _extensions;
    private readonly LruOptions _options;
    private readonly ILogger<ExtensionLruService> _logger;

    public ExtensionLruService(
        IInternalExtensionManager extensions,
        IOptions<RpcOptions> options,
        ILogger<ExtensionLruService> logger)
    {
        _extensions = extensions;
        _options = options.Value.Lru;
        _logger = logger;
    }

    /// <summary>Effective cap currently in force.</summary>
    public int Capacity => _options.MaxLoadedExtensions;

    /// <summary>Current loaded/eviction snapshot for the status endpoint.</summary>
    public InteropCacheStats GetStats() => _extensions.GetInteropCacheStats();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(30, _options.SweepIntervalSeconds));
        var idle = TimeSpan.FromMinutes(Math.Max(1, _options.IdleUnloadMinutes));
        int cap = Math.Max(1, _options.MaxLoadedExtensions);

        _logger.LogInformation(
            "Extension LRU policy active: max {Cap} loaded, idle unload after {Idle} min, sweep every {Interval}.",
            cap, idle.TotalMinutes, interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            try
            {
                await _extensions.SweepIdleInteropsAsync(idle, cap).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LRU sweep failed (best-effort, continuing).");
            }
        }
    }
}
