using RensaioBackend.Data;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Models.Database;
using RensaioBackend.Services.Contributions.Snapshot;
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
///    set) is folded into a local mapping by title overlap (same fuzzy rule as
///    <see cref="ContributionPropagationService"/>'s resolver), creating a local
///    <see cref="MappingEntity"/> (fresh local Guid) when none overlaps.
///  * metadata — cloud metadata ids are TRANSIENT. Rows are deduped by
///    (localMappingId, ProviderId, ProviderKey); existing rows keep their local
///    id and merge mapping_status by priority
///    (UserConfirmed > Blocked > AutoMatched > ForeverIgnored >
///    TemporaryIgnored > Unmatched), linked_date is last-writer-wins.
///
/// Only active (non-archived) rows are exported by the worker, so every incoming
/// row is treated as add/update (v = 0). Local rows not present in the snapshot
/// are left untouched (no destructive reconciliation).
/// </summary>
public class ContributionDownloadService
{
    private const int VersionAddOrUpdate = 0;

    private readonly ContributionDbContext _contributorDb;
    private readonly SettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ContributionDownloadService> _logger;

    public ContributionDownloadService(
        ContributionDbContext contributorDb,
        SettingsService settingsService,
        IHttpClientFactory httpClientFactory,
        ILogger<ContributionDownloadService> logger)
    {
        _contributorDb = contributorDb;
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
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
                    if (local.Title != cloud.Title || local.Version == -1)
                    {
                        _contributorDb.Titles.Attach(local);
                        local.Title = cloud.Title;
                        local.Version = VersionAddOrUpdate;
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
                        Version = VersionAddOrUpdate,
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
                    if (local.Version == -1)
                    {
                        _contributorDb.Sources.Attach(local);
                        local.Version = VersionAddOrUpdate;
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
                        SourceId = cloud.SourceId,
                        SourceName = cloud.SourceName,
                        SourceLanguage = cloud.SourceLanguage,
                        LastBatchExecutionUTC = cloud.LastBatchExecutionUTC,
                        Version = VersionAddOrUpdate,
                    });
                    sourcesById[cloud.Id] = cloud;
                    sourceCount++;
                }
            }

            // ── 3. Mappings (cloud → local by title overlap) ──
            // Group the cloud mapping_titles by cloud mapping id to recover each
            // mapping's title set (mappings are implicit — no separate wire list).
            var cloudMappingTitlesById = new Dictionary<Guid, List<Guid>>();
            foreach (var mt in snapshot.Mappings)
            {
                if (!cloudMappingTitlesById.TryGetValue(mt.MappingId, out var list))
                {
                    list = new List<Guid>();
                    cloudMappingTitlesById[mt.MappingId] = list;
                }
                list.Add(mt.TitleId);
            }

            var localByCloudMapping = new Dictionary<Guid, Guid>();

            // Pairs already tracked (added/attached earlier in this unit of work) and pairs
            // already queued by THIS loop. The DB-only AnyAsync below cannot see either, so
            // without this two cloud mappings collapsing onto one local mapping (or a
            // re-apply in the same context) would Add the same (MappingId, TitleId) twice
            // and EF would throw an identity-conflict on the composite key.
            var knownPairs = _contributorDb.ChangeTracker.Entries<MappingTitleEntity>()
                .Select(e => (e.Entity.MappingId, e.Entity.TitleId))
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

                    bool exists = await _contributorDb.MappingTitles
                        .AnyAsync(mt => mt.MappingId == localMappingId && mt.TitleId == titleId, token)
                        .ConfigureAwait(false);
                    if (!exists)
                    {
                        _contributorDb.MappingTitles.Add(new MappingTitleEntity
                        {
                            MappingId = localMappingId,
                            TitleId = titleId,
                            Version = VersionAddOrUpdate,
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
            var localMappingTitleRows = await _contributorDb.MappingTitles
                .Where(mt => localMappingIds.Contains(mt.MappingId)
                    && mt.Version != VersionAddOrUpdate
                    && mt.Version != -1)
                .ToListAsync(token).ConfigureAwait(false);
            foreach (var mt in localMappingTitleRows)
            {
                if (desiredPairs.Contains((mt.MappingId, mt.TitleId))) continue;
                _contributorDb.MappingTitles.Attach(mt);
                mt.Version = -1;
                _contributorDb.Entry(mt).Property(x => x.Version).IsModified = true;
            }

            int mappingCount = localByCloudMapping.Count;

            // ── 4. Series (deterministic ids, mapping re-keyed) ──
            var series = await _contributorDb.Series
                .Where(s => snapshot.Series.Select(x => x.Id).Contains(s.Id))
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);
            var seriesById = series.ToDictionary(s => s.Id);
            int seriesCount = 0;
            foreach (var cloud in snapshot.Series)
            {
                if (!localByCloudMapping.TryGetValue(cloud.MappingId, out var localMappingId))
                {
                    // Mapping for this series was not in the mapping_titles list —
                    // resolve by its record title id as a fallback.
                    localMappingId = await ResolveLocalMappingAsync(new[] { cloud.Data.TitleId }, token)
                        .ConfigureAwait(false);
                    localByCloudMapping[cloud.MappingId] = localMappingId;
                }

                if (seriesById.TryGetValue(cloud.Id, out var local))
                {
                    bool dataChanged = !JsonEquals(local.Data, cloud.Data);
                    if (dataChanged || local.MappingId != localMappingId
                        || local.SourceId != cloud.SourceId || local.Version == -1)
                    {
                        _contributorDb.Series.Attach(local);
                        local.MappingId = localMappingId;
                        local.SourceId = cloud.SourceId;
                        local.Data = cloud.Data;
                        local.Version = VersionAddOrUpdate;
                        _contributorDb.Entry(local).Property(s => s.MappingId).IsModified = true;
                        _contributorDb.Entry(local).Property(s => s.SourceId).IsModified = true;
                        _contributorDb.Entry(local).Property(s => s.Data).IsModified = true;
                        _contributorDb.Entry(local).Property(s => s.Version).IsModified = true;
                        seriesCount++;
                    }
                }
                else
                {
                    _contributorDb.Series.Add(new ContributionSeriesEntity
                    {
                        Id = cloud.Id,
                        MappingId = localMappingId,
                        SourceId = cloud.SourceId,
                        Data = cloud.Data,
                        Version = VersionAddOrUpdate,
                    });
                    seriesById[cloud.Id] = cloud;
                    seriesCount++;
                }
            }

            // ── 5. Metadata (dedup by localMappingId + ProviderId + ProviderKey) ──
            int metadataCount = 0;
            var metadataByLocalMapping = await _contributorDb.Metadata
                .Where(m => localByCloudMapping.Values.Contains(m.MappingId))
                .AsNoTracking()
                .ToListAsync(token)
                .ConfigureAwait(false);
            var localMetadataByKey = metadataByLocalMapping
                .GroupBy(m => m.MappingId)
                .ToDictionary(
                    g => g.Key,
                    g => g.ToDictionary(m => (m.ProviderId, m.ProviderKey ?? string.Empty)));
            // Rows added during THIS apply are tracked by the context — consult the
            // change tracker so duplicate (mapping, provider, key) rows within one
            // snapshot collapse instead of inserting twice.
            var touchedLocalMetadata = new Dictionary<(Guid MappingId, int ProviderId, string ProviderKey), ContributionMetadataEntity>();

            foreach (var cloud in snapshot.Metadata)
            {
                if (!localByCloudMapping.TryGetValue(cloud.MappingId, out var localMappingId))
                {
                    localMappingId = await ResolveLocalMappingAsync(Array.Empty<Guid>(), token)
                        .ConfigureAwait(false);
                    localByCloudMapping[cloud.MappingId] = localMappingId;
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
                        MappingId = localMappingId,
                        ProviderId = cloud.ProviderId,
                        ProviderKey = cloud.ProviderKey ?? string.Empty,
                        MappingStatus = cloud.MappingStatus,
                        LinkedDate = cloud.LinkedDate,
                        Version = VersionAddOrUpdate,
                    };
                    _contributorDb.Metadata.Add(local);
                    touchedLocalMetadata[(local.MappingId, local.ProviderId, local.ProviderKey)] = local;
                    metadataCount++;
                }
                else if (!touchedLocalMetadata.ContainsKey((localMappingId, cloud.ProviderId, cloud.ProviderKey ?? string.Empty)))
                {
                    var merged = MergeStatus(local.MappingStatus, cloud.MappingStatus);
                    bool changed = merged != local.MappingStatus
                        || local.LinkedDate != cloud.LinkedDate
                        || local.Version == -1;
                    if (changed)
                    {
                        _contributorDb.Metadata.Attach(local);
                        local.MappingStatus = merged;
                        local.LinkedDate = cloud.LinkedDate;
                        local.Version = VersionAddOrUpdate;
                        _contributorDb.Entry(local).Property(m => m.MappingStatus).IsModified = true;
                        _contributorDb.Entry(local).Property(m => m.LinkedDate).IsModified = true;
                        _contributorDb.Entry(local).Property(m => m.Version).IsModified = true;
                    }
                    touchedLocalMetadata[(local.MappingId, local.ProviderId, local.ProviderKey)] = local;
                    metadataCount++;
                }
            }

            await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);

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
            var created = new MappingEntity { Id = Guid.NewGuid() };
            _contributorDb.Mappings.Add(created);
            return created.Id;
        }

        var mappingTitles = await _contributorDb.MappingTitles
            .Where(mt => distinct.Contains(mt.TitleId))
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        var overlap = new Dictionary<Guid, int>();
        foreach (var mt in mappingTitles)
        {
            overlap.TryGetValue(mt.MappingId, out var count);
            overlap[mt.MappingId] = count + 1;
        }

        var best = overlap.OrderByDescending(kv => kv.Value).FirstOrDefault();
        if (best.Key != Guid.Empty)
            return best.Key;

        var createdMapping = new MappingEntity { Id = Guid.NewGuid() };
        _contributorDb.Mappings.Add(createdMapping);
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
    /// > TemporaryIgnored(3) > Unmatched(0).
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
        if (current == SeriesMappingStatus.TemporaryIgnored || incoming == SeriesMappingStatus.TemporaryIgnored)
            return SeriesMappingStatus.TemporaryIgnored;
        return SeriesMappingStatus.Unmatched;
    }

    private static bool JsonEquals<T>(T a, T b)
        => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

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