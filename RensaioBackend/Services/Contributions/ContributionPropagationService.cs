using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RensaioBackend.Data;
using RensaioBackend.Extensions;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Settings;
using System.Globalization;
using System.Text.Json;

namespace RensaioBackend.Services.Contributions
{
    /// <summary>
    /// Propagates a series' mapping state into the local contribution database
    /// (<c>contributor.db</c>) whenever a mapping action completes and the
    /// <c>ContributionEnabled</c> setting is on.
    ///
    /// Semantics:
    ///   - ADD / UPDATE  → Version = 0
    ///   - logical DELETE (row no longer desired, or identity key changed) → Version = -1
    ///   - identical rows are left untouched (no blind upserts).
    /// </summary>
    public class ContributionPropagationService
    {
        private const int VersionAddOrUpdate = 0;
        private const int VersionLogicalDelete = -1;

        private readonly AppDbContext _db;
        private readonly ContributionDbContext _contributorDb;
        private readonly SettingsService _settingsService;
        private readonly ILogger<ContributionPropagationService> _logger;

        public ContributionPropagationService(
            AppDbContext db,
            ContributionDbContext contributorDb,
            SettingsService settingsService,
            ILogger<ContributionPropagationService> logger)
        {
            _db = db;
            _contributorDb = contributorDb;
            _settingsService = settingsService;
            _logger = logger;
        }

        /// <summary>
        /// Returns <c>true</c> when the contribution flag is enabled and the
        /// contributor database is available. Reads the PERSISTED flags directly from the
        /// Settings table (not the in-memory cache) so a just-verified flag that was written via
        /// <see cref="SettingsService.SetContributionVerifiedAsync"/> is honored immediately.
        /// </summary>
        public async Task<bool> IsEnabledAsync(CancellationToken token = default)
        {
            try
            {
                var rows = await _db.Settings
                    .AsNoTracking()
                    .Where(s => s.Name == "ContributionEnabled"
                        || s.Name == "ContributionVerified")
                    .Select(s => new SettingEntity { Name = s.Name, Value = s.Value })
                    .ToListAsync(token).ConfigureAwait(false);
                bool enabled = GetSettingBool(rows, "ContributionEnabled");
                bool verified = GetSettingBool(rows, "ContributionVerified");
                _logger.LogInformation("Contribution propagation gate: ContributionEnabled={Enabled}, ContributionVerified={Verified}",
                    enabled, verified);
                return enabled && verified;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read contribution flag");
                return false;
            }
        }

        private static bool GetSettingBool(List<SettingEntity> rows, string name)
            => rows.FirstOrDefault(r => r.Name == name)?.Value is { } v
                && bool.TryParse(v, out var b) && b;

