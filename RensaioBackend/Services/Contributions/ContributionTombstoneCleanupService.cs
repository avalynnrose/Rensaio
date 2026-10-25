using Microsoft.EntityFrameworkCore;
using RensaioBackend.Data;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Services.Settings;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// One-shot maintenance that reclaims the historical tombstone bloat in the local contributor
/// database. Before the <see cref="ContributionRowLifecycle"/> rule was introduced, EVERY retired
/// row was tombstoned (<see cref="ContributionVersions.PendingDelete"/>) regardless of whether it
/// had ever been exported, so the graph accumulated tens of thousands of local-only tombstones
/// (measured: 61% of <c>SeriesTitles</c>).
///
/// Under the confirmed rule a pending tombstone (-1) is NOT cloud-backed, and an uploaded
/// tombstone (-3) is pure garbage, so both are safe to hard-delete. Any genuinely cloud-backed
/// delete would already have been exported to -3; re-running the upload is handled separately by
/// the normal export, so this pass does not need to flush first.
///
/// Runs at most once, guarded by the SQLite <c>user_version</c> pragma (no schema change). After
/// deleting, it removes orphaned <c>Series</c> aggregates and <c>VACUUM</c>s to physically shrink
/// the file.
/// </summary>
public sealed class ContributionTombstoneCleanupService
{
    /// <summary>
    /// <c>user_version</c> value stamped after a successful pass. Bump to force a re-run.
    /// </summary>
    private const int AppliedUserVersion = 20261011;

    private const int PendingDelete = ContributionVersions.PendingDelete;
    private const int UploadedDelete = ContributionVersions.UploadedDelete;

    private readonly ContributionDbContext _contributorDb;
    private readonly ContributionUploadService _uploadService;
    private readonly SettingsService _settingsService;
    private readonly ILogger<ContributionTombstoneCleanupService> _logger;

