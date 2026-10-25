using RensaioBackend.Data;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Contributions.Abstractions;
using RensaioBackend.Services.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace RensaioBackend.Services.Background;

/// <summary>
/// Background service that reconciles metadata mappings. On each run it links every local series
/// (and, when the in-memory repository has titles, those titles too) that needs attention, using
/// the global <see cref="SeriesMappingEntity.MappingStatus"/>:
///   - No global mapping (no rows yet) or one with a stale/empty external id -> link.
///   - Blocked-with-id (a DIFFERENT id may still be matched) -> link.
///   - Unmatched (settled — a previous pass already searched; no match found), ForeverIgnored,
///     ManualIgnore, hard-blocked (empty-id Blocked) / settled -> skipped (never re-auto-linked).
///   A series with an active link on one provider and an Unmatched row on another is SKIPPED —
///   the matched rows stay, the unmatched row stays settled (SRP: scan new work, not repeats).
/// </summary>
public sealed class MetadataBackgroundScanService : IWorkerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeSpan _interval;
    private readonly ILogger<MetadataBackgroundScanService> _logger;

    public MetadataBackgroundScanService(IServiceScopeFactory scopeFactory,
        ILogger<MetadataBackgroundScanService> logger)
        : this(scopeFactory, logger, TimeSpan.FromHours(24))
    {
    }

    public MetadataBackgroundScanService(IServiceScopeFactory scopeFactory,
        ILogger<MetadataBackgroundScanService> logger, TimeSpan interval)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _interval = interval;
    }

    public async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Metadata background scan started (interval {Interval})", _interval);
        try
        {
            using var timer = new PeriodicTimer(_interval);
            while (true)
            {
                try { await Task.Delay(_interval, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var engine = scope.ServiceProvider.GetRequiredService<MetadataLinkEngine>();
                var repo = scope.ServiceProvider.GetRequiredService<IGlobalMetadataRepository>();
                var repair = scope.ServiceProvider.GetRequiredService<MappingConflictRepairService>();
                await RunOnceAsync(db, engine, repo, repair, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _logger.LogInformation("Metadata background scan stopped");
        }
    }

    /// <summary>
    /// Kicks off a full background scan asynchronously in its own service scope. The engine's
    /// <see cref="MetadataLinkEngine.LinkAllAsync"/> publishes SignalR progress (jobType =
    /// MetadataLink). Returns immediately; the scan runs to completion in the background.
    /// </summary>
    public Task TriggerAsync(CancellationToken token = default)
    {
        return Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var engine = scope.ServiceProvider.GetRequiredService<MetadataLinkEngine>();
                await engine.LinkAllAsync(token).ConfigureAwait(false);
                // NOTE: no post-scan repair. Repair runs only at startup (StartupHostedService)
                // and via the explicit repair endpoint — a scan must never trigger it, and the
                // per-provider pending filter already prevents new conflicting claims.
            }
            catch (OperationCanceledException)
            {
                // Cancelled during shutdown — expected.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background metadata scan failed");
            }
        });
    }

    /// <summary>Runs a single scan pass (usable for manual "scan now" too).</summary>
    public async Task RunOnceAsync(AppDbContext db, MetadataLinkEngine engine,
        IGlobalMetadataRepository repo, MappingConflictRepairService? repair = null,
        CancellationToken token = default)
    {
        var seriesIds = await db.Series.Select(s => s.Id).ToListAsync(token).ConfigureAwait(false);
        var mappings = await db.SeriesMappings.ToListAsync(token).ConfigureAwait(false);

        // Only scan providers that need no auth + providers that are authenticated for at least one
        // user. The background worker has no request user, so "enabled" = any authenticated config.
        var enabledProviders = await engine.GetEnabledProviderSetAsync(token).ConfigureAwait(false);

        var linked = 0;
        var skipped = 0;

        foreach (var seriesId in seriesIds)
        {
            if (token.IsCancellationRequested) break;

            // Only series with GENUINELY NEW work are scanned by the 24h worker: a never-scanned
            // (enabled provider with no row). Series whose only pending state is an Unmatched row
            // are SKIPPED — "Unmatched" means a previous scan already searched and found no match,
            // so re-searching nightly re-triggers the whole-library storm. That is what the
            // explicit Scan-All / per-series Scan do.
            if (!HasNewWork(seriesId, mappings, enabledProviders))
            {
                skipped++;
                continue;
            }

            try
            {
                var result = await engine.LinkSeriesAsync(seriesId, token, enabledProviders).ConfigureAwait(false);
                linked += result.Links.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Metadata scan failed for series {SeriesId}", seriesId);
            }
        }

        var titles = repo.GetAllTitlesAsync(token);
        _logger.LogInformation("Metadata scan pass complete: {Linked} linked, {Skipped} skipped, {Titles} titles in repo",
            linked, skipped, titles.Count);

        // Post-pass repair: detect wrong self-reinforcing linkages and auto-block the losers.
        if (repair != null)
        {
            try
            {
                await repair.RepairAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mapping conflict repair after scan failed");
            }
        }
    }

    /// <summary>
    /// TRUE when the series has a genuinely-new (series, provider) to work on: an enabled provider
    /// with no mapping row. An Unmatched row alone does NOT count as new work — a previous pass
    /// already searched it; the 24h worker must not re-scan unmatched rows every night (only the
    /// explicit Scan-All / per-series Scan do that). ManualIgnore is a settled refusal with no
    /// expiration, so it is likewise never re-scanned.
    /// </summary>
    private static bool HasNewWork(Guid seriesId, List<SeriesMappingEntity> mappings,
        HashSet<ExternalSeriesProvider> enabledProviders)
    {
        var seriesMappings = mappings.Where(m => m.SeriesId == seriesId).ToList();
        var covered = new HashSet<ExternalSeriesProvider>();
        foreach (var m in seriesMappings)
        {
            if (!enabledProviders.Contains(m.Provider)) continue;
            covered.Add(m.Provider);
        }
        // An enabled provider with no row at all = brand-new work.
        return covered.Count < enabledProviders.Count;
    }
}