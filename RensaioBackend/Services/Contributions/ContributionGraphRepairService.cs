using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RensaioBackend.Data;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Models.Database;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// Structural repair for the local contribution graph (<c>contributor.db</c>).
///
/// The graph is title-first: a <see cref="SeriesEntity"/> is only meaningful when it owns
/// content. Its only referencing tables are <c>SeriesTitles</c> (title membership),
/// <c>SeriesEntries</c> (source records) and <c>Metadata</c> (provider links). A
/// <see cref="SeriesEntity"/> whose Id appears in NEITHER <c>SeriesEntries</c> NOR
/// <c>Metadata</c> is an empty aggregate produced by a previous merge bug; it must be removed
/// together with its <c>SeriesTitles</c> associations. Otherwise every title it (wrongly) links
/// keeps resolving new work onto that dead series — the "26k titles on one series" cluster.
///
/// <see cref="TitleEntity"/> rows are NEVER deleted: titles are global and reusable, and may be
/// legitimately re-associated by a future sweep or a cloud import.
///
/// Idempotent; safe to run only while the contribution write paths are idle (the caller holds
/// the shared <see cref="ContributionDbGate"/>). Best-effort: never throws into the caller.
/// </summary>
public sealed class ContributionGraphRepairService
{
    private readonly ContributionDbContext _contributorDb;
    private readonly ContributionTitleHeuristicsService _titleHeuristics;
    private readonly ILogger<ContributionGraphRepairService> _logger;

    public ContributionGraphRepairService(
        ContributionDbContext contributorDb,
        ContributionTitleHeuristicsService titleHeuristics,
        ILogger<ContributionGraphRepairService> logger)
    {
        _contributorDb = contributorDb;
        _titleHeuristics = titleHeuristics;
        _logger = logger;
    }

    /// <summary>
    /// Removes every orphaned series and its title associations. Returns the number of series
    /// removed.
    /// </summary>
    public async Task<int> RepairAsync(CancellationToken token = default)
    {
        try
        {
            // Normalize/consolidate series identity first: re-key any legacy (package:sourceId:url)
            // row onto the canonical (package:url) id and (re)build the SourceSeries associations.
            // Idempotent — a cheap no-op once the graph is normalized. This is what heals legacy
            // rows (re)introduced by a cloud import that predates the normalization.
            await ContributionSourceSeriesMigrator.RunAsync(_contributorDb, _logger, token).ConfigureAwait(false);

            await HealStaleSourceCursorsAsync(token).ConfigureAwait(false);

            // Legacy hard blocks: an empty-key Blocked row is no longer meaningful (blocks are
            // id-scoped — a keyless block matches nothing and cannot be unblocked by key). Convert
            // each to an Unmatched placeholder so the provider is re-searchable.
            int hardBlocksCleared = await _contributorDb.Metadata
                .Where(m => m.MappingStatus == SeriesMappingStatus.Blocked
                    && (m.ProviderKey == null || m.ProviderKey == "")
                    && m.Version != ContributionVersions.PendingDelete
                    && m.Version != ContributionVersions.UploadedDelete)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.MappingStatus, SeriesMappingStatus.Unmatched), token)
                .ConfigureAwait(false);
            if (hardBlocksCleared > 0)
                _logger.LogWarning(
                    "Contribution graph repair: cleared {Count} legacy empty-key Blocked row(s) to Unmatched.",
                    hardBlocksCleared);
            // Orphan = referenced by NO SeriesEntries row AND NO Metadata row. Computed on the
            // committed database only (a legitimately transient series is always given its entry
            // in the same unit of work, so it is never orphaned here).
            var orphanIds = await _contributorDb.Series
                .Where(s => !_contributorDb.SeriesEntries.Any(e => e.SeriesId == s.Id)
                    && !_contributorDb.Metadata.Any(m => m.SeriesId == s.Id))
                .Select(s => s.Id)
                .ToListAsync(token).ConfigureAwait(false);

