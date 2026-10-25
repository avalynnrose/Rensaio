using Microsoft.EntityFrameworkCore;
using RensaioBackend.Data;
using RensaioBackend.Models.ContributionDatabase;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// PART 2 (data) of the SourceSeries normalization. Runs once at startup AFTER the additive
/// migration (<c>AddSourceSeriesAndPackageId</c>) has created the columns/table.
///
/// Identity is now <c>package:url</c>, so several sources of the SAME package exposing the same
/// <c>Data.Url</c> must collapse onto ONE <see cref="ContributionSeriesEntity"/> row linked to
/// every contributing source through <see cref="ContributionSourceSeriesEntity"/>. Grouping by the
/// JSON <c>Data.Url</c> cannot be expressed reliably in SQL, hence this app-level pass.
///
/// Steps:
///   1. Backfill <c>Sources.PackageId</c> (MD5 of the package name).
///   2. For each distinct package, load its live series entries and group them by
///      <c>Data.Url</c> (lowercased).
///   3. Per group: pick a survivor, merge the absorbed series (titles + metadata) into the
///      survivor's series, create one <c>SourceSeries</c> per source in the group, retire the
///      extra entries, and stamp the survivor's <c>PackageId</c>.
///
/// Idempotent: guarded by "every source already has a PackageId AND no per-package url group has
/// more than one live entry", so a re-run is a cheap no-op.
/// </summary>
public static class ContributionSourceSeriesMigrator
{
    private const int VersionAddOrUpdate = ContributionVersions.AddOrUpdate;
    private const int VersionLogicalDelete = ContributionVersions.PendingDelete;

