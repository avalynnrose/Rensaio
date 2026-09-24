using RensaioBackend.Data;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Services.Contributions.Snapshot;
using RensaioBackend.Services.Settings;
using Microsoft.EntityFrameworkCore;

namespace RensaioBackend.Services.Contributions
{
    /// <summary>
    /// Daily/startup import of the cloud contribution snapshot into the local
    /// Contribution DB, using the GitHub `metadata.bin` export as the source of truth.
    ///
    /// RUNS FOR ALL USERS regardless of the Contribution settings: the export is the
    /// community's public read-only dataset (public export key + GitHub raw metadata.bin),
    /// so every client consumes it daily to keep the local contribution DB + mappings in
    /// sync. The Contribution settings only gate UPLOADING the user's own changes (and the
    /// Contribution page), not consuming the shared dataset.
    ///
    /// Pipeline:
    ///   1) GET {ContributionServerUrl}/key → base64(32B AES key + 16B IV)
    ///   2) GET https://raw.githubusercontent.com/{ExportRepo}/main/metadata.bin (base64)
    ///   3) Decode: base64 → AES-256-CBC decrypt → strip tag → ZSTD/Brotli decompress → protobuf
    ///   4) Apply with source-of-truth semantics:
    ///        A) delete local rows with Version == -2 or -3 (already uploaded; server supersedes)
    ///        B) preserve local rows with Version == 0 or -1 (pending; user edits win)
    ///        C) overwrite clean/uploaded rows that exist in the snapshot (server content at its version)
    ///        D) add rows present in the snapshot that don't exist locally
    ///        E) import stamps rows Clean (Version = -2) so they are not re-uploaded
    ///        F) VACUUM (SQLite shrink) after the bulk copy
    ///   5) Set SyncState.LastServerVersion = snapshot version (cursor for incremental skip)
    ///   6) (Separate phase) propagate Auto/User matches into rensaio.db SeriesMappings.
    ///
    /// The whole apply runs under an exclusive gate (caller acquires <see cref="ContributionDbGate"/>)
    /// so no other service touches the DB mid-copy.
    /// </summary>
    public class ContributionImportService
    {
        public const string DefaultExportRepo = "maxpiva/Rensaio-Metadata";
        private const int VersionPendingAdd = 0;    // local add/update, not yet uploaded
        private const int VersionPendingDelete = -1; // local delete, not yet uploaded
        private const int VersionUploaded = -2;     // uploaded (or imported clean)
        private const int VersionUploadedDelete = -3; // uploaded delete marker

        private readonly ContributionDbContext _contributorDb;
        private readonly SettingsService _settingsService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<ContributionImportService> _logger;

