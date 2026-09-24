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
///   * A mapping is resolved by title overlap (preferring the mapping that already links
///     the main title) and created only when none exists.
///   * Source identity is the deterministic (package + sourceId) key.
///   * Series identity is the deterministic (package + sourceId + url) key.
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
    /// Resolves the mapping that owns these titles, or creates a new one. Prefers the
    /// mapping already linking the main title, then any mapping linked to one of the
    /// titles, then a fresh mapping.
    /// </summary>
    public async Task<Guid> ResolveMappingAsync(
        Dictionary<string, Guid> titleIds, string mainTitle, CancellationToken token)
    {
        var seriesTitleIds = titleIds.Values.Distinct().ToList();
        if (seriesTitleIds.Count == 0)
            return CreateMapping().Id;

        var mainId = titleIds.TryGetValue(TitleEntity.Normalize(mainTitle), out var resolvedMain)
            ? resolvedMain
            : (Guid?)null;

        // Persisted links.
        var mappingTitles = await _db.MappingTitles
            .Where(mt => seriesTitleIds.Contains(mt.TitleId))
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        // Links created earlier in this unit of work (e.g. by an adjacent series in the
        // same sweep batch) — otherwise each series would mint its own mapping.
        var trackedLinks = _db.ChangeTracker.Entries<MappingTitleEntity>()
            .Select(e => e.Entity)
            .ToList();

        IEnumerable<MappingTitleEntity> allLinks = mappingTitles.Concat(trackedLinks);

        if (mainId.HasValue)
        {
            var viaMain = allLinks.FirstOrDefault(mt => mt.TitleId == mainId.Value)?.MappingId;
            if (viaMain.HasValue && viaMain.Value != Guid.Empty)
                return viaMain.Value;
        }

        var viaAny = allLinks.Select(mt => mt.MappingId).FirstOrDefault(id => id != Guid.Empty);
        if (viaAny != Guid.Empty)
            return viaAny;

        return CreateMapping().Id;
    }

    private MappingEntity CreateMapping()
    {
        var created = new MappingEntity { Id = Guid.NewGuid() };
        _db.Mappings.Add(created);
        return created;
    }

    /// <summary>Links every resolved title to the mapping, tombstoning stale links.</summary>
    public async Task SyncMappingTitlesAsync(
        Guid mappingId, IEnumerable<Guid> titleIds, CancellationToken token)
    {
        var desired = titleIds.Distinct().ToHashSet();

        var existingRows = await _db.MappingTitles
            .Where(mt => mt.MappingId == mappingId)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        // Rows created earlier in this unit of work for the same mapping.
        var trackedRows = _db.ChangeTracker.Entries<MappingTitleEntity>()
            .Where(e => e.Entity.MappingId == mappingId)
            .Select(e => e.Entity)
            .ToList();

        // Titles already linked to this mapping, from either source.
        var linkedTitleIds = existingRows.Select(mt => mt.TitleId).ToHashSet();
        linkedTitleIds.UnionWith(trackedRows.Select(mt => mt.TitleId));

        foreach (var titleId in desired)
        {
            if (!linkedTitleIds.Contains(titleId))
            {
                _db.MappingTitles.Add(new MappingTitleEntity
                {
                    MappingId = mappingId,
                    TitleId = titleId,
                    Version = VersionAddOrUpdate
                });
                linkedTitleIds.Add(titleId);
            }
        }

        // Tombstone persisted links that are no longer desired. Only persisted rows are
        // touched: a row added in this unit of work is already correct.
        foreach (var row in existingRows)
        {
            if (desired.Contains(row.TitleId) || row.Version == VersionLogicalDelete)
                continue;

            // The row may already be tracked: it was attached by an earlier
            // SyncMappingTitlesAsync call for this same mapping in this unit of work, or
            // added by the propagation path. Because the query above is AsNoTracking,
            // `row` is a fresh copy in that case, and attaching it would throw an
            // identity conflict for the (MappingId, TitleId) key. Reuse the tracked
            // instance instead.
            var tracked = FindTracked<MappingTitleEntity>(
                e => e.MappingId == row.MappingId && e.TitleId == row.TitleId);

            if (tracked != null)
            {
                if (tracked.Version != VersionLogicalDelete)
                {
                    tracked.Version = VersionLogicalDelete;
                    _db.Entry(tracked).Property(mt => mt.Version).IsModified = true;
                }
                continue;
            }

            _db.MappingTitles.Attach(row);
            row.Version = VersionLogicalDelete;
            _db.Entry(row).Property(mt => mt.Version).IsModified = true;
        }
    }

    /// <summary>
    /// Ensures the canonical source definition exists (keyed by package + sourceId) with the
    /// supplied batch cursor. Returns the deterministic source id.
    /// </summary>
    public async Task<Guid> EnsureSourceAsync(
        string package, long sourceId, string sourceName, string sourceLanguage,
        DateTime? lastBatchUtc, CancellationToken token)
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
                SourceId = sourceId,
                SourceName = desiredName,
                SourceLanguage = desiredLanguage,
                LastBatchExecutionUTC = lastBatchUtc,
                Version = VersionAddOrUpdate
            });
            return id;
        }

        // The display fields and the batch cursor are all init-only, so an update is
        // expressed by replacing the row. Because the key is unchanged, the replacement
        // is still a single UPDATE on SaveChanges rather than an INSERT.
        if (!string.Equals(row.SourceName, desiredName, StringComparison.Ordinal)
            || !string.Equals(row.SourceLanguage, desiredLanguage, StringComparison.Ordinal)
            || row.LastBatchExecutionUTC != lastBatchUtc)
        {
            _db.Sources.Remove(row);
            _db.Sources.Add(new ContributionSourceEntity
            {
                Id = id,
                Package = package,
                SourceId = sourceId,
                SourceName = desiredName,
                SourceLanguage = desiredLanguage,
                LastBatchExecutionUTC = lastBatchUtc,
                Version = VersionAddOrUpdate
            });
        }

        return id;
    }

    /// <summary>
    /// Upserts the (mapping, source, url) series record. Updates in place when the payload
    /// changed or the row was tombstoned; inserts otherwise.
    /// </summary>
    public async Task UpsertSeriesRecordAsync(
        Guid mappingId, Guid sourceEntityId, string package, long sourceNumericId, string url,
        ContributionRecordV1 record, CancellationToken token)
    {
        var id = ContributionSeriesEntity.DeriveId(package, sourceNumericId, url);

        // Check the tracker first: a series fetched on the Popular page and again on the
        // Latest page maps to the SAME id, so the second visit must update, not insert.
        var row = FindTracked<ContributionSeriesEntity>(e => e.Id == id);
        row ??= await _db.Series
            .FirstOrDefaultAsync(s => s.Id == id, token).ConfigureAwait(false);

        if (row == null)
        {
            _db.Series.Add(new ContributionSeriesEntity
            {
                Id = id,
                MappingId = mappingId,
                SourceId = sourceEntityId,
                Data = record,
                Version = VersionAddOrUpdate
            });
            return;
        }

        // Resurrect / update when tombstoned or when the payload actually changed.
        var currentJson = System.Text.Json.JsonSerializer.Serialize(row.Data);
        var nextJson = System.Text.Json.JsonSerializer.Serialize(record);
        if (row.Version == VersionLogicalDelete || !string.Equals(currentJson, nextJson, StringComparison.Ordinal))
        {
            row.Data = record;
            row.SourceId = sourceEntityId;
            row.Version = VersionAddOrUpdate;
            _db.Entry(row).Property(s => s.Data).IsModified = true;
            _db.Entry(row).Property(s => s.SourceId).IsModified = true;
            _db.Entry(row).Property(s => s.Version).IsModified = true;
        }
    }

    /// <summary>Persists all pending contribution changes.</summary>
    public Task SaveAsync(CancellationToken token) => _db.SaveChangesAsync(token);
}