            if (orphanIds.Count == 0)
            {
                _logger.LogInformation("Contribution graph repair: no orphaned series found.");
                return 0;
            }

            // Remove their title associations first (explicitly, not via FK cascade), then the
            // series rows. Titles themselves are never touched.
            int titlesRemoved = await _contributorDb.SeriesTitles
                .Where(mt => orphanIds.Contains(mt.SeriesId))
                .ExecuteDeleteAsync(token).ConfigureAwait(false);

            int seriesRemoved = await _contributorDb.Series
                .Where(s => orphanIds.Contains(s.Id))
                .ExecuteDeleteAsync(token).ConfigureAwait(false);

            _logger.LogInformation(
                "Contribution graph repair: removed {Series} orphaned series and {Titles} stale title association(s).",
                seriesRemoved, titlesRemoved);

            // No cached index exists any more: the reconcile keep set is computed per-series
            // directly from contributor.db (live source entries + Auto/User ProviderTitleIds), so
            // removing associations here cannot leave a stale keep set behind.

            return seriesRemoved;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Contribution graph repair failed");
            return 0;
        }
    }
    /// <summary>
    /// Heals sources whose <c>LatestIds</c> baseline is set but which have NO live series entries.
    /// That state means the entries were lost (e.g. the source-row cascade bug) while the cursor
    /// survived; clearing the cursor forces the next sweep down the FULL GetPopular path so the
    /// entries are rebuilt. Returns the number of sources healed.
    /// </summary>
    private async Task<int> HealStaleSourceCursorsAsync(CancellationToken token)
    {
        var sources = await _contributorDb.Sources
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        if (sources.Count == 0) return 0;

        var liveSourceIds = (await _contributorDb.SourceSeries
            .Where(ss => ss.Version != ContributionVersions.PendingDelete
                && ss.Version != ContributionVersions.UploadedDelete)
            .Select(ss => ss.SourceId)
            .Distinct()
            .ToListAsync(token).ConfigureAwait(false))
            .ToHashSet();

        int healed = 0;
        foreach (var s in sources)
        {
            if (s.LatestIds.Count == 0 || liveSourceIds.Contains(s.Id)) continue;

            var tracked = await _contributorDb.Sources
                .FirstAsync(x => x.Id == s.Id, token).ConfigureAwait(false);
            tracked.LatestIds = [];
            tracked.LastBatchExecutionUTC = null;
            _contributorDb.Entry(tracked).Property(x => x.LatestIds).IsModified = true;
            _contributorDb.Entry(tracked).Property(x => x.LastBatchExecutionUTC).IsModified = true;
            healed++;
        }

        if (healed > 0)
        {
            await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
            _logger.LogWarning(
                "Contribution graph repair: reset the GetLatest cursor on {Count} source(s) that had " +
                "a baseline but no live series entries; the next sweep will run a full pass.",
                healed);
        }
        return healed;
    }

    /// <summary>
    /// Recomputes the DERIVED <see cref="ContributionSeriesEntity.Metadata"/> flag for EVERY live
    /// series entry from the current graph: <c>true</c> when the owning series has at least one
    /// LIVE <see cref="ContributionMetadataEntity"/> row (any status: a real link or an empty-key
    /// Unmatched placeholder), <c>false</c> otherwise.
    ///
    /// Idempotent and set-based (no per-row loop). <c>Metadata = true</c> means "a metadata pass
    /// was already attempted for this series" — it NEVER means "matched" (that still requires an
    /// AutoMatched/UserConfirmed <c>MappingStatus</c>).
    ///
    /// Rows whose series was changed in THIS unit of work are skipped (dirty guard) so an
    /// in-flight write is never clobbered. Best-effort: never throws into the caller.
    /// Returns the number of rows whose flag changed.
    /// </summary>
    public async Task<int> RecomputeSeriesMetadataFlagsAsync(CancellationToken token = default)
        => await RecomputeSeriesMetadataFlagsCoreAsync(null, token).ConfigureAwait(false);

    /// <summary>
    /// Scoped variant of <see cref="RecomputeSeriesMetadataFlagsAsync(CancellationToken)"/>: only
    /// the supplied series ids are recomputed. Used by the two-phase sweep after each pass to
    /// settle the flag for just the series it touched (a merge folds several series into one, so
    /// the whole survivor group is passed here).
    /// </summary>
    public async Task<int> RecomputeSeriesMetadataFlagsAsync(
        IReadOnlyCollection<Guid> seriesIds, CancellationToken token = default)
        => await RecomputeSeriesMetadataFlagsCoreAsync(seriesIds, token).ConfigureAwait(false);

    private async Task<int> RecomputeSeriesMetadataFlagsCoreAsync(
        IReadOnlyCollection<Guid>? seriesIds, CancellationToken token)
    {
        try
        {
            var scope = seriesIds is null ? null : seriesIds.ToHashSet();

            // Series that carry at least one LIVE metadata row. When a scoped set is supplied
            // (the per-merge/split settle path) the probe is bounded to those series ids: the
            // graph-wide version scan was the dominant cost of every join/split, since it
            // re-read the ENTIRE Metadata table just to settle a handful of entries.
            var coveredQuery = _contributorDb.Metadata
                .Where(m => m.Version != ContributionVersions.PendingDelete
                    && m.Version != ContributionVersions.UploadedDelete);
            if (scope is not null)
                coveredQuery = coveredQuery.Where(m => scope.Contains(m.SeriesId));
            var covered = await coveredQuery
                .Select(m => m.SeriesId)
                .Distinct()
                .ToListAsync(token).ConfigureAwait(false);
            var coveredIds = covered.ToHashSet();

            // Dirty guard: series changed in this unit of work are left untouched.
            var dirty = _contributorDb.ChangeTracker.Entries<ContributionSeriesEntity>()
                .Where(e => e.State is Microsoft.EntityFrameworkCore.EntityState.Added
                    or Microsoft.EntityFrameworkCore.EntityState.Modified)
                .Select(e => e.Entity.SeriesId)
                .ToHashSet();

            bool InScope(Guid id) => (scope is null || scope.Contains(id)) && !dirty.Contains(id);

            var trueIds = coveredIds.Where(InScope).ToList();

            // Series currently flagged true that have NO live metadata row (they must flip to
            // false). Scoped the same way so the per-merge/split settle never scans the graph.
            var flaggedQuery = _contributorDb.SeriesEntries
                .Where(e => e.Metadata
                    && e.Version != ContributionVersions.PendingDelete
                    && e.Version != ContributionVersions.UploadedDelete);
            if (scope is not null)
                flaggedQuery = flaggedQuery.Where(e => scope.Contains(e.SeriesId));
            var falseIds = await flaggedQuery
                .Select(e => e.SeriesId)
                .Distinct()
                .ToListAsync(token).ConfigureAwait(false);
            falseIds = falseIds.Where(id => !coveredIds.Contains(id) && InScope(id)).ToList();

            int changed = 0;

            if (trueIds.Count > 0)
            {
                changed += await _contributorDb.SeriesEntries
                    .Where(e => trueIds.Contains(e.SeriesId) && !e.Metadata)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.Metadata, true), token)
                    .ConfigureAwait(false);
            }

            if (falseIds.Count > 0)
            {
                changed += await _contributorDb.SeriesEntries
                    .Where(e => falseIds.Contains(e.SeriesId) && e.Metadata)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.Metadata, false), token)
                    .ConfigureAwait(false);
            }

            if (changed > 0)
                _logger.LogInformation(
                    "Contribution metadata-flag recompute: {Changed} series-entry row(s) updated ({True} covered, {False} uncovered).",
                    changed, trueIds.Count, falseIds.Count);

            return changed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Contribution metadata-flag recompute failed");
            return 0;
        }
    }
}
