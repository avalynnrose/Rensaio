using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RensaioBackend.Data;
using RensaioBackend.Extensions;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Contributions;
using RensaioBackend.Services.Metadata;
using RensaioBackend.Services.Scrobbling.Abstractions;

namespace RensaioBackend.Services.Background;

/// <summary>
/// Single startup orchestrator for the whole contribution/mapping repair pipeline.
///
/// It DELIBERATELY runs the two repair passes SEQUENTIALLY (never in parallel) because they are
/// not independent:
///   * <see cref="MappingConflictRepairService.RepairAsync"/> repairs wrong auto-linkages in
///     rensaio.db, and via the link engine → <see cref="ContributionPropagationService"/> it also
///     writes the contribution database (contributor.db).
///   * <see cref="ContributionTitleHeuristicsService.ReconcileAllAsync"/> then reconciles the
///     contributor.db title associations against the (just-repaired) Auto/User links.
/// Running them concurrently could let a stale reconcile tombstone a title association that a
/// concurrent propagation legitimately just contributed, and causes needless SQLite write
/// contention — so repair must fully finish BEFORE reconciliation starts.
///
/// The whole sequence holds the shared <see cref="ContributionDbGate"/>, so it can never interleave
/// with the contribution import / upload workers either.
///
/// Finally, <see cref="BackfillCoversAsync"/> fills the local-only
/// <see cref="Models.ContributionDatabase.ContributionMetadataEntity.CoverUrl"/> for Auto/User rows
/// that still lack one. The row's replication Version is NOT touched, so the cover is never
/// exported to the cloud worker.
///
/// Best-effort: failures are logged and never crash the host.
/// </summary>
public sealed class ContributionRepairBackgroundService : IWorkerService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContributionRepairBackgroundService> _logger;

    public ContributionRepairBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<ContributionRepairBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Contribution repair background service started.");

        try
        {
            // Let the contribution import worker settle first (it also holds the gate).
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var gate = scope.ServiceProvider.GetRequiredService<ContributionDbGate>();
        var repair = scope.ServiceProvider.GetRequiredService<MappingConflictRepairService>();
        var graphRepair = scope.ServiceProvider.GetRequiredService<ContributionGraphRepairService>();
        var tombstoneCleanup = scope.ServiceProvider.GetRequiredService<ContributionTombstoneCleanupService>();
        var heuristics = scope.ServiceProvider.GetRequiredService<ContributionTitleHeuristicsService>();
        var support = scope.ServiceProvider.GetRequiredService<ExternalMappingSupport>();
        var contributorDb = scope.ServiceProvider.GetRequiredService<ContributionDbContext>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            using (await gate.LockAsync(stoppingToken).ConfigureAwait(false))
            {
                // ── Phase 0: one-shot tombstone cleanup. Reclaims the historical local-only
                //    tombstones (-1/-3) and VACUUMs, so the later passes run over a compact graph.
                //    Guarded by PRAGMA user_version — a no-op after the first successful run. ──
                await tombstoneCleanup.RunOnceAsync(stoppingToken).ConfigureAwait(false);

                // ── Phase 1: repair wrong auto-linkages (rensaio.db — also cascades the
                //    contribution DB through the link engine's propagation). ──
                try
                {
                    var repairResult = await repair.RepairAsync(stoppingToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Mapping conflict repair done: {Conflicts} conflicts, {Blocks} blocks, {Cleared} cleared.",
                        repairResult.ConflictsDetected, repairResult.BlocksCreated, repairResult.MappingsCleared);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Mapping conflict repair failed");
                }

                // ── Phase 1.5: repair the contributor.db graph STRUCTURE. Orphaned series
                //    (no SeriesEntries AND no Metadata) and their title associations are removed
                //    so a dead aggregate can no longer capture new titles. Must precede the
                //    reconcile so the keep set is computed over the cleaned graph. ──
                try
                {
                    var orphansRemoved = await graphRepair.RepairAsync(stoppingToken).ConfigureAwait(false);
                    _logger.LogInformation("Contribution graph repair done: {Removed} orphaned series removed.", orphansRemoved);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Contribution graph repair failed");
                }

                // ── Phase 2: reconcile contributor.db title associations (runs AFTER repair
                //    has fully completed — never in parallel). ──
                var removed = await heuristics.ReconcileAllAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("Contribution title reconciliation done: {Removed} associations removed.", removed);

                // ── Phase 2.5: recompute the DERIVED Metadata flag on every series entry from the
                //    now-repaired graph (true = a live metadata row exists). Runs LAST among the
                //    graph passes so it observes the final topology; the two-phase sweep and every
                //    join/split keep it honest thereafter. ──
                try
                {
                    var flagged = await graphRepair.RecomputeSeriesMetadataFlagsAsync(stoppingToken).ConfigureAwait(false);
                    _logger.LogInformation("Contribution metadata-flag recompute done: {Changed} row(s) updated.", flagged);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Contribution metadata-flag recompute failed");
                }

                // ── Phase 3: backfill provider covers (local-only column). ──
                await BackfillCoversAsync(contributorDb, support, stoppingToken).ConfigureAwait(false);

                // ── Phase 4: backfill the Mihon provider id on image-cache (ETagCache) rows that
                //    lack one. The extension sweep used to register cover URLs without the owning
                //    provider, leaving the URL image provider unable to use the source fallback for
                //    referer/header-protected hosts. The contribution DB now carries the identity,
                //    so empty rows are repaired from it. ──
                await BackfillImageCacheProviderIdsAsync(contributorDb, db, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Contribution repair / reconciliation / cover backfill failed");
        }
    }

    /// <summary>
    /// Fetches and stores provider covers for Auto/User metadata rows that have none yet.
    /// </summary>
    private async Task BackfillCoversAsync(
        ContributionDbContext contributorDb,
        ExternalMappingSupport support,
        CancellationToken token)
    {
        var rows = await contributorDb.Metadata
            .Where(m => m.CoverUrl == null
                && m.ProviderKey != null && m.ProviderKey != string.Empty
                && (m.MappingStatus == SeriesMappingStatus.AutoMatched
                    || m.MappingStatus == SeriesMappingStatus.UserConfirmed))
            .ToListAsync(token).ConfigureAwait(false);

        if (rows.Count == 0) return;

        var providers = support.GetMetadataProvidersInOrder();
        await support.EnsureProvidersAuthenticatedAsync(providers, token).ConfigureAwait(false);
        var byType = providers.ToDictionary(p => p.ProviderType);

        int filled = 0;
        foreach (var row in rows)
        {
            if (token.IsCancellationRequested) break;
            if (!byType.TryGetValue((ExternalSeriesProvider)row.ProviderId, out var provider)) continue;

            try
            {
                var detail = await provider
                    .FetchSeriesMetadataAsync(row.ProviderKey!, token)
                    .ConfigureAwait(false);
                if (detail == null || string.IsNullOrWhiteSpace(detail.CoverUrl)) continue;

                // Persist the LOCAL column only: do NOT modify Version, so this row never enters
                // the pending upload set and the cover is never exported to the cloud worker.
                row.CoverUrl = detail.CoverUrl;
                contributorDb.Entry(row).Property(m => m.CoverUrl).IsModified = true;
                filled++;

                if (filled % 25 == 0)
                    await contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cover backfill failed for provider {Provider} key {Key}",
                    provider.ProviderType, row.ProviderKey);
            }
        }

        if (filled > 0)
        {
            await contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
            _logger.LogInformation("Contribution cover backfill filled {Count} covers.", filled);
        }
    }

    /// <summary>
    /// Backfills <see cref="EtagCacheEntity.MihonProviderId"/> on image-cache rows that have none,
    /// using the cover→provider identity carried by the contribution DB. The owning provider of a
    /// cover URL is resolved by joining the live series entries'
    /// <see cref="Models.ContributionDatabase.ContributionRecordV2.ThumbnailUrl"/> to the entry's
    /// source (package + numeric source id) and formatting it as <c>package|sourceId</c>. Only rows
    /// whose provider id is NULL/empty are touched; the row's image data, etag and version are left
    /// untouched — this is purely the identity the URL image provider needs for its source fallback
    /// on referer/header-protected hosts.
    /// </summary>
    private async Task BackfillImageCacheProviderIdsAsync(
        ContributionDbContext contributorDb, AppDbContext db, CancellationToken token)
    {
        // 1. cover URL → mihon provider id, from the live contribution entries (via the
        //    SourceSeries association — a consolidated entry may have several contributing sources).
        var entries = await contributorDb.SeriesEntries
            .Include(se => se.Sources)
                .ThenInclude(ss => ss.Source)
            .Where(se => se.Version != ContributionVersions.PendingDelete
                && se.Version != ContributionVersions.UploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        var providerByCover = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            string? cover = entry.Data?.ThumbnailUrl;
            if (string.IsNullOrWhiteSpace(cover))
                continue;
            var source = entry.Sources
                .Where(ss => ContributionVersions.IsLive(ss.Version) && ss.Source != null)
                .OrderBy(ss => ss.SourceId)
                .Select(ss => ss.Source!)
                .FirstOrDefault();
            if (source is null)
                continue;
            string? providerId = PackageExtensions.BuildMihonProviderId(source.Package, source.SourceId);
            if (string.IsNullOrWhiteSpace(providerId))
                continue;
            providerByCover.TryAdd(cover, providerId);
        }

        if (providerByCover.Count == 0)
            return;

        // 2. Image-cache rows still missing the provider id.
        var rows = await db.ETagCache
            .Where(e => e.MihonProviderId == null || e.MihonProviderId == string.Empty)
            .ToListAsync(token).ConfigureAwait(false);

        int filled = 0;
        foreach (var row in rows)
        {
            if (token.IsCancellationRequested) break;
            if (!providerByCover.TryGetValue(row.Url, out var providerId)) continue;

            row.MihonProviderId = providerId;
            db.Entry(row).Property(e => e.MihonProviderId).IsModified = true;
            filled++;

            if (filled % 500 == 0)
                await db.SaveChangesAsync(token).ConfigureAwait(false);
        }

        if (filled > 0)
        {
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            _logger.LogInformation("Image cache provider-id backfill filled {Count} rows.", filled);
        }
    }
}