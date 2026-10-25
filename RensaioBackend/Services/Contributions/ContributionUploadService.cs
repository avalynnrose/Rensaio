using RensaioBackend.Data;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Services.Contributions.Snapshot;
using RensaioBackend.Services.Settings;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// Uploads the local contribution database's pending changes (Version 0 = add/update,
/// -1 = delete) to the Cloudflare contribution worker (/upload), then marks them
/// uploaded: 0 → -2 (Uploaded), -1 → -3 (UploadedDelete). The two uploaded variants
/// behave exactly like their pending counterparts — they only mean "already exported".
/// See <see cref="ContributionVersions"/>.
/// </summary>
public class ContributionUploadService
{
    private const int VersionAddOrUpdate = ContributionVersions.AddOrUpdate;
    private const int VersionLogicalDelete = ContributionVersions.PendingDelete;
    private const int VersionUploaded = ContributionVersions.Uploaded;
    private const int VersionUploadedDelete = ContributionVersions.UploadedDelete;

    private readonly ContributionDbContext _contributorDb;
    private readonly SettingsService _settingsService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IContributionUploadQueue _queue;
    private readonly ILogger<ContributionUploadService> _logger;

    public ContributionUploadService(
        ContributionDbContext contributorDb,
        SettingsService settingsService,
        IHttpClientFactory httpClientFactory,
        IContributionUploadQueue queue,
        ILogger<ContributionUploadService> logger)
    {
        _contributorDb = contributorDb;
        _settingsService = settingsService;
        _httpClientFactory = httpClientFactory;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>
    /// Fast path used by the controller — enqueues an upload request and returns
    /// immediately. The actual upload runs in the background hosted service.
    /// </summary>
    public bool EnqueueUpload()
    {
        return _queue.Enqueue();
    }

    /// <summary>
    /// Builds a ContributionSnapshotV1 from all rows with Version 0 or -1, POSTs it to
    /// the worker, and on success flips those rows to Version -2 (Uploaded).
    ///
    /// The snapshot is sent in MULTIPLE bounded requests (see <see cref="BuildBatches"/>).
    /// A single /upload carrying the whole dataset makes one Worker invocation run every
    /// row's D1 statement (≈5 per series), which exceeds the Cloudflare Worker CPU/time
    /// limit for large exports. Each batch is a self-contained snapshot slice whose rows
    /// are internally consistent (a mapping's titles never straddle two batches), so the
    /// worker's per-request mapping-id resolution and single-ownership rules stay correct.
    ///
    /// When <paramref name="force"/> is set, ALL local rows (including already-uploaded
    /// Version -2) are included — used after a cloud wipe to re-upload the full dataset.
    /// </summary>
    /// <returns>Upload result with per-entity counts, or an error message.</returns>
    public async Task<ContributionUploadResult> UploadPendingChangesAsync(
        bool force = false, CancellationToken token = default)
    {
        var settings = await _settingsService.GetSettingsAsync(token).ConfigureAwait(false);
        if (!settings.ContributionEnabled)
        {
            return ContributionUploadResult.Failed("Contribution is disabled in settings.");
        }

        if (!settings.ContributionVerified)
        {
            return ContributionUploadResult.Failed("Contribution contributor id has not been verified against the contribution database.");
        }

        if (string.IsNullOrWhiteSpace(settings.ContributionContributorId))
        {
            return ContributionUploadResult.Failed("Contribution contributor id is not configured.");
        }

        if (string.IsNullOrWhiteSpace(settings.ContributionServerUrl))
        {
            return ContributionUploadResult.Failed("Contribution server url is not configured.");
        }

        // Load pending rows (Version 0 = add/update, -1 = delete). With force,
        // include already-uploaded rows (-2 live, -3 uploaded delete) so a cloud wipe
        // is repopulated with the full dataset.
        var pendingVersions = force
            ? new[] { VersionAddOrUpdate, VersionLogicalDelete, VersionUploaded, VersionUploadedDelete }
            : new[] { VersionAddOrUpdate, VersionLogicalDelete };

        var titles = await _contributorDb.Titles
            .Where(t => pendingVersions.Contains(t.Version))
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        var mappingTitles = await _contributorDb.SeriesTitles
            .Where(mt => pendingVersions.Contains(mt.Version))
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        var sources = await _contributorDb.Sources
            .Where(s => pendingVersions.Contains(s.Version))
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        var series = await _contributorDb.SeriesEntries
            .Where(s => pendingVersions.Contains(s.Version))
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        var metadata = await _contributorDb.Metadata
            .Where(m => pendingVersions.Contains(m.Version))
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        var sourceSeries = await _contributorDb.SourceSeries
            .Where(ss => pendingVersions.Contains(ss.Version))
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        int total =
            titles.Count + mappingTitles.Count + sources.Count +
            series.Count + metadata.Count + sourceSeries.Count;

        if (total == 0)
        {
            return ContributionUploadResult.Succeeded(0, 0, 0, 0, 0, 0, 0);
        }

        // Force re-upload: rows already marked Uploaded (-2 live, -3 uploaded delete)
        // must NOT be serialized as v:-2/-3 — the worker's validateRow only accepts 0
        // (upsert) or -1 (delete). Normalize -2 → 0 and -3 → -1 so the full dataset is
        // re-sent using only the two wire versions the worker understands.
        if (force)
        {
            NormalizeVersionsForWire(titles, mappingTitles, sources, series, metadata, sourceSeries);
        }

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        string endpoint = settings.ContributionServerUrl.TrimEnd('/') +
            "/upload?contributor=" + Uri.EscapeDataString(settings.ContributionContributorId);

        List<UploadBatch> batches = BuildBatches(titles, mappingTitles, sources, series, metadata, sourceSeries);
        _logger.LogInformation(
            "Contribution upload: {Total} rows split into {Batches} request(s) to stay under the worker CPU limit.",
            total, batches.Count);

        var failedLists = new HashSet<string>(StringComparer.Ordinal);
        var errorMessages = new List<string>();
        int processedTotal = 0;

        using var client = _httpClientFactory.CreateClient("ContributionUpload");
        try
        {
            foreach (UploadBatch batch in batches)
            {
                // Same 1-letter JSON field names the worker expects; the top-level Version is
                // informational (the worker merges blindly).
                var snapshot = new ContributionSnapshotV1
                {
                    SchemaVersion = ContributionSnapshotV1.CurrentSchemaVersion,
                    GeneratedUtc = DateTime.UtcNow,
                    Version = 0,
                    Titles = batch.Titles,
                    Mappings = batch.Mappings,
                    Sources = batch.Sources,
                    Series = batch.Series,
                    Metadata = batch.Metadata,
                    SourceSeries = batch.SourceSeries,
                };

                using var response = await client.PostAsJsonAsync(endpoint, snapshot, options, token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    return ContributionUploadResult.Failed(
                        $"Contribution worker rejected upload ({response.StatusCode}): {Truncate(body, 500)}");
                }

                // Parse the worker's JSON response. `errors` contains per-row failures
                // (entity list + index); a subset may still have been persisted.
                string responseBody = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                var workerResponse = DeserializeWorkerResponse(responseBody);
                processedTotal += workerResponse?.Processed ?? batch.Total;

                if (workerResponse?.Errors is { Count: > 0 })
                {
                    foreach (var error in workerResponse.Errors)
                        failedLists.Add(error.List);
                    errorMessages.AddRange(workerResponse.Errors.Select(e => e.ToString()));
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Contribution upload HTTP failure after {Processed} rows", processedTotal);
            return ContributionUploadResult.Failed(
                $"Failed to reach contribution worker: {ex.Message}");
        }

        // Mark uploaded ONCE, after every request returned HTTP 200. Rows whose list reported
        // per-row errors stay pending so a retry re-sends just them; everything else is done.
        string? workerErrors = errorMessages.Count > 0
            ? string.Join("; ", errorMessages.Distinct().Take(50))
            : null;

        if (failedLists.Count > 0)
        {
            _logger.LogWarning(
                "Contribution worker reported {ErrorCount} per-row errors across {Batches} request(s): {Errors}",
                errorMessages.Count, batches.Count, workerErrors);
            await MarkUploadedForListsAsync(failedLists, token).ConfigureAwait(false);
        }
        else
        {
            await MarkUploadedAsync(token).ConfigureAwait(false);
        }

        return ContributionUploadResult.Succeeded(
            titles.Count, mappingTitles.Count, sources.Count,
            series.Count, metadata.Count, total, processedTotal, workerErrors);
    }

    /// <summary>
    /// Max rows per /upload request. Keeps each Worker invocation's D1 statement count
    /// (≈1 per row; a mapping row costs a couple more) well under the Cloudflare CPU/time
    /// budget. Batches are grouped by mapping, so this tracks series rather than raw rows.
    /// </summary>
    private const int RowsPerUploadBatch = 2000;

    /// <summary>
    /// One self-contained ContributionSnapshotV1 slice. Every id set is internally consistent:
    /// a mapping's titles + series records always travel together, so the worker can resolve
    /// mapping ids and enforce single-title-ownership within each request.
    /// </summary>
    private sealed class UploadBatch
    {
        public List<TitleEntity> Titles { get; } = [];
        public List<SeriesTitleEntity> Mappings { get; } = [];
        public List<ContributionSourceEntity> Sources { get; } = [];
        public List<ContributionSeriesEntity> Series { get; } = [];
        public List<ContributionMetadataEntity> Metadata { get; } = [];
        public List<ContributionSourceSeriesEntity> SourceSeries { get; } = [];

        public int Total =>
            Titles.Count + Mappings.Count + Sources.Count + Series.Count + Metadata.Count + SourceSeries.Count;
    }

    /// <summary>
    /// Splits the upload into bounded, self-contained batches grouped by mapping so a mapping's
    /// titles/series never straddle two requests (keeping the worker's mapping-id resolution and
    /// single-ownership rules correct). The shared source registry is included in every batch
    /// (idempotent upsert); orphan titles and all metadata rows go to the last batch.
    /// </summary>
    private static List<UploadBatch> BuildBatches(
        List<TitleEntity> titles,
        List<SeriesTitleEntity> mappingTitles,
        List<ContributionSourceEntity> sources,
        List<ContributionSeriesEntity> series,
        List<ContributionMetadataEntity> metadata,
        List<ContributionSourceSeriesEntity> sourceSeries)
    {
        var titlesById = titles.ToDictionary(t => t.Id);

        // Combine mapping-titles and series rows that share a mapping id into one unit, so a
        // mapping (its title links + its series records) is never split across batches.
        var seriesByMapping = series.GroupBy(s => s.SeriesId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var mappingTitlesByMapping = mappingTitles.GroupBy(mt => mt.SeriesId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var units = new List<(Guid MappingId, int Weight)>();
        foreach (var mappingId in seriesByMapping.Keys.Concat(mappingTitlesByMapping.Keys).Distinct())
        {
            int weight = (seriesByMapping.TryGetValue(mappingId, out var s) ? s.Count : 0)
                + (mappingTitlesByMapping.TryGetValue(mappingId, out var mt) ? mt.Count : 0);
            units.Add((mappingId, Math.Max(weight, 1)));
        }

        // Deterministic order so retries/resends are stable and batches are reproducible.
        units.Sort((a, b) => a.MappingId.CompareTo(b.MappingId));

        var batches = new List<UploadBatch>();
        var current = new UploadBatch();
        var emittedTitleIds = new HashSet<Guid>();

        foreach (var unit in units)
        {
            if (current.Total > 0 && current.Total + unit.Weight > RowsPerUploadBatch)
            {
                batches.Add(current);
                current = new UploadBatch();
            }

            if (mappingTitlesByMapping.TryGetValue(unit.MappingId, out var mts))
            {
                foreach (var mt in mts)
                {
                    current.Mappings.Add(mt);
                    // Emit the referenced title in the SAME batch so the worker's per-request
                    // FK/upsert ordering (t → m → i → d) is satisfied.
                    if (titlesById.TryGetValue(mt.TitleId, out var title) && emittedTitleIds.Add(title.Id))
                        current.Titles.Add(title);
                }
            }

            if (seriesByMapping.TryGetValue(unit.MappingId, out var srs))
                current.Series.AddRange(srs);
        }

        if (current.Total > 0)
            batches.Add(current);

        // Orphan titles (not referenced by any mapping row) and every metadata row go to the
        // last batch — metadata carries its own mapping reference via ensureMapping.
        var last = batches.Count > 0 ? batches[^1] : null;
        foreach (var title in titles)
        {
            if (emittedTitleIds.Add(title.Id))
            {
                if (last == null)
                {
                    last = new UploadBatch();
                    batches.Add(last);
                }
                last.Titles.Add(title);
            }
        }

        if (metadata.Count > 0)
        {
            if (last == null)
            {
                last = new UploadBatch();
                batches.Add(last);
            }
            last.Metadata.AddRange(metadata);
        }

        if (batches.Count == 0)
            batches.Add(new UploadBatch());

        // Sources are a small shared registry — include every one in every batch (idempotent
        // upsert) so no series in any batch references a source the worker hasn't seen yet.
        if (sources.Count > 0)
        {
            foreach (var batch in batches)
                batch.Sources.AddRange(sources);
        }

        // Association rows go to the last batch (idempotent upsert; a delete needs no order).
        if (sourceSeries.Count > 0)
        {
            var la = batches.Count > 0 ? batches[^1] : null;
            if (la == null)
            {
                la = new UploadBatch();
                batches.Add(la);
            }
            la.SourceSeries.AddRange(sourceSeries);
        }

        return batches;
    }

    /// <summary>
    /// Set Version = -2 (Uploaded) on every row that had Version 0 or -1.
    /// The entities are already change-tracked? No — they were loaded AsNoTracking,
    /// so we re-query and update in one atomic SaveChanges.
    /// </summary>
    private async Task MarkUploadedAsync(CancellationToken token)
    {
        // Re-fetch tracked copies and mutate.
        int updated = 0;
        updated += await ExecuteVersionBumpAsync(
            _contributorDb.Titles.Where(t => t.Version == VersionAddOrUpdate || t.Version == VersionLogicalDelete),
            token).ConfigureAwait(false);
        updated += await ExecuteVersionBumpAsync(
            _contributorDb.SeriesTitles.Where(mt => mt.Version == VersionAddOrUpdate || mt.Version == VersionLogicalDelete),
            token).ConfigureAwait(false);
        updated += await ExecuteVersionBumpAsync(
            _contributorDb.Sources.Where(s => s.Version == VersionAddOrUpdate || s.Version == VersionLogicalDelete),
            token).ConfigureAwait(false);
        updated += await ExecuteVersionBumpAsync(
            _contributorDb.SeriesEntries.Where(s => s.Version == VersionAddOrUpdate || s.Version == VersionLogicalDelete),
            token).ConfigureAwait(false);
        updated += await ExecuteVersionBumpAsync(
            _contributorDb.Metadata.Where(m => m.Version == VersionAddOrUpdate || m.Version == VersionLogicalDelete),
            token).ConfigureAwait(false);
        updated += await ExecuteVersionBumpAsync(
            _contributorDb.SourceSeries.Where(ss => ss.Version == VersionAddOrUpdate || ss.Version == VersionLogicalDelete),
            token).ConfigureAwait(false);

        if (updated > 0)
        {
            await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
        }
    }

    private async Task<int> ExecuteVersionBumpAsync<T>(IQueryable<T> query, CancellationToken token)
        where T : class
    {
        List<T> rows = await query.ToListAsync(token).ConfigureAwait(false);
        foreach (T row in rows)
        {
            var versionProp = typeof(T).GetProperty(nameof(ContributionSourceEntity.Version));
            // Pending add/update (0) → -2 (Uploaded); pending delete (-1) → -3 (UploadedDelete).
            // The two tombstones are DISTINCT: a delete that is flipped to -2 would look LIVE
            // again (see ContributionVersions.IsLive), resurrecting a removed row on the page.
            var current = (int?)versionProp?.GetValue(row) ?? ContributionVersions.Uploaded;
            versionProp?.SetValue(row, ContributionVersions.ToUploaded(current));
        }

        // Attach may be needed if AsNoTracking state was used; rows from a tracked query are fine.
        // To be safe, attach each.
        foreach (T row in rows)
        {
            _contributorDb.Attach(row);
            _contributorDb.Entry(row).Property(nameof(ContributionSourceEntity.Version)).IsModified = true;
        }
        return rows.Count;
    }

    /// <summary>
    /// Like <see cref="MarkUploadedAsync"/> but only bumps rows whose entity list
    /// did NOT experience per-row errors (so failed rows stay pending for retry).
    /// </summary>
    private async Task MarkUploadedForListsAsync(HashSet<string> failedLists, CancellationToken token)
    {
        bool skip(string list) => failedLists.Contains(list);

        int updated = 0;
        if (!skip("t"))
        {
            updated += await ExecuteVersionBumpAsync(
                _contributorDb.Titles.Where(t => t.Version == VersionAddOrUpdate || t.Version == VersionLogicalDelete),
                token).ConfigureAwait(false);
        }
        if (!skip("m"))
        {
            updated += await ExecuteVersionBumpAsync(
                _contributorDb.SeriesTitles.Where(mt => mt.Version == VersionAddOrUpdate || mt.Version == VersionLogicalDelete),
                token).ConfigureAwait(false);
        }
        if (!skip("s"))
        {
            updated += await ExecuteVersionBumpAsync(
                _contributorDb.Sources.Where(s => s.Version == VersionAddOrUpdate || s.Version == VersionLogicalDelete),
                token).ConfigureAwait(false);
        }
        if (!skip("i"))
        {
            updated += await ExecuteVersionBumpAsync(
                _contributorDb.SeriesEntries.Where(s => s.Version == VersionAddOrUpdate || s.Version == VersionLogicalDelete),
                token).ConfigureAwait(false);
        }
        if (!skip("d"))
        {
            updated += await ExecuteVersionBumpAsync(
                _contributorDb.Metadata.Where(m => m.Version == VersionAddOrUpdate || m.Version == VersionLogicalDelete),
                token).ConfigureAwait(false);
        }
        if (!skip("x"))
        {
            updated += await ExecuteVersionBumpAsync(
                _contributorDb.SourceSeries.Where(ss => ss.Version == VersionAddOrUpdate || ss.Version == VersionLogicalDelete),
                token).ConfigureAwait(false);
        }

        if (updated > 0)
        {
            await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
        }
    }

    private static WorkerUploadResponse? DeserializeWorkerResponse(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkerUploadResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Force-reupload helper: maps the uploaded variants back to the two wire versions the
    /// worker accepts — Uploaded (-2) → AddOrUpdate (0), UploadedDelete (-3) → delete (-1).
    /// Executed on detached/AsNoTracking lists, so no EF state is disturbed.
    /// </summary>
    private static void NormalizeVersionsForWire(
        List<TitleEntity> titles,
        List<SeriesTitleEntity> mappingTitles,
        List<ContributionSourceEntity> sources,
        List<ContributionSeriesEntity> series,
        List<ContributionMetadataEntity> metadata,
        List<ContributionSourceSeriesEntity> sourceSeries)
    {
        static int Wire(int version) => version switch
        {
            VersionUploaded => VersionAddOrUpdate,
            VersionUploadedDelete => VersionLogicalDelete,
            _ => version,
        };

        foreach (var t in titles) t.Version = Wire(t.Version);
        foreach (var mt in mappingTitles) mt.Version = Wire(mt.Version);
        foreach (var s in sources) s.Version = Wire(s.Version);
        foreach (var s in series) s.Version = Wire(s.Version);
        foreach (var m in metadata) m.Version = Wire(m.Version);
        foreach (var ss in sourceSeries) ss.Version = Wire(ss.Version);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}

/// <summary>Deserialized response from the contribution worker's /upload endpoint.</summary>
internal sealed class WorkerUploadResponse
{
    [JsonPropertyName("processed")]
    public int Processed { get; set; }

    [JsonPropertyName("errors")]
    public List<WorkerUploadError> Errors { get; set; } = [];
}

internal sealed class WorkerUploadError
{
    [JsonPropertyName("list")]
    public string List { get; set; } = string.Empty;

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Renders like [list@index] message (e.g. "[s@2] Invalid replication version (v): -2")
    /// so logs/join() show WHAT happened instead of the type name.
    /// </summary>
    public override string ToString() => $"[{List}@{Index}] {Message}";
}

/// <summary>Result of a contribution upload attempt.</summary>
public sealed class ContributionUploadResult
{
    public bool Success { get; private init; }
    public string? Error { get; private init; }
    public int Titles { get; private init; }
    public int Mappings { get; private init; }
    public int Sources { get; private init; }
    public int Series { get; private init; }
    public int Metadata { get; private init; }
    public int Total { get; private init; }
    public int Processed { get; private init; }

    /// <summary>Human-readable worker per-row errors, e.g. "[s@2] message; [i@3] message".</summary>
    public string? WorkerErrors { get; private init; }

    public static ContributionUploadResult Succeeded(
        int titles, int mappings, int sources, int series, int metadata, int total, int processed,
        string? workerErrors = null) => new()
        {
            Success = true,
            Titles = titles,
            Mappings = mappings,
            Sources = sources,
            Series = series,
            Metadata = metadata,
            Total = total,
            Processed = processed,
            WorkerErrors = workerErrors,
        };

    public static ContributionUploadResult Failed(string error) => new()
    {
        Success = false,
        Error = error,
    };
}