    public static async Task RunAsync(ContributionDbContext db, ILogger logger, CancellationToken token)
    {
        // ── 1. Backfill Sources.PackageId ──
        var sourcesMissingPackage = await db.Sources
            .Where(s => s.PackageId == Guid.Empty)
            .ToListAsync(token).ConfigureAwait(false);
        foreach (var s in sourcesMissingPackage)
        {
            s.PackageId = ContributionSourceEntity.DerivePackageId(s.Package);
            db.Entry(s).Property(x => x.PackageId).IsModified = true;
        }
        if (sourcesMissingPackage.Count > 0)
        {
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            logger.LogInformation(
                "Contribution SourceSeries migration: backfilled PackageId on {Count} source(s).",
                sourcesMissingPackage.Count);
        }

        // Snapshot all sources (package lookup + the per-source association targets).
        var allSources = await db.Sources.AsNoTracking().ToListAsync(token).ConfigureAwait(false);
        if (allSources.Count == 0)
            return;
        var packageBySourceId = allSources.ToDictionary(s => s.Id, s => s.Package);

        int consolidated = 0;

        // ── 2 + 3. Consolidate per package, grouped by url ──
        foreach (var packageGroup in allSources.GroupBy(s => s.Package, StringComparer.Ordinal))
        {
            var sourceIds = packageGroup.Select(s => s.Id).ToHashSet();
            var entries = await db.SeriesEntries
                .Where(e => sourceIds.Contains(e.SourceId)
                    && e.Version != ContributionVersions.PendingDelete
                    && e.Version != ContributionVersions.UploadedDelete)
                .ToListAsync(token).ConfigureAwait(false);
            if (entries.Count == 0)
                continue;

            var byUrl = entries.GroupBy(e => (e.Data?.Url ?? string.Empty).ToLowerInvariant());
            foreach (var urlGroup in byUrl)
            {
                var members = urlGroup.ToList();

                // Canonical (source-independent) id for this (package, url).
                var canonicalId = ContributionSeriesEntity.DeriveId(packageGroup.Key, urlGroup.Key);

                // Idempotent fast-path: the group is already fully normalized when EVERY member is
                // the canonical row and carries a PackageId. This makes repeated runs (startup +
                // post-import + daily repair) cheap no-ops for already-migrated data, while still
                // re-keying a lone LEGACY row (Id != canonicalId).
                if (members.All(m => m.Id == canonicalId && m.PackageId != Guid.Empty))
                    continue;

                // Survivor: prefer a live, non-pending entry; then the lowest id for determinism.
                var survivor = members
                    .OrderByDescending(e => ContributionVersions.IsLive(e.Version))
                    .ThenBy(e => e.Id)
                    .First();

                // The canonical id under the NEW (source-independent) derivation.
                var newId = canonicalId;

                // Merge every OTHER member's series into the survivor's series.
                var absorbedSeriesIds = members
                    .Where(e => e.SeriesId != survivor.SeriesId)
                    .Select(e => e.SeriesId)
                    .Distinct()
                    .ToList();
                if (absorbedSeriesIds.Count > 1)
                    await MergeSeriesIntoAsync(db, survivor.SeriesId, absorbedSeriesIds, token).ConfigureAwait(false);

                // The canonical row must exist under newId. If the survivor already IS the
                // canonical row, just refresh its PackageId; otherwise create a canonical row
                // (copied from the survivor) and retire every LEGACY member row.
                var canonical = await db.SeriesEntries
                    .FirstOrDefaultAsync(e => e.Id == newId, token).ConfigureAwait(false);
                var membersToRetire = members.Where(m => m.Id != newId).ToList();

                if (canonical == null)
                {
                    var survivorTracked = await db.SeriesEntries
                        .FirstAsync(e => e.Id == survivor.Id, token).ConfigureAwait(false);
                    canonical = new ContributionSeriesEntity
                    {
                        Id = newId,
                        SeriesId = survivorTracked.SeriesId,
                        PackageId = ContributionSeriesEntity.DerivePackageId(packageGroup.Key),
                        SourceId = survivorTracked.SourceId,
                        Data = survivorTracked.Data,
                        VerifiedDateUTC = survivorTracked.VerifiedDateUTC,
                        Metadata = survivorTracked.Metadata,
                        Version = VersionAddOrUpdate,
                    };
                    db.SeriesEntries.Add(canonical);
                }
                else if (canonical.PackageId == Guid.Empty)
                {
                    canonical.PackageId = ContributionSeriesEntity.DerivePackageId(packageGroup.Key);
                    db.Entry(canonical).Property(x => x.PackageId).IsModified = true;
                }

                // One association per (source, canonical). The member's OWN legacy SourceId is the
                // authoritative source link for pre-normalization rows.
                var distinctSourceIds = members
                    .Select(m => m.SourceId)
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .ToList();
                foreach (var sourceId in distinctSourceIds)
                {
                    bool exists = await db.SourceSeries
                        .AnyAsync(ss => ss.SourceId == sourceId && ss.SeriesEntityId == newId, token)
                        .ConfigureAwait(false);
                    if (!exists)
                    {
                        db.SourceSeries.Add(new ContributionSourceSeriesEntity
                        {
                            SourceId = sourceId,
                            SeriesEntityId = newId,
                            Version = VersionAddOrUpdate
                        });
                    }
                }

                // Retire every LEGACY member row: cloud-backed rows are tombstoned so the delete
                // propagates; never-exported rows are detached (deleted) locally.
                foreach (var member in membersToRetire)
                {
                    var tracked = await db.SeriesEntries
                        .FirstAsync(e => e.Id == member.Id, token).ConfigureAwait(false);
                    if (member.Version == ContributionVersions.Uploaded
                        || member.Version == ContributionVersions.UploadedDelete)
                    {
                        tracked.Version = VersionLogicalDelete;
                        db.Entry(tracked).Property(x => x.Version).IsModified = true;
                    }
                    else
                    {
                        db.SeriesEntries.Remove(tracked);
                    }
                }

                consolidated++;
            }

            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }

        if (consolidated > 0)
        {
            logger.LogInformation(
                "Contribution SourceSeries migration: consolidated {Count} same-package same-url group(s).",
                consolidated);
        }
    }

