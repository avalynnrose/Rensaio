using RensaioBackend.Data;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Models.Database;
using RensaioBackend.Services.Contributions.Snapshot;
using SeriesEntity = RensaioBackend.Models.ContributionDatabase.SeriesEntity;
using RensaioBackend.Services.Settings;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// Downloads the cloud contribution snapshot (GET /snapshot on the worker) and
/// applies it to the local contributor database.
///
/// Identity rules (mirror of the uploader, Option A — semantic dedup):
///  * titles / sources / series — deterministic ids (MD5-derived), upsert by id.
///  * mappings — cloud mapping ids are TRANSIENT. Each cloud mapping (its title
///    set) is folded into a local series by title overlap (same fuzzy rule as
///    <see cref="ContributionPropagationService"/>'s resolver), creating a local
///    <see cref="SeriesEntity"/> (fresh local Guid) when none overlaps.
///  * metadata — cloud metadata ids are TRANSIENT. Rows are deduped by
///    (localMappingId, ProviderId, ProviderKey); existing rows keep their local
///    id and merge mapping_status by priority
///    (UserConfirmed > Blocked > AutoMatched > ForeverIgnored >
///    ManualIgnore > Unmatched), linked_date is last-writer-wins.
///
/// Only active (non-archived) rows are exported by the worker, so every incoming
/// row is CLOUD-BACKED: it is stamped <see cref="ContributionVersions.Uploaded"/> (-2),
/// matching <see cref="ContributionImportService"/>. Marking it -2 (not 0) is what lets the
/// local reproduction of a cloud row be recognised as "exists in the cloud", so a later
/// reconcile tombstones (rather than hard-deletes) it and never re-exports it. Local rows not
/// present in the snapshot are left untouched (no destructive reconciliation).
/// </summary>
public class ContributionDownloadService
{
    private const int VersionUploaded = ContributionVersions.Uploaded;

    private readonly ContributionDbContext _contributorDb;
    private readonly SettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ContributionTitleHeuristicsService _titleHeuristics;
    private readonly ILogger<ContributionDownloadService> _logger;

    public ContributionDownloadService(
        ContributionDbContext contributorDb,
        SettingsService settingsService,
        IHttpClientFactory httpClientFactory,
        ContributionTitleHeuristicsService titleHeuristics,
        ILogger<ContributionDownloadService> logger)
    {
        _contributorDb = contributorDb;
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
        _titleHeuristics = titleHeuristics;
        _logger = logger;
    }