        /// <summary>
        /// Propagates the mapping state of EVERY series into the contribution database.
        /// Used when a user becomes a contributor (ContributionVerified flips to true):
        /// the whole SeriesMappings graph is migrated to contributor.db in one pass.
        /// Best-effort: iterates <see cref="SyncSeriesAsync"/> per series; no-ops when the
        /// contribution is not enabled+verified; never throws into the caller.
        /// </summary>
        public async Task SyncAllSeriesAsync(CancellationToken token = default)
        {
            _logger.LogInformation("SyncAllSeriesAsync: starting full contribution propagation.");
            if (!await IsEnabledAsync(token).ConfigureAwait(false))
            {
                _logger.LogWarning("SyncAllSeriesAsync: gate is DISABLED (contributions not enabled+verified) — skipping migration.");
                return;
            }
            try
            {
                var ids = await _db.Series.AsNoTracking()
                    .Select(s => s.Id)
                    .ToListAsync(token).ConfigureAwait(false);
                _logger.LogInformation("SyncAllSeriesAsync: gate OK — {Count} series to propagate.", ids.Count);
                var processed = 0;
                foreach (var id in ids)
                {
                    if (token.IsCancellationRequested) break;
                    // Read fresh settings each iteration so a just-verified flag (persisted via
                    // SetContributionVerifiedAsync) is picked up even if the in-memory cache is stale.
                    if (!await IsEnabledAsync(token).ConfigureAwait(false))
                    {
                        _logger.LogWarning("SyncAllSeriesAsync: gate became disabled mid-run — aborting after {Processed} series.", processed);
                        return;
                    }
                    await SyncSeriesAsync(id, token).ConfigureAwait(false);
                    processed++;
                    if (processed % 200 == 0)
                    {
                        _logger.LogInformation("SyncAllSeriesAsync: {Processed}/{Total} series propagated so far.", processed, ids.Count);
                    }
                }
                _logger.LogInformation("Contribution propagation: migrated {Count} series mappings to contributor database.", ids.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to propagate all series mappings to contributor database");
            }
        }

        /// <summary>
        /// Propagates the mapping state of one series into the contribution database.
        /// Safe to call unconditionally — it no-ops when the flag is off and never
        /// throws into the caller (errors are logged).
        /// </summary>
        public async Task SyncSeriesAsync(Guid seriesId, CancellationToken token = default)
        {
            if (!await IsEnabledAsync(token).ConfigureAwait(false))
                return;

            try
            {
                var series = await _db.Series
                    .Include(s => s.Sources)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == seriesId, token).ConfigureAwait(false);
                if (series == null)
                    return;

                var mappings = await _db.SeriesMappings
                    .AsNoTracking()
                    .Where(m => m.SeriesId == seriesId)
                    .ToListAsync(token).ConfigureAwait(false);

                await SyncCoreAsync(series, mappings, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to propagate contribution state for series {SeriesId}", seriesId);
            }
        }

        private async Task SyncCoreAsync(SeriesEntity series, List<SeriesMappingEntity> mappings, CancellationToken token)
        {
            // ── 1. Collect the desired titles (main + provider + alt). ──
            var rawTitles = new List<string>();
            if (!string.IsNullOrWhiteSpace(series.Title))
                rawTitles.Add(series.Title);
            foreach (var source in series.Sources)
            {
                if (!string.IsNullOrWhiteSpace(source.Title))
                    rawTitles.Add(source.Title);
            }
            foreach (var mapping in mappings)
            {
                if (!string.IsNullOrWhiteSpace(mapping.ExternalSeriesTitle))
                    rawTitles.Add(mapping.ExternalSeriesTitle);
                foreach (var alt in mapping.AlternativeTitles)
                {
                    if (!string.IsNullOrWhiteSpace(alt))
                        rawTitles.Add(alt);
                }
            }

            var uniqueTitles = rawTitles
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ── 2. Resolve / create TitleEntity rows. ──
            // The primary key itself is deterministic: MD5(Normalize(title)) → Guid, so
            // dedup happens entirely on the Id (same normalized title ⇒ same Id).
            var titleIds = await ResolveTitlesAsync(uniqueTitles, token).ConfigureAwait(false);

            // Main title identity: the canonical title for sources' Data.TitleId.
            var mainTitleId = titleIds.TryGetValue(TitleEntity.Normalize(series.Title), out var mt) ? mt : Guid.Empty;

            // ── 3. Resolve the one MappingEntity per series via its titles. ──
            var mappingId = await ResolveMappingAsync(titleIds, series.Title, token).ConfigureAwait(false);

            // ── 4. Ensure every series title is associated with the mapping. ──
            await SyncMappingTitlesAsync(mappingId, titleIds.Values, token).ConfigureAwait(false);

            // ── 5. Diff sources belonging to this mapping. ──
            await SyncSourcesAsync(series, mappings, mappingId, mainTitleId, token).ConfigureAwait(false);

            // ── 6. Diff metadata links for this mapping. ──
            await SyncMetadataAsync(mappings, mappingId, token).ConfigureAwait(false);

            await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
        }

        private async Task<Dictionary<string, Guid>> ResolveTitlesAsync(List<string> titles, CancellationToken token)
        {
            // Stable id per normalized title: MD5(Normalize(title)) → Guid.
            var desired = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var desiredIds = new HashSet<Guid>();
            foreach (var title in titles)
            {
                var key = TitleEntity.Normalize(title);
                var id = TitleEntity.DeriveId(title);
                desired[key] = id;
                desiredIds.Add(id);
            }

            // Load any rows that already exist (matched purely by primary key).
            var existing = await _contributorDb.Titles
                .Where(t => desiredIds.Contains(t.Id))
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);
            var existingIds = existing.Select(t => t.Id).ToHashSet();

            // Rows added earlier in THIS unit of work are invisible to the query above but
            // already tracked; re-adding them would raise "another instance with the same
            // key value ... is already being tracked".
            foreach (var tracked in _contributorDb.ChangeTracker.Entries<TitleEntity>().Select(e => e.Entity))
                existingIds.Add(tracked.Id);

            var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var title in titles)
            {
                var key = TitleEntity.Normalize(title);
                var id = TitleEntity.DeriveId(title);

                if (existingIds.Contains(id))
                {
                    result[key] = id;
                    continue;
                }

                _contributorDb.Titles.Add(new TitleEntity
                {
                    Id = id,
                    Title = title,
                    Version = VersionAddOrUpdate
                });
                existingIds.Add(id);
                result[key] = id;
            }