    /// <summary>
    /// Folds each absorbed series into <paramref name="targetSeriesId"/>: re-parents any remaining
    /// live series entries, unions title links (tombstoning duplicates) and migrates metadata rows
    /// on (ProviderId, ProviderKey). Mirrors the manual Join semantics so the graph stays coherent.
    /// </summary>
    private static async Task MergeSeriesIntoAsync(
        ContributionDbContext db, Guid targetSeriesId, IReadOnlyCollection<Guid> absorbedSeriesIds,
        CancellationToken token)
    {
        // Ensure the target series row exists.
        if (!await db.Series.AnyAsync(s => s.Id == targetSeriesId, token).ConfigureAwait(false))
            db.Series.Add(new SeriesEntity { Id = targetSeriesId });

        // Titles: union onto the target, retiring duplicates.
        var targetTitles = await db.SeriesTitles
            .Where(mt => mt.SeriesId == targetSeriesId)
            .ToListAsync(token).ConfigureAwait(false);
        var targetTitleIds = targetTitles
            .Where(mt => ContributionVersions.IsLive(mt.Version))
            .Select(mt => mt.TitleId)
            .ToHashSet();
        var targetTitleByTitle = targetTitles.ToDictionary(mt => mt.TitleId);

        var absorbedTitles = await db.SeriesTitles
            .Where(mt => absorbedSeriesIds.Contains(mt.SeriesId))
            .ToListAsync(token).ConfigureAwait(false);
        foreach (var link in absorbedTitles)
        {
            if (!ContributionVersions.IsLive(link.Version)) continue;

            if (targetTitleIds.Add(link.TitleId))
            {
                if (targetTitleByTitle.TryGetValue(link.TitleId, out var existing))
                {
                    existing.Version = VersionAddOrUpdate;
                    db.Entry(existing).Property(x => x.Version).IsModified = true;
                }
                else
                {
                    db.SeriesTitles.Add(new SeriesTitleEntity
                    {
                        SeriesId = targetSeriesId,
                        TitleId = link.TitleId,
                        Version = VersionAddOrUpdate
                    });
                }
            }

            link.Version = VersionLogicalDelete;
            db.Entry(link).Property(x => x.Version).IsModified = true;
        }

        // Metadata: migrate on (ProviderId, ProviderKey), strongest status wins.
        var targetMeta = await db.Metadata
            .Where(m => m.SeriesId == targetSeriesId)
            .ToListAsync(token).ConfigureAwait(false);
        var metaByKey = new Dictionary<(int, string), ContributionMetadataEntity>();
        foreach (var m in targetMeta)
            if (ContributionVersions.IsLive(m.Version))
                metaByKey.TryAdd((m.ProviderId, m.ProviderKey ?? string.Empty), m);

        var absorbedMeta = await db.Metadata
            .Where(m => absorbedSeriesIds.Contains(m.SeriesId))
            .ToListAsync(token).ConfigureAwait(false);
        foreach (var m in absorbedMeta)
        {
            if (!ContributionVersions.IsLive(m.Version)) continue;
            var key = (m.ProviderId, m.ProviderKey ?? string.Empty);
            if (metaByKey.TryGetValue(key, out var existing))
            {
                // Keep the stronger status on the target; tombstone the absorbed row.
                if (Priority(m.MappingStatus) > Priority(existing.MappingStatus))
                {
                    existing.MappingStatus = m.MappingStatus;
                    existing.LinkedDate = m.LinkedDate;
                    existing.Version = VersionAddOrUpdate;
                    db.Entry(existing).Property(x => x.MappingStatus).IsModified = true;
                    db.Entry(existing).Property(x => x.LinkedDate).IsModified = true;
                    db.Entry(existing).Property(x => x.Version).IsModified = true;
                }
                m.Version = VersionLogicalDelete;
                db.Entry(m).Property(x => x.Version).IsModified = true;
            }
            else
            {
                // Re-key the absorbed metadata onto the target series (PK is the metadata Id, so a
                // direct re-parent is safe and preserves the provider link verbatim).
                m.SeriesId = targetSeriesId;
                m.Version = VersionAddOrUpdate;
                db.Entry(m).Property(x => x.SeriesId).IsModified = true;
                db.Entry(m).Property(x => x.Version).IsModified = true;
                metaByKey[key] = m;
            }
        }
    }

    private static int Priority(RensaioBackend.Models.Database.SeriesMappingStatus status) => status switch
    {
        RensaioBackend.Models.Database.SeriesMappingStatus.UserConfirmed => 5,
        RensaioBackend.Models.Database.SeriesMappingStatus.Blocked => 4,
        RensaioBackend.Models.Database.SeriesMappingStatus.AutoMatched => 3,
        RensaioBackend.Models.Database.SeriesMappingStatus.ForeverIgnored => 2,
        RensaioBackend.Models.Database.SeriesMappingStatus.ManualIgnore => 1,
        _ => 0,
    };
}