    /// <summary>
    /// Fetches the cloud snapshot and applies it to the local contribution database.
    /// </summary>
    /// <returns>Counts of applied rows per entity type.</returns>
    public async Task<ContributionDownloadResult> DownloadSnapshotAsync(CancellationToken token = default)
    {
        var settings = await _settingsService.GetSettingsAsync(token).ConfigureAwait(false);
        if (!settings.ContributionEnabled)
        {
            return ContributionDownloadResult.Failed("Contribution is disabled in settings.");
        }
        if (!settings.ContributionVerified)
        {
            return ContributionDownloadResult.Failed("Contribution contributor id has not been verified against the contribution database.");
        }
        if (string.IsNullOrWhiteSpace(settings.ContributionContributorId))
        {
            return ContributionDownloadResult.Failed("Contribution contributor id is not configured.");
        }
        if (string.IsNullOrWhiteSpace(settings.ContributionServerUrl))
        {
            return ContributionDownloadResult.Failed("Contribution server url is not configured.");
        }

        string endpoint = settings.ContributionServerUrl.TrimEnd('/') +
            "/snapshot?contributor=" + Uri.EscapeDataString(settings.ContributionContributorId);

        using var client = _httpClientFactory.CreateClient("ContributionUpload");
        using var response = await client.GetAsync(endpoint, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return ContributionDownloadResult.Failed(
                $"Contribution worker rejected snapshot download ({response.StatusCode}): {Truncate(errorBody, 500)}");
        }

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        var snapshot = await response.Content.ReadFromJsonAsync<ContributionSnapshotV1>(options, token)
            .ConfigureAwait(false);
        if (snapshot == null)
        {
            return ContributionDownloadResult.Failed("Contribution worker returned an empty snapshot.");
        }

        return await ApplySnapshotAsync(snapshot, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a decoded snapshot to the local contribution database.
    /// Public so tests / the GitHub-export path can reuse the same apply logic.
    /// </summary>
    public async Task<ContributionDownloadResult> ApplySnapshotAsync(
        ContributionSnapshotV1 snapshot, CancellationToken token = default)
    {
        try
        {
            // ── 1. Titles (deterministic ids) ──
            var titles = await _contributorDb.Titles
                .Where(t => snapshot.Titles.Select(x => x.Id).Contains(t.Id))
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);
            var titlesById = titles.ToDictionary(t => t.Id);
            int titleCount = 0;
            foreach (var cloud in snapshot.Titles)
            {
                if (titlesById.TryGetValue(cloud.Id, out var local))
                {
                    if (local.Title != cloud.Title || ContributionVersions.IsDeleted(local.Version))
                    {
                        _contributorDb.Titles.Attach(local);
                        local.Title = cloud.Title;
                        local.Version = VersionUploaded;
                        _contributorDb.Entry(local).Property(t => t.Title).IsModified = true;
                        _contributorDb.Entry(local).Property(t => t.Version).IsModified = true;
                        titleCount++;
                    }
                }
                else
                {
                    _contributorDb.Titles.Add(new TitleEntity
                    {
                        Id = cloud.Id,
                        Title = cloud.Title,
                        Version = VersionUploaded,
                    });
                    titlesById[cloud.Id] = cloud;
                    titleCount++;
                }
            }

            // ── 2. Sources (deterministic ids) ──
            // ContributionSourceEntity identity/display fields are init-only
            // (immutable once created — the first writer wins). For existing rows
            // we only resurrect: bump Version from tombstone (-1) to 0.
            var sources = await _contributorDb.Sources
                .Where(s => snapshot.Sources.Select(x => x.Id).Contains(s.Id))
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);
            var sourcesById = sources.ToDictionary(s => s.Id);
            int sourceCount = 0;
            foreach (var cloud in snapshot.Sources)
            {
                if (sourcesById.TryGetValue(cloud.Id, out var local))
                {
                    if (ContributionVersions.IsDeleted(local.Version))
                    {
                        _contributorDb.Sources.Attach(local);
                        local.Version = VersionUploaded;
                        _contributorDb.Entry(local).Property(s => s.Version).IsModified = true;
                        sourceCount++;
                    }
                }
                else
                {
                    _contributorDb.Sources.Add(new ContributionSourceEntity
                    {
                        Id = cloud.Id,
                        Package = cloud.Package,
                        PackageId = cloud.PackageId != Guid.Empty
                            ? cloud.PackageId
                            : ContributionSourceEntity.DerivePackageId(cloud.Package),
                        SourceId = cloud.SourceId,
                        SourceName = cloud.SourceName,
                        SourceLanguage = cloud.SourceLanguage,
                        LastBatchExecutionUTC = cloud.LastBatchExecutionUTC,
                        LatestIds = cloud.LatestIds,
                        Version = VersionUploaded,
                    });
                    sourcesById[cloud.Id] = cloud;
                    sourceCount++;
                }
            }

            // Cloud package id per source, so a series entry (which carries no source id anymore)
            // can resolve its PackageId via its associations.
            var cloudPackageById = new Dictionary<Guid, Guid>();
            foreach (var s in snapshot.Sources)
                cloudPackageById[s.Id] = s.PackageId != Guid.Empty
                    ? s.PackageId
                    : ContributionSourceEntity.DerivePackageId(s.Package);
            foreach (var s in sources)
                cloudPackageById[s.Id] = s.PackageId != Guid.Empty
                    ? s.PackageId
                    : ContributionSourceEntity.DerivePackageId(s.Package);

            // ── 3. Mappings (cloud → local by title overlap) ──
            // Group the cloud mapping_titles by cloud mapping id to recover each
            // mapping's title set (mappings are implicit — no separate wire list).
            var cloudMappingTitlesById = new Dictionary<Guid, List<Guid>>();
            foreach (var mt in snapshot.Mappings)
            {
                if (!cloudMappingTitlesById.TryGetValue(mt.SeriesId, out var list))
                {
                    list = new List<Guid>();
                    cloudMappingTitlesById[mt.SeriesId] = list;
                }
                list.Add(mt.TitleId);
            }

            var localByCloudMapping = new Dictionary<Guid, Guid>();

            // Pairs already tracked (added/attached earlier in this unit of work) and pairs
            // already queued by THIS loop. The DB-only AnyAsync below cannot see either, so
            // without this two cloud mappings collapsing onto one local mapping (or a
            // re-apply in the same context) would Add the same (MappingId, TitleId) twice
            // and EF would throw an identity-conflict on the composite key.
            var knownPairs = _contributorDb.ChangeTracker.Entries<SeriesTitleEntity>()
                .Select(e => (e.Entity.SeriesId, e.Entity.TitleId))
                .ToHashSet();

            foreach (var (cloudMappingId, titleIds) in cloudMappingTitlesById)
            {
                var localMappingId = await ResolveLocalMappingAsync(titleIds, token).ConfigureAwait(false);
                localByCloudMapping[cloudMappingId] = localMappingId;

                // Ensure every association exists under the LOCAL mapping id.
                foreach (var titleId in titleIds.Distinct())
                {
                    if (!knownPairs.Add((localMappingId, titleId)))
                        continue;

                    bool exists = await _contributorDb.SeriesTitles
                        .AnyAsync(mt => mt.SeriesId == localMappingId && mt.TitleId == titleId, token)
                        .ConfigureAwait(false);
                    if (!exists)
                    {
                        _contributorDb.SeriesTitles.Add(new SeriesTitleEntity
                        {
                            SeriesId = localMappingId,
                            TitleId = titleId,
                            Version = VersionUploaded,
                        });
                    }
                }
            }

            // ── 3b. Mappings reconcile (title unglue) ──
            // The cloud snapshot is the source of truth for the mapping-title graph. A title the
            // cloud moved to a different mapping (mapping-conflict repair) must stop being
            // associated with the stale local mapping. Tombstone local non-pending mapping_title
            // rows whose (mappingId, titleId) is absent from the snapshot; local pending rows
            // (Version 0/-1, user edits) are preserved.
            var desiredPairs = new HashSet<(Guid MappingId, Guid TitleId)>();
            foreach (var (cloudMappingId, titleIds) in cloudMappingTitlesById)
            {
                if (!localByCloudMapping.TryGetValue(cloudMappingId, out var localMappingId))
                    continue;
                foreach (var titleId in titleIds.Distinct())
                    desiredPairs.Add((localMappingId, titleId));
            }
            var localMappingIds = localByCloudMapping.Values.Distinct().ToList();
            // Only UPLOADED LIVE (-2) links are candidates for a fresh tombstone. A pending
            // row (0/-1, user edit) is preserved, and a row already tombstoned (-1/-3) must
            // not be re-flipped (that would re-export a delete already sent).
            var localMappingTitleRows = await _contributorDb.SeriesTitles
                .Where(mt => localMappingIds.Contains(mt.SeriesId)
                    && mt.Version == ContributionVersions.Uploaded)
                .ToListAsync(token).ConfigureAwait(false);
            foreach (var mt in localMappingTitleRows)
            {
                if (desiredPairs.Contains((mt.SeriesId, mt.TitleId))) continue;
                _contributorDb.SeriesTitles.Attach(mt);
                mt.Version = -1;
                _contributorDb.Entry(mt).Property(x => x.Version).IsModified = true;
            }

            int mappingCount = localByCloudMapping.Count;

            // ── 4. Series (deterministic ids, mapping re-keyed) ──
            var series = await _contributorDb.SeriesEntries
                .Where(s => snapshot.Series.Select(x => x.Id).Contains(s.Id))
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);
            var seriesById = series.ToDictionary(s => s.Id);
            int seriesCount = 0;
            foreach (var cloud in snapshot.Series)
            {
                if (!localByCloudMapping.TryGetValue(cloud.SeriesId, out var localMappingId))
                {
                    // Mapping for this series was not in the mapping_titles list —
                    // resolve by its record title id as a fallback.
                    localMappingId = await ResolveLocalMappingAsync(new[] { cloud.Data.TitleId }, token)
                        .ConfigureAwait(false);
                    localByCloudMapping[cloud.SeriesId] = localMappingId;
                }

                if (seriesById.TryGetValue(cloud.Id, out var local))
                {
                    bool dataChanged = !JsonEquals(local.Data, cloud.Data);
                    // Null-preserving verified date: the cloud value is one-way. Only adopt it when
                    // non-null so a null cloud value cannot clear a local verification (mirrors the
                    // worker's sticky rule); a local clear is therefore also never re-downloaded.
                    bool verifiedChanged = cloud.VerifiedDateUTC is not null
                        && local.VerifiedDateUTC != cloud.VerifiedDateUTC;
                    if (dataChanged || local.SeriesId != localMappingId
                        || ContributionVersions.IsDeleted(local.Version)
                        || verifiedChanged)
                    {
                        _contributorDb.SeriesEntries.Attach(local);
                        local.SeriesId = localMappingId;
                        local.PackageId = ResolvePackageId(cloud, cloudPackageById);
                        local.Data = cloud.Data;
                        if (cloud.VerifiedDateUTC is not null)
                        {
                            local.VerifiedDateUTC = cloud.VerifiedDateUTC;
                            _contributorDb.Entry(local).Property(s => s.VerifiedDateUTC).IsModified = true;
                        }
                        local.Version = VersionUploaded;
                        _contributorDb.Entry(local).Property(s => s.SeriesId).IsModified = true;
                        _contributorDb.Entry(local).Property(s => s.PackageId).IsModified = true;
                        _contributorDb.Entry(local).Property(s => s.Data).IsModified = true;
                        _contributorDb.Entry(local).Property(s => s.Version).IsModified = true;
                        seriesCount++;
                    }
                }
                else
                {
                    _contributorDb.SeriesEntries.Add(new ContributionSeriesEntity
                    {
                        Id = cloud.Id,
                        SeriesId = localMappingId,
                        PackageId = ResolvePackageId(cloud, cloudPackageById),
                        Data = cloud.Data,
                        VerifiedDateUTC = cloud.VerifiedDateUTC,
                        // Advisory seed for a brand-new local row: the cloud's derived flag. The
                        // repair pass (RecomputeSeriesMetadataFlagsAsync) is authoritative locally,
                        // so this is never trusted over a local recompute.
                        Metadata = cloud.Metadata,
                        Version = VersionUploaded,
                    });
                    seriesById[cloud.Id] = cloud;
                    seriesCount++;
                }
            }

            // ── 4b. Source associations (many-to-many) ──
            // The cloud ships the authoritative source set per series entry; rebuild the local
            // SourceSeries rows so consolidation/re-parenting is reflected. Local pending rows
            // (user edits, Version 0/-1) are preserved.
            await SyncSourceAssociationsAsync(
                snapshot.SourceSeries, seriesById.Keys.ToList(), token).ConfigureAwait(false);

            // ── 5. Metadata (dedup by localMappingId + ProviderId + ProviderKey) ──
            int metadataCount = 0;
            var metadataByLocalMapping = await _contributorDb.Metadata
                .Where(m => localByCloudMapping.Values.Contains(m.SeriesId))
                .AsNoTracking()
                .ToListAsync(token)
                .ConfigureAwait(false);
            var localMetadataByKey = metadataByLocalMapping
                .GroupBy(m => m.SeriesId)
                .ToDictionary(
                    g => g.Key,
                    g => g.ToDictionary(m => (m.ProviderId, m.ProviderKey ?? string.Empty)));
            // Rows added during THIS apply are tracked by the context — consult the
            // change tracker so duplicate (mapping, provider, key) rows within one
            // snapshot collapse instead of inserting twice.
            var touchedLocalMetadata = new Dictionary<(Guid MappingId, int ProviderId, string ProviderKey), ContributionMetadataEntity>();

            foreach (var cloud in snapshot.Metadata)
            {
                if (!localByCloudMapping.TryGetValue(cloud.SeriesId, out var localMappingId))
                {
                    localMappingId = await ResolveLocalMappingAsync(Array.Empty<Guid>(), token)
                        .ConfigureAwait(false);
                    localByCloudMapping[cloud.SeriesId] = localMappingId;
                }

                var key = (cloud.ProviderId, cloud.ProviderKey ?? string.Empty);
                ContributionMetadataEntity? local = null;

                if (touchedLocalMetadata.TryGetValue((localMappingId, cloud.ProviderId, cloud.ProviderKey ?? string.Empty), out var tracked))
                {
                    local = tracked;
                }
                else
                {
                    local = FindLocalMetadata(localMetadataByKey, localMappingId, key);
                }

                if (local == null)
                {
                    // Assign a fresh LOCAL id — the cloud id is transient.
                    local = new ContributionMetadataEntity
                    {
                        Id = Guid.NewGuid(),
                        SeriesId = localMappingId,
                        ProviderId = cloud.ProviderId,
                        ProviderKey = cloud.ProviderKey ?? string.Empty,
                        MappingStatus = cloud.MappingStatus,
                        LinkedDate = cloud.LinkedDate,
                        Version = VersionUploaded,
                    };
                    _contributorDb.Metadata.Add(local);
                    touchedLocalMetadata[(local.SeriesId, local.ProviderId, local.ProviderKey)] = local;
                    metadataCount++;
                }
                else if (!touchedLocalMetadata.ContainsKey((localMappingId, cloud.ProviderId, cloud.ProviderKey ?? string.Empty)))
                {
                    var merged = MergeStatus(local.MappingStatus, cloud.MappingStatus);
                    bool changed = merged != local.MappingStatus
                        || local.LinkedDate != cloud.LinkedDate
                        || ContributionVersions.IsDeleted(local.Version);
                    if (changed)
                    {
                        _contributorDb.Metadata.Attach(local);
                        local.MappingStatus = merged;
                        local.LinkedDate = cloud.LinkedDate;
                        local.Version = VersionUploaded;
                        _contributorDb.Entry(local).Property(m => m.MappingStatus).IsModified = true;
                        _contributorDb.Entry(local).Property(m => m.LinkedDate).IsModified = true;
                        _contributorDb.Entry(local).Property(m => m.Version).IsModified = true;
                    }
                    touchedLocalMetadata[(local.SeriesId, local.ProviderId, local.ProviderKey)] = local;
                    metadataCount++;
                }
            }

            // Reconcile title associations against the merged Auto/User links.
            await _titleHeuristics.ReconcileAllAsync(token).ConfigureAwait(false);

            // Cloud-apply must NOT invalidate local verification: suppress the graph-change hook
            // for this save (the download is the authoritative cloud state, not a user/scan edit).
            using (_contributorDb.SuppressVerificationInvalidation())
            {
                await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
            }

            return ContributionDownloadResult.Succeeded(
                titleCount, mappingCount, sourceCount, seriesCount, metadataCount);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DbUpdateException)
        {
            _logger.LogError(ex, "Contribution snapshot apply failed");
            return ContributionDownloadResult.Failed($"Failed to apply contribution snapshot: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves a set of title ids to a LOCAL mapping id using the same
    /// title-overlap rule as the propagation service: prefers the local mapping
    /// that already links the most of these titles; creates a new local mapping
    /// (fresh Guid) when none overlaps.
    /// </summary>
    private async Task<Guid> ResolveLocalMappingAsync(IEnumerable<Guid> titleIds, CancellationToken token)
    {
        var distinct = titleIds.Distinct().ToList();
        if (distinct.Count == 0)
        {
            var created = new SeriesEntity { Id = Guid.NewGuid() };
            _contributorDb.Series.Add(created);
            return created.Id;
        }

        // Only LIVE links count: a tombstoned association must not resolve a local mapping.
        var seriesTitles = await _contributorDb.SeriesTitles
            .Where(mt => distinct.Contains(mt.TitleId)
                && mt.Version != ContributionVersions.PendingDelete
                && mt.Version != ContributionVersions.UploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        var overlap = new Dictionary<Guid, int>();
        foreach (var mt in seriesTitles)
        {
            overlap.TryGetValue(mt.SeriesId, out var count);
            overlap[mt.SeriesId] = count + 1;
        }

        var best = overlap.OrderByDescending(kv => kv.Value).FirstOrDefault();
        if (best.Key != Guid.Empty)
            return best.Key;

        var createdMapping = new SeriesEntity { Id = Guid.NewGuid() };
        _contributorDb.Series.Add(createdMapping);
        return createdMapping.Id;
    }

    private static ContributionMetadataEntity? FindLocalMetadata(
        Dictionary<Guid, Dictionary<(int ProviderId, string ProviderKey), ContributionMetadataEntity>> byMapping,
        Guid mappingId,
        (int ProviderId, string ProviderKey) key)
    {
        return byMapping.TryGetValue(mappingId, out var byKey)
            && byKey.TryGetValue(key, out var row)
            ? row
            : null;
    }

    /// <summary>
    /// Status-priority merge — mirrors the worker's ON CONFLICT CASE expression:
    /// UserConfirmed(2) > Blocked(5) > AutoMatched(1) > ForeverIgnored(4)
    /// > ManualIgnore(3) > Unmatched(0).
    /// </summary>
    private static SeriesMappingStatus MergeStatus(SeriesMappingStatus current, SeriesMappingStatus incoming)
    {
        if (current == SeriesMappingStatus.UserConfirmed || incoming == SeriesMappingStatus.UserConfirmed)
            return SeriesMappingStatus.UserConfirmed;
        if (current == SeriesMappingStatus.Blocked || incoming == SeriesMappingStatus.Blocked)
            return SeriesMappingStatus.Blocked;
        if (current == SeriesMappingStatus.AutoMatched || incoming == SeriesMappingStatus.AutoMatched)
            return SeriesMappingStatus.AutoMatched;
        if (current == SeriesMappingStatus.ForeverIgnored || incoming == SeriesMappingStatus.ForeverIgnored)
            return SeriesMappingStatus.ForeverIgnored;
        if (current == SeriesMappingStatus.ManualIgnore || incoming == SeriesMappingStatus.ManualIgnore)
            return SeriesMappingStatus.ManualIgnore;
        return SeriesMappingStatus.Unmatched;
    }

    private static bool JsonEquals<T>(T a, T b)
        => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    /// <summary>
    /// Resolves a series entry's <c>PackageId</c>: prefer the value on the (new) cloud entry; fall
    /// back to the package id of its representative/associated source, then to the record url's
    /// owning package when the cloud predates the normalization.
    /// </summary>
    private static Guid ResolvePackageId(
        ContributionSeriesEntity cloud, IReadOnlyDictionary<Guid, Guid> cloudPackageById)
    {
        if (cloud.PackageId != Guid.Empty)
            return cloud.PackageId;
        return cloudPackageById.TryGetValue(cloud.SourceId, out var packageId) ? packageId : Guid.Empty;
    }

    /// <summary>
    /// Reconciles the local <c>SourceSeries</c> association set for the supplied series entries
    /// against the cloud snapshot: live associations missing locally are added; local UNPENDING
    /// associations that the cloud dropped are tombstoned. Local pending rows (Version 0/-1, user
    /// edits) are preserved.
    /// </summary>
    private async Task SyncSourceAssociationsAsync(
        IReadOnlyList<ContributionSourceSeriesEntity> cloudRows,
        IReadOnlyList<Guid> seriesIds, CancellationToken token)
    {
        if (seriesIds.Count == 0) return;

        // Guard against a STALE cloud snapshot (produced before the SourceSeries normalization):
        // when the snapshot carries NO association rows at all, do NOT treat that as "no sources"
        // — that would tombstone the associations the local migrator just rebuilt. Association
        // deletions only apply once the cloud has started publishing the feature.
        if (cloudRows.Count == 0) return;

        var desired = cloudRows
            .Where(ss => ss.SeriesEntityId != Guid.Empty && ss.SourceId != Guid.Empty)
            .Select(ss => (ss.SourceId, ss.SeriesEntityId))
            .ToHashSet();

        var existing = await _contributorDb.SourceSeries
            .Where(ss => seriesIds.Contains(ss.SeriesEntityId))
            .ToListAsync(token).ConfigureAwait(false);
        var existingByKey = existing.ToDictionary(ss => (ss.SourceId, ss.SeriesEntityId));

        foreach (var key in desired)
        {
            if (existingByKey.TryGetValue(key, out var row))
            {
                if (ContributionVersions.IsDeleted(row.Version))
                {
                    _contributorDb.SourceSeries.Attach(row);
                    row.Version = VersionUploaded;
                    _contributorDb.Entry(row).Property(x => x.Version).IsModified = true;
                }
            }
            else
            {
                _contributorDb.SourceSeries.Add(new ContributionSourceSeriesEntity
                {
                    SourceId = key.SourceId,
                    SeriesEntityId = key.SeriesEntityId,
                    Version = VersionUploaded
                });
            }
        }

        // Tombstone clean local associations the cloud no longer carries (pending rows preserved).
        foreach (var row in existing)
        {
            if (row.Version != ContributionVersions.Uploaded) continue; // keep pending (0/-1) + tombstoned
            if (desired.Contains((row.SourceId, row.SeriesEntityId))) continue;
            _contributorDb.SourceSeries.Attach(row);
            row.Version = ContributionVersions.UploadedDelete;
            _contributorDb.Entry(row).Property(x => x.Version).IsModified = true;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

/// <summary>Result of a contribution snapshot download / apply.</summary>
public sealed class ContributionDownloadResult
{
    public bool Success { get; private init; }
    public string? Error { get; private init; }
    public int Titles { get; private init; }
    public int Mappings { get; private init; }
    public int Sources { get; private init; }
    public int Series { get; private init; }
    public int Metadata { get; private init; }

    public static ContributionDownloadResult Succeeded(
        int titles, int mappings, int sources, int series, int metadata) => new()
    {
        Success = true,
        Titles = titles,
        Mappings = mappings,
        Sources = sources,
        Series = series,
        Metadata = metadata,
    };

    public static ContributionDownloadResult Failed(string error) => new()
    {
        Success = false,
        Error = error,
    };
}