            return result;
        }

        private async Task<Guid> ResolveMappingAsync(Dictionary<string, Guid> titleIds, string mainTitle, CancellationToken token)
        {
            // All title ids resolved for this series.
            var seriesTitleIds = titleIds.Values.Distinct().ToList();
            if (seriesTitleIds.Count == 0)
                return Guid.NewGuid();

            var mappingTitles = await _contributorDb.MappingTitles
                .Where(mt => seriesTitleIds.Contains(mt.TitleId))
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);

            // Also consider links created earlier in this unit of work (e.g. by a sibling
            // series sharing a title) so they collapse onto ONE mapping instead of each
            // minting its own.
            mappingTitles.AddRange(_contributorDb.ChangeTracker.Entries<MappingTitleEntity>()
                .Select(e => e.Entity)
                .Where(mt => seriesTitleIds.Contains(mt.TitleId)));

            // Prefer the mapping that already links the main title.
            if (titleIds.TryGetValue(TitleEntity.Normalize(mainTitle), out var mainId))
            {
                var viaMain = mappingTitles.FirstOrDefault(mt => mt.TitleId == mainId)?.MappingId;
                if (viaMain.HasValue)
                    return viaMain.Value;
            }

            // Fall back to the mapping linked to any of this series' titles.
            var viaAny = mappingTitles.Select(mt => mt.MappingId).FirstOrDefault();
            if (viaAny != Guid.Empty)
                return viaAny;