    public ContributionTombstoneCleanupService(
        ContributionDbContext contributorDb,
        ContributionUploadService uploadService,
        SettingsService settingsService,
        ILogger<ContributionTombstoneCleanupService> logger)
    {
        _contributorDb = contributorDb;
        _uploadService = uploadService;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// Runs the cleanup once. No-op if it already ran (see <see cref="AppliedUserVersion"/>).
    /// Best-effort: errors are logged, never thrown.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken token = default)
    {
        try
        {
            var userVersion = await GetUserVersionAsync(token).ConfigureAwait(false);
            if (userVersion >= AppliedUserVersion)
                return;

            // ── 0. Safety: flush any queued deletes so cloud-backed tombstones move -1 → -3 and
            //    are unambiguously reclaimable. If contribution is disabled/not verified or the
            //    upload cannot run, SKIP the -1 deletions entirely (only GC -3) so a pending
            //    cloud delete is never silently cancelled. ──
            bool canDeletePending = await TryFlushPendingDeletesAsync(token).ConfigureAwait(false);

            // ── 1. Hard-delete the reclaimable tombstones. Under the confirmed rule -1 is not
            //    cloud-backed (and was just flushed if it ever was) and -3 is an exported delete. ──
            int titlesDeleted = await _contributorDb.SeriesTitles
                .Where(mt => mt.Version == UploadedDelete || (canDeletePending && mt.Version == PendingDelete))
                .ExecuteDeleteAsync(token).ConfigureAwait(false);

            int entriesDeleted = await _contributorDb.SeriesEntries
                .Where(s => s.Version == UploadedDelete || (canDeletePending && s.Version == PendingDelete))
                .ExecuteDeleteAsync(token).ConfigureAwait(false);

            int metadataDeleted = await _contributorDb.Metadata
                .Where(m => m.Version == UploadedDelete || (canDeletePending && m.Version == PendingDelete))
                .ExecuteDeleteAsync(token).ConfigureAwait(false);

            // ── 2. Remove Series aggregates left with no entries and no metadata (orphans created
            //    by the tombstone removals above). Mirrors ContributionGraphRepairService. ──
            var orphanIds = await _contributorDb.Series
                .Where(s => !_contributorDb.SeriesEntries.Any(e => e.SeriesId == s.Id)
                    && !_contributorDb.Metadata.Any(m => m.SeriesId == s.Id))
                .Select(s => s.Id)
                .ToListAsync(token).ConfigureAwait(false);

            int seriesDeleted = 0;
            if (orphanIds.Count > 0)
            {
                // Explicitly remove their remaining title associations first (FK cascade would do
                // it, but being explicit keeps the delete count auditable).
                await _contributorDb.SeriesTitles
                    .Where(mt => orphanIds.Contains(mt.SeriesId))
                    .ExecuteDeleteAsync(token).ConfigureAwait(false);

                seriesDeleted = await _contributorDb.Series
                    .Where(s => orphanIds.Contains(s.Id))
                    .ExecuteDeleteAsync(token).ConfigureAwait(false);
            }

            // ── 3. Stamp the marker BEFORE the (long, non-transactional) VACUUM so a crash during
            //    VACUUM does not repeat the deletes on the next boot. ──
            await SetUserVersionAsync(AppliedUserVersion, token).ConfigureAwait(false);

            // ── 4. Reclaim space and refresh the planner stats for the new index shapes. ──
            await _contributorDb.Database.ExecuteSqlRawAsync("VACUUM;", token).ConfigureAwait(false);
            await _contributorDb.Database.ExecuteSqlRawAsync("PRAGMA optimize;", token).ConfigureAwait(false);

            _logger.LogInformation(
                "Contribution tombstone cleanup: deleted {Titles} title association(s), {Entries} source entry(ies) " +
                "and {Metadata} metadata row(s); removed {Series} orphaned series; vacuumed.",
                titlesDeleted, entriesDeleted, metadataDeleted, seriesDeleted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Contribution tombstone cleanup failed (best-effort; will retry next start).");
        }
    }

    /// <summary>
    /// Attempts to export queued deletes so any cloud-backed pending tombstone advances to
    /// <see cref="ContributionVersions.UploadedDelete"/>. Returns <c>true</c> only when the flush
    /// actually ran (contribution enabled, verified and the upload succeeded) — the caller uses
    /// that to decide whether pending (-1) rows are safe to reclaim. A no-op/disabled/skipped
    /// upload returns <c>false</c>, which makes the caller delete ONLY -3 rows and leave -1 alone.
    /// </summary>
    private async Task<bool> TryFlushPendingDeletesAsync(CancellationToken token)
    {
        try
        {
            var settings = await _settingsService.GetSettingsAsync(token).ConfigureAwait(false);
            if (!settings.ContributionEnabled || !settings.ContributionVerified)
            {
                _logger.LogInformation(
                    "Contribution tombstone cleanup: contribution disabled/unverified — reclaiming only " +
                    "exported tombstones (-3) and preserving pending deletes (-1).");
                return false;
            }

            var result = await _uploadService.UploadPendingChangesAsync(false, token).ConfigureAwait(false);
            if (!result.Success)
            {
                _logger.LogInformation(
                    "Contribution tombstone cleanup: pending-delete flush did not succeed ({Error}); " +
                    "reclaiming only exported tombstones (-3) and preserving pending deletes (-1).",
                    result.Error);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Contribution tombstone cleanup: pending-delete flush failed; reclaiming only exported tombstones (-3).");
            return false;
        }
    }

    private async Task<int> GetUserVersionAsync(CancellationToken token)
    {
        var conn = _contributorDb.Database.GetDbConnection();
        await _contributorDb.Database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
        return result is long l ? (int)l : Convert.ToInt32(result ?? 0);
    }

    private async Task SetUserVersionAsync(int value, CancellationToken token)
    {
        await _contributorDb.Database
            .ExecuteSqlRawAsync($"PRAGMA user_version = {value};", token)
            .ConfigureAwait(false);
    }
}
