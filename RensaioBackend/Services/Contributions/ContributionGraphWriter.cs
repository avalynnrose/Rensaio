using RensaioBackend.Data;
using RensaioBackend.Models.ContributionDatabase;
using Microsoft.EntityFrameworkCore;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// Low-level writer for the local contribution graph (<c>contributor.db</c> only).
///
/// It owns the identity/merge rules that must be identical everywhere a contribution row
/// is produced:
///   * Title identity is deterministic (MD5(Normalize(title)) → Guid), so the same title
///     from any extension collapses onto one row.
///   * A series is resolved by title overlap (preferring the series that already links
///     the main title) and created only when none exists.
///   * Source identity is the deterministic (package + sourceId) key.
///   * Series-entry identity is the deterministic (package + sourceId + url) key.
///
/// IMPORTANT — idempotency within a unit of work: a sweep performs many of these calls
/// before a single <see cref="SaveAsync"/>. A query alone is therefore NOT enough to
/// decide whether a row exists: rows added earlier in the same unit of work are only
/// visible in the change tracker, not in the database. Every create path here checks
/// <see cref="DbContext.ChangeTracker"/> too (via <c>FindTracked</c>), otherwise a second
/// call would try to track a duplicate key and EF would throw
/// "another instance with the same key value ... is already being tracked".
///
/// Deliberately does NOT touch rensaio.db (<see cref="AppDbContext"/>): the seed sweep must
/// never write to the library, and only contribution rows are produced here.
/// </summary>
public sealed class ContributionGraphWriter
{
    private const int VersionAddOrUpdate = 0;
    private const int VersionLogicalDelete = -1;

    private readonly ContributionDbContext _db;

    public ContributionGraphWriter(ContributionDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Upserts the many-to-many <c>SourceSeries</c> association for a (source, series entry) pair,
    /// reviving a tombstoned row instead of inserting a duplicate that would violate the composite
    /// key. Idempotent within a unit of work (checks the change tracker first).
    /// </summary>
    public async Task EnsureSourceSeriesAsync(Guid sourceEntityId, Guid seriesEntityId, CancellationToken token)
    {
        var row = FindTracked<ContributionSourceSeriesEntity>(
            e => e.SourceId == sourceEntityId && e.SeriesEntityId == seriesEntityId);
        row ??= await _db.SourceSeries
            .FirstOrDefaultAsync(ss => ss.SourceId == sourceEntityId && ss.SeriesEntityId == seriesEntityId, token)
            .ConfigureAwait(false);

        if (row == null)
        {
            _db.SourceSeries.Add(new ContributionSourceSeriesEntity
            {
                SourceId = sourceEntityId,
                SeriesEntityId = seriesEntityId,
                Version = VersionAddOrUpdate
            });
            return;
        }

        if (ContributionVersions.IsDeleted(row.Version))
        {
            row.Version = VersionAddOrUpdate;
            _db.Entry(row).Property(ss => ss.Version).IsModified = true;
        }
    }

    /// <summary>
    /// Retires the <c>SourceSeries</c> association for a (source, series entry) pair when it exists,
    /// leaving an already-tombstoned row untouched. Used when a source stops contributing an entry.
    /// </summary>
    public async Task RetireSourceSeriesAsync(Guid sourceEntityId, Guid seriesEntityId, CancellationToken token)
    {
        var row = FindTracked<ContributionSourceSeriesEntity>(
            e => e.SourceId == sourceEntityId && e.SeriesEntityId == seriesEntityId);
        row ??= await _db.SourceSeries
            .FirstOrDefaultAsync(ss => ss.SourceId == sourceEntityId && ss.SeriesEntityId == seriesEntityId, token)
            .ConfigureAwait(false);

        if (row == null || ContributionVersions.IsDeleted(row.Version))
            return;

        row.Version = VersionLogicalDelete;
        _db.Entry(row).Property(ss => ss.Version).IsModified = true;
    }

    /// <summary>
    /// Returns the already-tracked instance whose key matches, regardless of state
    /// (Added/Modified/Unchanged/Deleted), or null when the entity is not tracked.
    ///
    /// This is what makes repeated calls in one unit of work safe: newly-<c>Add</c>ed
    /// rows are invisible to a LINQ query but present here, and so are rows the
    /// propagation path attached for a tombstone.
    /// </summary>
    private TEntity? FindTracked<TEntity>(Func<TEntity, bool> predicate) where TEntity : class
        => _db.ChangeTracker.Entries<TEntity>()
            .Select(e => e.Entity)
            .FirstOrDefault(predicate);

    /// <summary>
    /// Resolves/creates TitleEntity rows for the supplied titles. Returns a map of
    /// normalized title → deterministic title id.
    /// </summary>
    public async Task<Dictionary<string, Guid>> ResolveTitlesAsync(
        IEnumerable<string> titles, CancellationToken token)
    {
        var unique = titles
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var desiredIds = new HashSet<Guid>();
        foreach (var title in unique)
            desiredIds.Add(TitleEntity.DeriveId(title));

        // Database hits (authoritative for previously persisted rows) ...
        var existingIds = (await _db.Titles
            .Where(t => desiredIds.Contains(t.Id))
            .AsNoTracking()
            .Select(t => t.Id)
            .ToListAsync(token).ConfigureAwait(false))
            .ToHashSet();

        // ... plus anything already added/tracked in THIS unit of work.
        foreach (var tracked in _db.ChangeTracker.Entries<TitleEntity>().Select(e => e.Entity))
            existingIds.Add(tracked.Id);

        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var title in unique)
        {
            var key = TitleEntity.Normalize(title);
            var id = TitleEntity.DeriveId(title);

            if (!existingIds.Contains(id))
            {
                _db.Titles.Add(new TitleEntity
                {
                    Id = id,
                    Title = title,
                    Version = VersionAddOrUpdate
                });
                existingIds.Add(id);
            }

            result[key] = id;
        }

        return result;
    }