            // No existing mapping → create one.
            var created = new MappingEntity { Id = Guid.NewGuid() };
            _contributorDb.Mappings.Add(created);
            return created.Id;
        }

        private async Task SyncMappingTitlesAsync(Guid mappingId, IEnumerable<Guid> titleIds, CancellationToken token)
        {
            var desired = titleIds.Distinct().ToHashSet();
            var existingRows = await _contributorDb.MappingTitles
                .Where(mt => mt.MappingId == mappingId)
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);

            var existingByTitle = existingRows.ToDictionary(mt => mt.TitleId);

            // Links created earlier in this unit of work are invisible to the query above.
            // Without them a second series sharing a title would re-Add the same
            // (MappingId, TitleId) key and EF would throw a duplicate-tracking error.
            var trackedTitleIds = _contributorDb.ChangeTracker.Entries<MappingTitleEntity>()
                .Where(e => e.Entity.MappingId == mappingId)
                .Select(e => e.Entity.TitleId)
                .ToHashSet();

            // ADD missing associations.
            foreach (var titleId in desired)
            {
                if (!existingByTitle.ContainsKey(titleId) && !trackedTitleIds.Contains(titleId))
                {
                    _contributorDb.MappingTitles.Add(new MappingTitleEntity
                    {
                        MappingId = mappingId,
                        TitleId = titleId,
                        Version = VersionAddOrUpdate
                    });
                }
            }

            // Tombstone associations that are no longer desired.
            foreach (var row in existingRows)
            {
                if (desired.Contains(row.TitleId) || row.Version == VersionLogicalDelete)
                    continue;

                // A previous SyncMappingTitlesAsync for this mapping in the same unit of work
                // may already track this (MappingId, TitleId) — existingRows is AsNoTracking,
                // so attaching the fresh copy would throw "another instance with the same key
                // value ... is already being tracked". Reuse the tracked instance instead.
                var tracked = _contributorDb.ChangeTracker.Entries<MappingTitleEntity>()
                    .Select(e => e.Entity)
                    .FirstOrDefault(e => e.MappingId == row.MappingId && e.TitleId == row.TitleId);

                if (tracked != null)
                {
                    if (tracked.Version != VersionLogicalDelete)
                    {
                        tracked.Version = VersionLogicalDelete;
                        _contributorDb.Entry(tracked).Property(mt => mt.Version).IsModified = true;
                    }
                    continue;
                }

                _contributorDb.MappingTitles.Attach(row);
                row.Version = VersionLogicalDelete;
                _contributorDb.Entry(row).Property(mt => mt.Version).IsModified = true;
            }
        }

        private async Task SyncSourcesAsync(
            SeriesEntity series,
            List<SeriesMappingEntity> mappings,
            Guid mappingId,
            Guid mainTitleId,
            CancellationToken token)
        {
            // Metadata-provider cover (e.g. ComicVine) captured on the SeriesMappings rows,
            // used as a fallback when a Mihon source has no own thumbnail (e.g. local/library
            // sources imported without covers).
            var mappingCover = mappings
                .Select(m => m.SeriesCoverUrl)
                .FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
            var existing = await _contributorDb.Series
                .Where(s => s.MappingId == mappingId)
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);
            var existingById = existing.ToDictionary(s => s.Id, s => s);

            // Series records added earlier in this unit of work are only in the tracker.
            // (Only persisted rows are tombstoned below, which is why `existing` stays as-is.)
            foreach (var tracked in _contributorDb.ChangeTracker.Entries<ContributionSeriesEntity>())
                existingById[tracked.Entity.Id] = tracked.Entity;

            // ── Pass 1: parse the desired (package, sourceId) identities ──
            var desired = new List<(SeriesProviderEntity Source, string Package, long SourceId, Guid Id, Guid SourceEntityId)>();
            var desiredIds = new HashSet<Guid>();
            var wantedSourceKeys = new HashSet<Guid>();

            foreach (var source in series.Sources)
            {
                if (string.IsNullOrWhiteSpace(source.MihonProviderId) || string.IsNullOrWhiteSpace(source.Url))
                    continue;

                string package;
                long sourceId;
                try
                {
                    (package, var sourceIdString) = source.MihonProviderId.GetPackageAndSourceId();
                    sourceId = ParseSourceId(sourceIdString);
                }
                catch
                {
                    continue;
                }

                var id = ContributionSeriesEntity.DeriveId(package, sourceId, source.Url);
                desiredIds.Add(id);
                var sourceEntityId = ContributionSourceEntity.DeriveId(package, sourceId);
                wantedSourceKeys.Add(sourceEntityId);
                desired.Add((source, package, sourceId, id, sourceEntityId));
            }

            // Load the canonical source entities that are desired (or already tracked).
            var sourceEntitiesById = (await _contributorDb.Sources
                .Where(s => wantedSourceKeys.Contains(s.Id))
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false))
                .ToDictionary(s => s.Id);

            // Sources added earlier in this unit of work are only in the tracker.
            foreach (var tracked in _contributorDb.ChangeTracker.Entries<ContributionSourceEntity>()
                         .Where(e => wantedSourceKeys.Contains(e.Entity.Id)))
            {
                sourceEntitiesById[tracked.Entity.Id] = tracked.Entity;
            }

            foreach (var (source, package, sourceId, id, sourceEntityId) in desired)
            {
                // Ensure the canonical source definition exists.
                if (!sourceEntitiesById.TryGetValue(sourceEntityId, out var sourceEntity))
                {
                    sourceEntity = new ContributionSourceEntity
                    {
                        Id = sourceEntityId,
                        Package = package,
                        SourceId = sourceId,
                        // SourceName is the EXTENSION / plugin display name (source.Provider),
                        // NOT the series title as seen on that provider (source.Title).
                        SourceName = source.Provider ?? "Unknown",
                        SourceLanguage = source.Language ?? string.Empty,
                        Version = VersionAddOrUpdate
                    };
                    _contributorDb.Sources.Add(sourceEntity);
                    sourceEntitiesById[sourceEntityId] = sourceEntity;
                }

                var record = new ContributionRecordV1
                {
                    SchemaVersion = ContributionRecordV1.CurrentSchemaVersion,
                    TitleId = mainTitleId,
                    ThumbnailUrl = string.IsNullOrWhiteSpace(source.ThumbnailUrl) ? mappingCover : source.ThumbnailUrl,
                    Status = (int)source.Status
                };
                var dataJson = JsonSerializer.Serialize(record);

                if (existingById.TryGetValue(id, out var row))
                {
                    var rowJson = JsonSerializer.Serialize(row.Data);
                    // Resurrect / update only when the payload changed (or it was tombstoned).
                    if (row.Version == VersionLogicalDelete || !string.Equals(rowJson, dataJson, StringComparison.Ordinal))
                    {
                        _contributorDb.Series.Attach(row);
                        row.Data = record;
                        row.SourceId = sourceEntityId;
                        row.Version = VersionAddOrUpdate;
                        _contributorDb.Entry(row).Property(s => s.Data).IsModified = true;
                        _contributorDb.Entry(row).Property(s => s.SourceId).IsModified = true;
                        _contributorDb.Entry(row).Property(s => s.Version).IsModified = true;
                    }
                }
                else
                {
                    _contributorDb.Series.Add(new ContributionSeriesEntity
                    {
                        Id = id,
                        MappingId = mappingId,
                        SourceId = sourceEntityId,
                        Data = record,
                        Version = VersionAddOrUpdate
                    });
                }
            }

            // Tombstone sources no longer belonging to the series (or identity changed).
            foreach (var row in existing)
            {
                if (desiredIds.Contains(row.Id) || row.Version == VersionLogicalDelete)
                    continue;

                // `existing` is AsNoTracking, so a row already tracked from an earlier call
                // for this mapping in the same unit of work is a fresh copy here; attaching
                // it would throw the identity conflict. Reuse the tracked instance instead.
                var tracked = _contributorDb.ChangeTracker.Entries<ContributionSeriesEntity>()
                    .Select(e => e.Entity)
                    .FirstOrDefault(e => e.Id == row.Id);

                if (tracked != null)
                {
                    if (tracked.Version != VersionLogicalDelete)
                    {
                        tracked.Version = VersionLogicalDelete;
                        _contributorDb.Entry(tracked).Property(s => s.Version).IsModified = true;
                    }
                    continue;
                }

                _contributorDb.Series.Attach(row);
                row.Version = VersionLogicalDelete;
                _contributorDb.Entry(row).Property(s => s.Version).IsModified = true;
            }
        }

        private async Task SyncMetadataAsync(List<SeriesMappingEntity> mappings, Guid mappingId, CancellationToken token)
        {
            var existing = await _contributorDb.Metadata
                .Where(m => m.MappingId == mappingId)
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);

            // Substitute any instance already tracked from an earlier call for this mapping in
            // the same unit of work: both the update path (Attach(activeRow)) and the tombstone
            // path (Attach(row)) below would otherwise re-attach the same persisted key and EF
            // would throw "another instance with the same key value ... is already being tracked".
            var trackedMetadataById = _contributorDb.ChangeTracker
                .Entries<ContributionMetadataEntity>()
                .ToDictionary(e => e.Entity.Id, e => e.Entity);
            for (int i = 0; i < existing.Count; i++)
            {
                if (trackedMetadataById.TryGetValue(existing[i].Id, out var trackedMeta))
                    existing[i] = trackedMeta;
            }

            // Group the desired provider rows: rensaio.db can technically hold both a
            // (provider, "") Unmatched row AND a (provider, id) matched row. For the
            // contribution DB we enforce ONE active row per (mapping, provider) — the
            // strongest decision wins (UserConfirmed > AutoMatched > non-empty key > Unmatched).
            var desiredByProvider = new Dictionary<int, SeriesMappingEntity>();
            foreach (var mapping in mappings)
            {
                int pid = (int)mapping.Provider;
                var existingStronger = desiredByProvider.TryGetValue(pid, out var cur) && StrongerThan(cur, mapping);
                if (existingStronger)
                    continue;
                desiredByProvider[pid] = mapping;
            }

            // Track identities we actually want to keep active.
            var desiredActive = new HashSet<Guid>();

            foreach (var (pid, mapping) in desiredByProvider)
            {
                // Find the best EXISTING active row for this provider to reuse (matches
                // the same key first, then any active row) so we never accumulate two rows.
                var byKey = existing.FirstOrDefault(m => m.ProviderId == pid
                    && string.Equals(m.ProviderKey, mapping.ExternalSeriesId ?? "", StringComparison.Ordinal)
                    && m.Version != VersionLogicalDelete);
                var activeRow = byKey
                    ?? existing.FirstOrDefault(m => m.ProviderId == pid && m.Version != VersionLogicalDelete);

                if (activeRow != null)
                {
                    desiredActive.Add(activeRow.Id);
                    var statusChanged = activeRow.MappingStatus != mapping.MappingStatus
                        || !string.Equals(activeRow.ProviderKey, mapping.ExternalSeriesId ?? "", StringComparison.Ordinal)
                        || activeRow.LinkedDate != mapping.LinkedDate
                        || activeRow.Version == VersionLogicalDelete;
                    if (statusChanged)
                    {
                        _contributorDb.Metadata.Attach(activeRow);
                        activeRow.ProviderKey = mapping.ExternalSeriesId ?? string.Empty;
                        activeRow.MappingStatus = mapping.MappingStatus;
                        activeRow.LinkedDate = mapping.LinkedDate;
                        activeRow.Version = VersionAddOrUpdate;
                        _contributorDb.Entry(activeRow).Property(m => m.ProviderKey).IsModified = true;
                        _contributorDb.Entry(activeRow).Property(m => m.MappingStatus).IsModified = true;
                        _contributorDb.Entry(activeRow).Property(m => m.LinkedDate).IsModified = true;
                        _contributorDb.Entry(activeRow).Property(m => m.Version).IsModified = true;
                    }
                }
                else
                {
                    var created = new ContributionMetadataEntity
                    {
                        Id = Guid.NewGuid(),
                        MappingId = mappingId,
                        ProviderId = pid,
                        ProviderKey = mapping.ExternalSeriesId ?? string.Empty,
                        MappingStatus = mapping.MappingStatus,
                        LinkedDate = mapping.LinkedDate,
                        Version = VersionAddOrUpdate
                    };
                    _contributorDb.Metadata.Add(created);
                    desiredActive.Add(created.Id);
                }
            }

            // Tombstone any remaining active rows for a provider NOT desired, and collapse
            // sibling duplicates: keep only the single row we want active per provider.
            var desiredProviders = desiredByProvider.Keys.ToHashSet();
            foreach (var row in existing)
            {
                if (row.Version == VersionLogicalDelete)
                    continue;
                if (desiredActive.Contains(row.Id) || !desiredProviders.Contains(row.ProviderId))
                {
                    // Not desired (entirely) or it IS our kept row — skip. Rows for providers
                    // that are NOT in the desired set are tombstoned below.
                }
                else
                {
                    _contributorDb.Metadata.Attach(row);
                    row.Version = VersionLogicalDelete;
                    _contributorDb.Entry(row).Property(m => m.Version).IsModified = true;
                }
            }
        }

        /// <summary>True when <paramref name="a"/> is a stronger user/decision state than <paramref name="b"/>.</summary>
        private static bool StrongerThan(SeriesMappingEntity a, SeriesMappingEntity b)
        {
            int Rank(SeriesMappingEntity m) => m.MappingStatus switch
            {
                SeriesMappingStatus.UserConfirmed => 5,
                SeriesMappingStatus.Blocked => 4,
                SeriesMappingStatus.AutoMatched => 3,
                SeriesMappingStatus.ForeverIgnored => 2,
                SeriesMappingStatus.TemporaryIgnored => 1,
                _ => 0,
            };
            if (Rank(a) != Rank(b)) return Rank(a) > Rank(b);
            // Same status: prefer the row that carries a real external id.
            return !string.IsNullOrWhiteSpace(a.ExternalSeriesId)
                && string.IsNullOrWhiteSpace(b.ExternalSeriesId);
        }

        private static long ParseSourceId(string value)
        {
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        }
    }
}