        public ContributionImportService(
            ContributionDbContext contributorDb,
            SettingsService settingsService,
            IHttpClientFactory httpClientFactory,
            ILogger<ContributionImportService> logger)
        {
            _contributorDb = contributorDb;
            _settingsService = settingsService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>
        /// Runs the full import: fetch metadata.bin + key, decode, apply, persist cursor, VACUUM.
        /// </summary>
        public async Task<ContributionImportResult> ImportAsync(CancellationToken token = default)
        {
            var settings = await _settingsService.GetSettingsAsync(token).ConfigureAwait(false);

            // Universal read-only import: NOT gated by ContributionEnabled / ContributionVerified.
            // The export key + metadata.bin are the public community dataset consumed by every
            // client (SRP: uploads are opt-in via settings; consuming the shared graph is not).
            // Only a missing server URL blocks the import (the export repo has a safe default).
            if (string.IsNullOrWhiteSpace(settings.ContributionServerUrl))
                return ContributionImportResult.Failed("Contribution server url is not configured.");

            using var client = _httpClientFactory.CreateClient("ContributionImport");

            // 2) metadata.bin + optional SHA-256 sidecar dedup.
            // The worker pushes base64 to the GitHub Contents API, but GitHub stores the
            // DECODED binary — so `raw.githubusercontent.com/.../metadata.bin` serves the raw
            // encrypted stream (ALREADY binary, no base64 layer on the client). The sidecar
            // metadata.bin.sha256 = base64(SHA-256 of those raw bytes). We hash what we pull
            // and compare; when the hash matches the last-imported one the file is unchanged →
            // skip the whole import (no key fetch, no decode, no apply, no VACUUM). If the
            // sidecar is absent (old export repos / transfer errors) we skip the check and
            // continue with the full import.
            string repo = string.IsNullOrWhiteSpace(settings.ContributionExportRepo) ? DefaultExportRepo : settings.ContributionExportRepo!;
            string binUrl = $"https://raw.githubusercontent.com/{repo}/refs/heads/main/metadata.bin";
            byte[] encryptedPayload;
            try
            {
                encryptedPayload = await client.GetByteArrayAsync(binUrl, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch contribution metadata.bin from {Url}", binUrl);
                return ContributionImportResult.Failed($"Failed to fetch metadata.bin: {ex.Message}");
            }

            try
            {
                var previousHash = await _settingsService.GetSettingValueAsync(LastHashSetting, token).ConfigureAwait(false);
                var sidecarHash = await TryFetchSidecarSha256Async(client, repo, token).ConfigureAwait(false);
                if (sidecarHash != null)
                {
                    // Hash over the raw BINARY bytes served by GitHub — matches the worker side
                    // (the sidecar is base64(SHA-256(raw bytes of the file GitHub serves))).
                    var currentHash = ComputeBase64Sha256(encryptedPayload);
                    if (currentHash == sidecarHash && previousHash == currentHash)
                    {
                        _logger.LogInformation("Contribution metadata.bin unchanged (hash {Hash}); skipping import.", currentHash);
                        return ContributionImportResult.Skipped();
                    }
                }
                // else: no sidecar → proceed with the full import (no dedup possible).
            }
            catch (Exception ex)
            {
                // Dedup is best-effort: any failure to read the sidecar/hash must never block the import.
                _logger.LogWarning(ex, "Contribution metadata.bin dedup check failed; continuing with full import.");
            }

            // 1) Key
            string keyEndpoint = settings.ContributionServerUrl.TrimEnd('/') + "/key";
            string aeskey256iv;
            try
            {
                aeskey256iv = await client.GetStringAsync(keyEndpoint, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch contribution export key from {Endpoint}", keyEndpoint);
                return ContributionImportResult.Failed($"Failed to fetch export key: {ex.Message}");
            }

            // 3) Decode
            ContributionSnapshotV1 snapshot;
            try
            {
                byte[] protobuf = ContributionExportDecoder.DecodeMetadataBin(encryptedPayload, aeskey256iv);
                snapshot = ContributionProtobufCodec.DecodeSnapshot(protobuf);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decode contribution metadata.bin");
                return ContributionImportResult.Failed($"Failed to decode metadata.bin: {ex.Message}");
            }

            // 4) Apply (source-of-truth semantics)
            var applied = await ApplyAsync(snapshot, token).ConfigureAwait(false);
            if (applied.Success)
            {
                // Persist the new hash so the next daily run can dedup against it.
                await _settingsService.SetSettingValueAsync(LastHashSetting, ComputeBase64Sha256(encryptedPayload), token).ConfigureAwait(false);
            }
            return applied;
        }

        private const string LastHashSetting = "ContributionMetadataBinSha256";

        /// <summary>base64(SHA-256) of the raw binary bytes of metadata.bin (what GitHub serves).</summary>
        private static string ComputeBase64Sha256(byte[] bytes)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return System.Convert.ToBase64String(sha.ComputeHash(bytes));
        }

        /// <summary>
        /// Downloads the publisher's SHA-256 sidecar (metadata.bin.sha256) and returns its trimmed
        /// value, or null when the file does not exist (older export repos) — the caller then skips
        /// the dedup check and proceeds with the full import.
        /// </summary>
        private async Task<string?> TryFetchSidecarSha256Async(HttpClient client, string repo, CancellationToken token)
        {
            string sidecarUrl = $"https://raw.githubusercontent.com/{repo}/refs/heads/main/metadata.bin.sha256";
            var body = await client.GetByteArrayAsync(sidecarUrl, token).ConfigureAwait(false);
            return Convert.ToBase64String(body);
        }

        /// <summary>
        /// Applies a decoded snapshot to the local Contribution DB with the target semantics.
        /// Public so the /snapshot path and tests can reuse it.
        /// </summary>
        public async Task<ContributionImportResult> ApplyAsync(ContributionSnapshotV1 snapshot, CancellationToken token = default)
        {
            // Snapshot rows carry server `v` (Version field on the DTO) — but per the corrected
            // version semantics we ignore that and stamp imported rows Clean (-2) locally.
            try
            {
                // A) delete uploaded rows (-2 / -3) — server truth supersedes them.
                int deletedUploaded = 0;
                deletedUploaded += await _contributorDb.Database
                    .ExecuteSqlRawAsync(
                        "DELETE FROM Titles WHERE Version IN (-2, -3)", token).ConfigureAwait(false);
                deletedUploaded += await _contributorDb.Database
                    .ExecuteSqlRawAsync(
                        "DELETE FROM Metadata WHERE Version IN (-2, -3)", token).ConfigureAwait(false);

                // ── Titles ──
                var snapshotTitleIds = snapshot.Titles.Select(t => t.Id).ToList();
                var localTitles = await _contributorDb.Titles
                    .Where(t => snapshotTitleIds.Contains(t.Id))
                    .ToListAsync(token).ConfigureAwait(false);
                var localTitleById = localTitles.ToDictionary(t => t.Id);

                foreach (var cloud in snapshot.Titles)
                {
                    if (localTitleById.TryGetValue(cloud.Id, out var local))
                    {
                        // B) pending (0/-1) preserved → skip; C) clean row overwritten with server content.
                        if (local.Version == VersionPendingAdd || local.Version == VersionPendingDelete)
                            continue;
                        local.Title = cloud.Title;
                        local.Version = VersionUploaded; // C + E: server content, stamped clean
                        _contributorDb.Entry(local).Property(t => t.Title).IsModified = true;
                        _contributorDb.Entry(local).Property(t => t.Version).IsModified = true;
                    }
                    else
                    {
                        // D + E: new row, stamped Clean.
                        _contributorDb.Titles.Add(new TitleEntity
                        {
                            Id = cloud.Id,
                            Title = cloud.Title,
                            Version = VersionUploaded
                        });
                    }
                }

                // ── Sources (init-only fields: create-only; existing clean rows overwrite Version; the
                //    identity/display fields are immutable once created — server wins on first create). ──
                var snapshotSourceIds = snapshot.Sources.Select(s => s.Id).ToList();
                var localSources = await _contributorDb.Sources
                    .Where(s => snapshotSourceIds.Contains(s.Id))
                    .AsNoTracking()
                    .ToListAsync(token).ConfigureAwait(false);
                var localSourceById = localSources.ToDictionary(s => s.Id);

                foreach (var cloud in snapshot.Sources)
                {
                    if (localSourceById.TryGetValue(cloud.Id, out var local))
                    {
                        if (local.Version == VersionPendingAdd || local.Version == VersionPendingDelete)
                            continue;
                        var attached = localSources.First(l => l.Id == local.Id); // tracked copy
                        _contributorDb.Sources.Attach(attached);
                        attached.Version = VersionUploaded;
                        _contributorDb.Entry(attached).Property(s => s.Version).IsModified = true;
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
                            Version = VersionUploaded
                        });
                    }
                }

                // ── Mappings (cloud → local by title overlap) ──
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

                // Pairs already tracked (added/attached earlier in this unit of work) plus
                // pairs queued by THIS loop. The DB-only AnyAsync is blind to both, so two
                // cloud mappings resolving to one local mapping would Add the same
                // (MappingId, TitleId) twice → EF identity conflict on the composite key.
                var knownPairs = _contributorDb.ChangeTracker.Entries<MappingTitleEntity>()
                    .Select(e => (e.Entity.MappingId, e.Entity.TitleId))
                    .ToHashSet();

                foreach (var (cloudMappingId, titleIds) in cloudMappingTitlesById)
                {
                    var localMappingId = await ResolveLocalMappingAsync(titleIds, token).ConfigureAwait(false);
                    localByCloudMapping[cloudMappingId] = localMappingId;

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
                                Version = VersionUploaded
                            });
                        }
                    }
                }

                // ── Mappings reconcile (title unglue) ──
                // The cloud is the source of truth for CLEAN rows: a title that the cloud moved
                // to a different mapping (mapping-conflict repair) must stop being associated with
                // the stale local mapping. Tombstone local Clean/Uploaded mapping_titles rows
                // (Version -2/-3) that are no longer present in the snapshot; local pending rows
                // (Version 0/-1, user edits) are preserved. The tombstoned rows are then uploaded
                // as deletes so the cloud worker archives them too.
                var desiredPairs = new HashSet<(Guid MappingId, Guid TitleId)>();
                foreach (var (cloudMappingId, titleIds) in cloudMappingTitlesById)
                {
                    if (!localByCloudMapping.TryGetValue(cloudMappingId, out var localMappingId))
                        continue;
                    foreach (var titleId in titleIds.Distinct())
                        desiredPairs.Add((localMappingId, titleId));
                }
                var localMappingIds = localByCloudMapping.Values.Distinct().ToList();
                var localCleanMappingTitles = await _contributorDb.MappingTitles
                    .Where(mt => localMappingIds.Contains(mt.MappingId)
                        && (mt.Version == VersionUploaded || mt.Version == VersionUploadedDelete))
                    .ToListAsync(token).ConfigureAwait(false);
                foreach (var mt in localCleanMappingTitles)
                {
                    if (desiredPairs.Contains((mt.MappingId, mt.TitleId))) continue;
                    _contributorDb.MappingTitles.Attach(mt);
                    mt.Version = VersionPendingDelete;
                    _contributorDb.Entry(mt).Property(x => x.Version).IsModified = true;
                }

                // ── Series (deterministic ids, mapping re-keyed) ──
                var snapshotSeriesIds = snapshot.Series.Select(s => s.Id).ToList();
                var localSeries = await _contributorDb.Series
                    .Where(s => snapshotSeriesIds.Contains(s.Id))
                    .ToListAsync(token).ConfigureAwait(false);
                var localSeriesById = localSeries.ToDictionary(s => s.Id);

                foreach (var cloud in snapshot.Series)
                {
                    if (!localByCloudMapping.TryGetValue(cloud.MappingId, out var localMappingId))
                    {
                        localMappingId = await ResolveLocalMappingAsync(new[] { cloud.Data.TitleId }, token)
                            .ConfigureAwait(false);
                        localByCloudMapping[cloud.MappingId] = localMappingId;
                    }

                    if (localSeriesById.TryGetValue(cloud.Id, out var local))
                    {
                        if (local.Version == VersionPendingAdd || local.Version == VersionPendingDelete)
                            continue;
                        local.MappingId = localMappingId;
                        local.SourceId = cloud.SourceId;
                        local.Data = cloud.Data;
                        local.Version = VersionUploaded;
                        _contributorDb.Entry(local).Property(s => s.MappingId).IsModified = true;
                        _contributorDb.Entry(local).Property(s => s.SourceId).IsModified = true;
                        _contributorDb.Entry(local).Property(s => s.Data).IsModified = true;
                        _contributorDb.Entry(local).Property(s => s.Version).IsModified = true;
                    }
                    else
                    {
                        _contributorDb.Series.Add(new ContributionSeriesEntity
                        {
                            Id = cloud.Id,
                            MappingId = localMappingId,
                            SourceId = cloud.SourceId,
                            Data = cloud.Data,
                            Version = VersionUploaded
                        });
                    }
                }

                // ── Metadata (dedup by (localMappingId, ProviderId, ProviderKey)) ──
                var snapshotMetadata = await _contributorDb.Metadata
                    .Where(m => localByCloudMapping.Values.Contains(m.MappingId))
                    .ToListAsync(token).ConfigureAwait(false);
                var localMetaByKey = snapshotMetadata
                    .GroupBy(m => m.MappingId)
                    .ToDictionary(g => g.Key, g => g.ToDictionary(m => (m.ProviderId, m.ProviderKey ?? string.Empty)));

                foreach (var cloud in snapshot.Metadata)
                {
                    if (!localByCloudMapping.TryGetValue(cloud.MappingId, out var localMappingId))
                    {
                        localMappingId = await ResolveLocalMappingAsync(Array.Empty<Guid>(), token).ConfigureAwait(false);
                        localByCloudMapping[cloud.MappingId] = localMappingId;
                    }

                    var key = (cloud.ProviderId, cloud.ProviderKey ?? string.Empty);
                    ContributionMetadataEntity? local = null;
                    if (localMetaByKey.TryGetValue(localMappingId, out var byKey))
                        byKey.TryGetValue(key, out local);

                    if (local != null)
                    {
                        if (local.Version == VersionPendingAdd || local.Version == VersionPendingDelete)
                            continue;
                        local.MappingStatus = cloud.MappingStatus;
                        local.LinkedDate = cloud.LinkedDate;
                        local.Version = VersionUploaded;
                        _contributorDb.Entry(local).Property(m => m.MappingStatus).IsModified = true;
                        _contributorDb.Entry(local).Property(m => m.LinkedDate).IsModified = true;
                        _contributorDb.Entry(local).Property(m => m.Version).IsModified = true;
                    }
                    else
                    {
                        _contributorDb.Metadata.Add(new ContributionMetadataEntity
                        {
                            Id = Guid.NewGuid(),
                            MappingId = localMappingId,
                            ProviderId = cloud.ProviderId,
                            ProviderKey = cloud.ProviderKey ?? string.Empty,
                            MappingStatus = cloud.MappingStatus,
                            LinkedDate = cloud.LinkedDate,
                            Version = VersionUploaded
                        });
                    }
                }

                // Persist everything.
                await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);

                // F) VACUUM — shrink after the bulk copy.
                await _contributorDb.Database.ExecuteSqlRawAsync("VACUUM;", token).ConfigureAwait(false);

                // 5) Cursor
                var sync = await _contributorDb.SyncStates.FirstOrDefaultAsync(s => s.Id == 1, token)
                    .ConfigureAwait(false);
                if (sync == null)
                {
                    _contributorDb.SyncStates.Add(new SyncStateEntity { Id = 1, LastServerVersion = snapshot.Version });
                }
                else
                {
                    sync.LastServerVersion = snapshot.Version;
                    _contributorDb.Entry(sync).Property(s => s.LastServerVersion).IsModified = true;
                }
                await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);

                return ContributionImportResult.Succeeded(
                    snapshot.Titles.Count, snapshot.Sources.Count, snapshot.Series.Count,
                    snapshot.Metadata.Count, deletedUploaded, snapshot.Version);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Contribution import apply failed");
                return ContributionImportResult.Failed($"Import apply failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Title-overlap mapping resolution (same rule as the downloader/propagation).
        /// </summary>
        internal async Task<Guid> ResolveLocalMappingAsync(IEnumerable<Guid> titleIds, CancellationToken token)
        {
            var distinct = titleIds.Distinct().ToList();
            if (distinct.Count == 0)
            {
                var m = new MappingEntity { Id = Guid.NewGuid() };
                _contributorDb.Mappings.Add(m);
                return m.Id;
            }

            var rows = await _contributorDb.MappingTitles
                .Where(mt => distinct.Contains(mt.TitleId))
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);
            var overlap = new Dictionary<Guid, int>();
            foreach (var r in rows)
                overlap[r.MappingId] = overlap.GetValueOrDefault(r.MappingId) + 1;

            var best = overlap.OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (best.Key != Guid.Empty)
                return best.Key;

            var created = new MappingEntity { Id = Guid.NewGuid() };
            _contributorDb.Mappings.Add(created);
            return created.Id;
        }
    }

    /// <summary>Result of a contribution import attempt.</summary>
    public sealed class ContributionImportResult
    {
        public bool Success { get; private init; }
        public string? Error { get; private init; }

        /// <summary>True when the daily import was skipped because the remote metadata.bin was unchanged.</summary>
        public bool WasSkipped { get; private init; }
        public int Titles { get; private init; }
        public int Sources { get; private init; }
        public int Series { get; private init; }
        public int Metadata { get; private init; }
        public int DeletedUploaded { get; private init; }
        public int ServerVersion { get; private init; }

        public static ContributionImportResult Succeeded(
            int titles, int sources, int series, int metadata, int deletedUploaded, int serverVersion) => new()
            {
                Success = true,
                Titles = titles,
                Sources = sources,
                Series = series,
                Metadata = metadata,
                DeletedUploaded = deletedUploaded,
                ServerVersion = serverVersion,
            };

        /// <summary>Import was a no-op — the remote metadata.bin hash matches the last imported one.</summary>
        public static ContributionImportResult Skipped() => new()
        {
            Success = true,
            WasSkipped = true,
        };

        public static ContributionImportResult Failed(string error) => new()
        {
            Success = false,
            Error = error,
        };
    }
}