    /// <summary>
    /// Resolves the series that owns these titles, or creates a new one. Prefers the
    /// series already linking the main title, then any series linked to one of the
    /// titles, then a fresh series.
    ///
    /// Returns the resolved series id plus <c>Created</c>, which is <c>true</c> only when NO
    /// existing (live or in-unit-of-work) series association was found and a brand-new
    /// <see cref="SeriesEntity"/> was added. The sweep uses this to decide whether a series is
    /// "new" for the incremental metadata-match gate.
    /// </summary>
    public async Task<(Guid SeriesId, bool Created)> ResolveSeriesAsync(
        Dictionary<string, Guid> titleIds, string mainTitle, CancellationToken token)
    {
        var seriesTitleIds = titleIds.Values.Distinct().ToList();
        if (seriesTitleIds.Count == 0)
            return (CreateSeries().Id, true);

        var mainId = titleIds.TryGetValue(TitleEntity.Normalize(mainTitle), out var resolvedMain)
            ? resolvedMain
            : (Guid?)null;

        // 1. Persisted LIVE links for these titles. A tombstoned link (-1/-3) must NOT
        //    count as a series association: per the model, a title belongs to a series only
        //    through a LIVE SeriesTitles row. Otherwise an orphaned/tombstoned link would
        //    make every new title collapse onto a stale series (the giant-cluster bug).
        var seriesTitles = await _db.SeriesTitles
            .Where(mt => seriesTitleIds.Contains(mt.TitleId)
                && mt.Version != VersionLogicalDelete
                && mt.Version != ContributionVersions.UploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        // 2. Links created earlier in this unit of work (e.g. by an adjacent series in the
        //    same sweep batch) — SCOPED to this series' titles. Without the Where filter the
        //    and LIVE-version check, an UNRELATED tracked link would be picked up by the
        //    `viaAny` fallback below and a brand-new title would be merged onto whatever series
        //    was processed earlier in the unit of work. SaveChangesAsync does not detach, so
        //    this leak was cumulative over the whole sweep.
        var trackedLinks = _db.ChangeTracker.Entries<SeriesTitleEntity>()
            .Select(e => e.Entity)
            .Where(mt => seriesTitleIds.Contains(mt.TitleId) && ContributionVersions.IsLive(mt.Version))
            .ToList();

        IEnumerable<SeriesTitleEntity> allLinks = seriesTitles.Concat(trackedLinks);

        if (mainId.HasValue)
        {
            var viaMain = allLinks.FirstOrDefault(mt => mt.TitleId == mainId.Value)?.SeriesId;
            if (viaMain.HasValue && viaMain.Value != Guid.Empty)
                return (viaMain.Value, false);
        }

        var viaAny = allLinks.Select(mt => mt.SeriesId).FirstOrDefault(id => id != Guid.Empty);
        if (viaAny != Guid.Empty)
            return (viaAny, false);

        return (CreateSeries().Id, true);
    }

    private SeriesEntity CreateSeries()
    {
        var created = new SeriesEntity { Id = Guid.NewGuid() };
        _db.Series.Add(created);
        return created;
    }

    /// <summary>Links every resolved title to the series, tombstoning stale links.</summary>
    public async Task SyncSeriesTitlesAsync(
        Guid seriesId, IEnumerable<Guid> titleIds, CancellationToken token)
    {
        var desired = titleIds.Distinct().ToHashSet();

        var existingRows = await _db.SeriesTitles
            .Where(mt => mt.SeriesId == seriesId)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        // Rows created earlier in this unit of work for the same series.
        var trackedRows = _db.ChangeTracker.Entries<SeriesTitleEntity>()
            .Where(e => e.Entity.SeriesId == seriesId)
            .Select(e => e.Entity)
            .ToList();

        // Titles already ACTIVELY linked to this series (LIVE rows only). A tombstoned
        // link (pending -1 or uploaded -3) must NOT count as linked, otherwise a title
        // that was unlinked could never be re-linked.
        var linkedTitleIds = existingRows
            .Where(mt => ContributionVersions.IsLive(mt.Version))
            .Select(mt => mt.TitleId).ToHashSet();
        linkedTitleIds.UnionWith(trackedRows
            .Where(mt => ContributionVersions.IsLive(mt.Version))
            .Select(mt => mt.TitleId));

        foreach (var titleId in desired)
        {
            if (linkedTitleIds.Contains(titleId))
                continue;

            // Resurrect a tombstoned row for this (series, title) instead of inserting a
            // duplicate that would violate the composite key.
            var deleted = trackedRows.FirstOrDefault(e => e.TitleId == titleId)
                ?? existingRows.FirstOrDefault(mt => mt.TitleId == titleId);
            if (deleted != null)
            {
                _db.SeriesTitles.Attach(deleted);
                deleted.Version = VersionAddOrUpdate;
                _db.Entry(deleted).Property(mt => mt.Version).IsModified = true;
                linkedTitleIds.Add(titleId);
                continue;
            }

            _db.SeriesTitles.Add(new SeriesTitleEntity
            {
                SeriesId = seriesId,
                TitleId = titleId,
                Version = VersionAddOrUpdate
            });
            linkedTitleIds.Add(titleId);
        }

        // Retire persisted links that are no longer desired. Only persisted rows are touched: a
        // row added in this unit of work is already correct. A row that exists in the cloud
        // (Version -2) is TOMBSTONED so the delete propagates; a never-exported row (0) is
        // DELETED locally (no cloud copy to inform). An already-tombstoned row (-1/-3) is left in
        // place — its delete may still be pending export, and a queued delete must not be lost.
        foreach (var row in existingRows)
        {
            if (desired.Contains(row.TitleId) || ContributionVersions.IsDeleted(row.Version))
                continue;

            // The row may already be tracked: it was attached by an earlier
            // SyncSeriesTitlesAsync call for this same series in this unit of work, or
            // added by the propagation path. Because the query above is AsNoTracking,
            // `row` is a fresh copy in that case, and attaching it would throw an
            // identity conflict for the (SeriesId, TitleId) key. Reuse the tracked
            // instance instead.
            var tracked = FindTracked<SeriesTitleEntity>(
                e => e.SeriesId == row.SeriesId && e.TitleId == row.TitleId);

            ContributionRowLifecycle.Retire(_db, tracked ?? row);
        }
    }

    /// <summary>
    /// Ensures the canonical source definition exists (keyed by package + sourceId) with the
    /// supplied batch cursor. Returns the deterministic source id.
    /// </summary>
    public async Task<Guid> EnsureSourceAsync(
        string package, long sourceId, string sourceName, string sourceLanguage,
        DateTime? lastBatchUtc, IReadOnlyList<string>? latestIds, CancellationToken token)
    {
        var id = ContributionSourceEntity.DeriveId(package, sourceId);

        var row = FindTracked<ContributionSourceEntity>(e => e.Id == id);
        row ??= await _db.Sources
            .FirstOrDefaultAsync(s => s.Id == id, token).ConfigureAwait(false);

        string desiredName = string.IsNullOrWhiteSpace(sourceName) ? "Unknown" : sourceName;
        string desiredLanguage = sourceLanguage ?? string.Empty;

        if (row == null)
        {
            _db.Sources.Add(new ContributionSourceEntity
            {
                Id = id,
                Package = package,
                PackageId = ContributionSourceEntity.DerivePackageId(package),
                SourceId = sourceId,
                SourceName = desiredName,
                SourceLanguage = desiredLanguage,
                LastBatchExecutionUTC = lastBatchUtc,
                LatestIds = latestIds?.ToList() ?? [],
                Version = VersionAddOrUpdate
            });
            return id;
        }

        // Update IN PLACE. Do NOT Remove+Add: ContributionSeriesEntity cascades from Sources
        // (OnDelete.Cascade), so marking the tracked source row Deleted would cascade-delete its
        // SeriesEntries — which are tracked in the SAME scoped DbContext used for the whole sweep —
        // wiping the very rows the sweep just wrote (the "sources: []" bug).
        bool changed = false;

        // Backfill PackageId for rows created before the (package-scoped) normalization.
        if (row.PackageId == Guid.Empty)
        {
            row.PackageId = ContributionSourceEntity.DerivePackageId(row.Package);
            _db.Entry(row).Property(x => x.PackageId).IsModified = true;
            changed = true;
        }
        if (!string.Equals(row.SourceName, desiredName, StringComparison.Ordinal))
        {
            row.SourceName = desiredName;
            _db.Entry(row).Property(x => x.SourceName).IsModified = true;
            changed = true;
        }
        if (!string.Equals(row.SourceLanguage, desiredLanguage, StringComparison.Ordinal))
        {
            row.SourceLanguage = desiredLanguage;
            _db.Entry(row).Property(x => x.SourceLanguage).IsModified = true;
            changed = true;
        }
        if (row.LastBatchExecutionUTC != lastBatchUtc)
        {
            row.LastBatchExecutionUTC = lastBatchUtc;
            _db.Entry(row).Property(x => x.LastBatchExecutionUTC).IsModified = true;
            changed = true;
        }
        if (latestIds != null && !row.LatestIds.SequenceEqual(latestIds))
        {
            row.LatestIds = latestIds.ToList();
            _db.Entry(row).Property(x => x.LatestIds).IsModified = true;
            changed = true;
        }
        if (changed)
        {
            row.Version = VersionAddOrUpdate;
            _db.Entry(row).Property(x => x.Version).IsModified = true;
        }
        return id;
    }

    /// <summary>Reads a source row (batch cursor + LatestIds) for the sweep heuristic. Null when absent.</summary>
    public async Task<ContributionSourceEntity?> GetSourceAsync(string package, long sourceId, CancellationToken token)
    {
        var id = ContributionSourceEntity.DeriveId(package, sourceId);
        return await _db.Sources
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Upserts the <c>(series, package, url)</c> series-entry record. Updates in place when the
    /// payload changed or the row was tombstoned; inserts otherwise. Identity is
    /// <c>package:url</c> (source-independent), so a second source of the same package exposing the
    /// same url reuses this row and only adds a <c>SourceSeries</c> association.
    ///
    /// Returns <c>true</c> when a NEW row was inserted, <c>false</c> when an existing row was
    /// updated (including a tombstoned row revived with a fresh payload — the row already existed).
    /// The sweep uses this to distinguish genuinely NEW entries from rows it merely re-wrote, so
    /// its logs do not report an already-covered series as "new" on every pass.
    /// </summary>
    public async Task<bool> UpsertSeriesRecordAsync(
        Guid seriesId, Guid sourceEntityId, string package, string url,
        ContributionRecordV2 record, CancellationToken token)
    {
        var id = ContributionSeriesEntity.DeriveId(package, url);

        // Check the tracker first: a series fetched on the Popular page and again on the
        // Latest page maps to the SAME id, so the second visit must update, not insert.
        var row = FindTracked<ContributionSeriesEntity>(e => e.Id == id);
        row ??= await _db.SeriesEntries
            .FirstOrDefaultAsync(s => s.Id == id, token).ConfigureAwait(false);

        if (row == null)
        {
            _db.SeriesEntries.Add(new ContributionSeriesEntity
            {
                Id = id,
                SeriesId = seriesId,
                PackageId = ContributionSeriesEntity.DerivePackageId(package),
                SourceId = sourceEntityId,
                Data = record,
                Version = VersionAddOrUpdate
            });
            await EnsureSourceSeriesAsync(sourceEntityId, id, token).ConfigureAwait(false);
            return true;
        }

        // The row already exists; make sure this source is associated with it (many-to-many).
        await EnsureSourceSeriesAsync(sourceEntityId, id, token).ConfigureAwait(false);

        // Backfill PackageId / representative SourceId for rows created before the normalization.
        if (row.PackageId == Guid.Empty)
        {
            row.PackageId = ContributionSeriesEntity.DerivePackageId(package);
            _db.Entry(row).Property(s => s.PackageId).IsModified = true;
        }
        if (row.SourceId == Guid.Empty)
        {
            row.SourceId = sourceEntityId;
            _db.Entry(row).Property(s => s.SourceId).IsModified = true;
        }

        // Resurrect / update when tombstoned or when the payload actually changed. The comparison
        // IGNORES the routine LastUpdateUTC stamp: the sweep refreshes it on every re-visit, so a
        // full-payload compare would mark every already-covered series Modified on every page (a
        // rewrite storm whose cost grows with the sweep). A material change on any other field still
        // writes; the fresh stamp is persisted with that write.
        var nextStamp = record.LastUpdateUTC;
        record.LastUpdateUTC = row.Data?.LastUpdateUTC;
        var currentJson = System.Text.Json.JsonSerializer.Serialize(row.Data);
        var nextJson = System.Text.Json.JsonSerializer.Serialize(record);
        record.LastUpdateUTC = nextStamp;
        if (ContributionVersions.IsDeleted(row.Version) || !string.Equals(currentJson, nextJson, StringComparison.Ordinal))
        {
            row.Data = record;
            row.Version = VersionAddOrUpdate;
            _db.Entry(row).Property(s => s.Data).IsModified = true;
            _db.Entry(row).Property(s => s.Version).IsModified = true;
        }

        // The row already existed (even if revived from a tombstone): this is an update, not an insert.
        return false;
    }

    /// <summary>Persists all pending contribution changes.</summary>
    public Task SaveAsync(CancellationToken token) => _db.SaveChangesAsync(token);

    /// <summary>
    /// Suppresses automatic change detection for a batch unit of work (see
    /// <see cref="ContributionDbContext.SuppressAutoDetectChanges"/>). The sweep issues many writes
    /// per page against one scoped context; without this, every call scans the whole change tracker.
    /// </summary>
    public IDisposable SuppressAutoDetectChanges() => _db.SuppressAutoDetectChanges();

    /// <summary>
    /// Detaches every tracked entity. Call after a unit of work is fully persisted to bound the
    /// identity map; otherwise the shared scoped context accumulates every row a long sweep touched
    /// and each subsequent query/save scans a growing tracker.
    /// </summary>
    public void DetachAll() => _db.ChangeTracker.Clear();
}
