using RensaioBackend.Data;
using RensaioBackend.Models;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Jobs;
using RensaioBackend.Services.Series;
using RensaioBackend.Services.Scrobbling;
using RensaioBackend.Services.Scrobbling.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SeriesEntity = RensaioBackend.Models.Database.SeriesEntity;

namespace RensaioBackend.Services.Metadata;

/// <summary>
/// Enum describing the resulting status of a provider link from the metadata engine.
/// </summary>
public enum SeriesLinkStatus
{
    Linked,
    Suggested,
    Failed
}

/// <summary>
/// A single provider link produced by the cross-provider metadata engine.
/// </summary>
public class MetaDataLinkEntry
{
    public ExternalSeriesProvider Provider { get; set; }
    public string ExternalSeriesId { get; set; } = string.Empty;
    public string? Title { get; set; }
    public double Confidence { get; set; }
    public SeriesLinkStatus Status { get; set; }
    public List<string> LinkedSitesIds { get; set; } = [];
    public List<string> AlternativeTitles { get; set; } = [];

    /// <summary>Cover image URL from the provider detail call (persisted to SeriesMappingEntity.SeriesCoverUrl).</summary>
    public string? CoverUrl { get; set; }

    /// <summary>Provider-native JSON payload from the detail call (persisted to SeriesMappingEntity.MetaData).</summary>
    public string? MetaData { get; set; }

    // ── Image (PDQ) match diagnostics ───────────────────────────────────────
    // Populated only when the image matcher ran for this entry (both covers hashed). Null when no
    // image comparison happened (no local cover, no provider cover, or hashing failed). Not persisted.

    /// <summary>Hamming distance (0..256) between the local cover and the selected provider cover.</summary>
    public int? ImageDistance { get; set; }

    /// <summary>PDQ similarity percentage (0..100) derived from <see cref="ImageDistance"/> (higher = closer).</summary>
    public int? ImageSimilarity { get; set; }

    /// <summary>True when the covers are the same image (distance within the PDQ threshold).</summary>
    public bool ImageMatched { get; set; }

    /// <summary>True when the image drove the selection (tie-break or corroboration).</summary>
    public bool ImageUsed { get; set; }
}

/// <summary>
/// Result of running the metadata link engine for one series.
/// </summary>
public class MetadataLinkResult
{
    public Guid SeriesId { get; set; }
    public string? SeriesTitle { get; set; }
    public List<MetaDataLinkEntry> Links { get; set; } = [];
    public List<MetaDataLinkEntry> Suggestions { get; set; } = [];
    public int ProvidersSearched { get; set; }
    public long ElapsedMs { get; set; }
}

/// <summary>
/// Cross-provider series link engine (Rensaio-DB persistence adapter).
///
/// Given a local series (title + existing sources), delegates the search/score/propagation
/// algorithm to <see cref="MetadataMatchCore"/> (shared with the Contribution Mappings flow) and
/// persists the linked results into global SeriesMappings (respecting role-based overwrite rules),
/// materializes Unmatched rows, syncs rensaio.json and propagates into the contribution database.
/// </summary>
public class MetadataLinkEngine
{
    private const string LinkAllJobId = "metadata-link-all";

    // Contribution-DB row version semantics (see ContributionUploadService): 0 = pending
    // add/update, -1 = pending delete, -2 = uploaded (LIVE), -3 = uploaded delete. A live
    // row is therefore any value except the two tombstones.
    private const int ContributionVersionLogicalDelete = -1;
    private const int ContributionVersionUploadedDelete = -3;

    private readonly AppDbContext _db;
    private readonly ContributionDbContext _contributorDb;
    private readonly ExternalSeriesProviderFactory _factory;
    private readonly MetadataMatchCore _matchCore;
    private readonly ExternalMappingSupport _support;
    private readonly TitleMatcher _titleMatcher;
    private readonly SeriesStateService _seriesStateService;
    private readonly JobHubReportService _hubReport;
    private readonly Contributions.ContributionPropagationService _contributionSync;
    private readonly ILogger<MetadataLinkEngine> _logger;

    public MetadataLinkEngine(
        AppDbContext db,
        ContributionDbContext contributorDb,
        ExternalSeriesProviderFactory factory,
        MetadataMatchCore matchCore,
        ExternalMappingSupport support,
        TitleMatcher titleMatcher,
        SeriesStateService seriesStateService,
        JobHubReportService hubReport,
        Contributions.ContributionPropagationService contributionSync,
        ILogger<MetadataLinkEngine> logger)
    {
        _db = db;
        _contributorDb = contributorDb;
        _factory = factory;
        _matchCore = matchCore;
        _support = support;
        _titleMatcher = titleMatcher;
        _seriesStateService = seriesStateService;
        _hubReport = hubReport;
        _contributionSync = contributionSync;
        _logger = logger;
    }

    /// <summary>
    /// Links ALL local series across all metadata providers, publishing live progress on
    /// ProgressHub (jobType = MetadataLink). Returns the summary when done.
    /// </summary>
    public async Task<MetaDataLinkAllResultDto> LinkAllAsync(CancellationToken token = default)
    {
        var summary = new MetaDataLinkAllResultDto();
        var seriesIds = await _db.Series.Select(s => s.Id).ToListAsync(token);
        var total = seriesIds.Count;

        // Only series with pending work are scanned: at least one enabled provider row is
        // Unmatched or Blocked-with-id (a DIFFERENT id may still be matched). Settled series
        // (active links, forever-ignored, manual-ignored, hard-blocked empty rows) are skipped.
        // Index mappings by series ONCE. The per-series attention check previously scanned the
        // whole mapping list for every series (O(series × mappings)) — on a large library that
        // stalled the whole scan before any progress.
        var mappingsBySeries = (await _db.SeriesMappings
                .Where(m => m.SeriesId != null)
                .ToListAsync(token))
            .GroupBy(m => m.SeriesId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var enabledProviders = await GetEnabledProviderSetAsync(token).ConfigureAwait(false);

        _logger.LogInformation("Metadata scan started: {SeriesCount} series, {ProviderCount} enabled provider(s)",
            total, enabledProviders.Count);

        if (_hubReport != null)
        {
            await _hubReport.ReportProgressAsync(new ProgressState
            {
                Id = LinkAllJobId,
                JobType = JobType.MetadataLink,
                ProgressStatus = ProgressStatus.Started,
                Percentage = 0,
                Message = $"Linking 0/{total} series"
            });
        }

        foreach (var id in seriesIds)
        {
            if (token.IsCancellationRequested) break;
            var seriesMappings = mappingsBySeries.TryGetValue(id, out var sm) ? sm : new();
            if (!SeriesNeedsAttention(seriesMappings, enabledProviders))
            {
                summary.SkippedSeries++;
                continue;
            }
            _logger.LogInformation("Metadata scan: series {Index}/{Total} ({SeriesId}) starting ({Linked} linked so far, {Skipped} skipped)",
                summary.ProcessedSeries + summary.SkippedSeries + 1, total, id, summary.LinkedProviderEntries, summary.SkippedSeries);
            var result = await LinkSeriesAsync(id, token, enabledProviders).ConfigureAwait(false);
            summary.ProcessedSeries++;
            summary.LinkedProviderEntries += result.Links.Count;
            summary.SuggestedEntries += result.Suggestions.Count;
            _logger.LogInformation("Metadata scan: series {SeriesId} done — {Links} linked, {Suggestions} suggested",
                id, result.Links.Count, result.Suggestions.Count);

            if (_hubReport != null)
            {
                var pct = total > 0 ? (summary.ProcessedSeries * 100 / total) : 100;
                await _hubReport.ReportProgressAsync(new ProgressState
                {
                    Id = LinkAllJobId,
                    JobType = JobType.MetadataLink,
                    ProgressStatus = ProgressStatus.InProgress,
                    Percentage = pct,
                    Message = $"Linking {summary.ProcessedSeries}/{total} series · {summary.LinkedProviderEntries} linked · {summary.SkippedSeries} skipped"
                });
            }
        }

        if (_hubReport != null)
        {
            await _hubReport.ReportProgressAsync(new ProgressState
            {
                Id = LinkAllJobId,
                JobType = JobType.MetadataLink,
                ProgressStatus = ProgressStatus.Completed,
                Percentage = 100,
                Message = $"Linked {summary.LinkedProviderEntries} entries across {summary.ProcessedSeries} series"
            });
        }

        return summary;
    }

    /// <summary>
    /// Links a single local series across all metadata providers and persists the result.
    /// </summary>
    public async Task<MetadataLinkResult> LinkSeriesAsync(Guid seriesId, CancellationToken token = default,
        HashSet<ExternalSeriesProvider>? enabledProviders = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var series = await _db.Series
            .Include(s => s.Sources)
            .FirstOrDefaultAsync(s => s.Id == seriesId, token);
        if (series == null)
            return new MetadataLinkResult { SeriesId = seriesId };

        var result = new MetadataLinkResult
        {
            SeriesId = seriesId,
            SeriesTitle = series.Title
        };

        // Per-series progress events (id = "metadata-link-{seriesId}") so the External Mappings
        // page can show a live spinner + completion for the row being scanned. These are distinct
        // from the global scan's aggregate progress (id = LinkAllJobId).
        if (_hubReport != null)
        {
            await _hubReport.ReportProgressAsync(new ProgressState
            {
                Id = $"metadata-link-{seriesId}",
                JobType = JobType.MetadataLink,
                ProgressStatus = ProgressStatus.Started,
                Percentage = 0,
                Message = $"Scanning '{series.Title}'…"
            }).ConfigureAwait(false);
        }

        // 1. Load the existing status-aware mappings and derive block/skip rules.
        //  - Blocked rows: id-level RULE — that provider must never link to that exact id, but may
        //    automatch a DIFFERENT id later. Record the blocked ids; do not seed them as nodes/edges.
        //  - ForeverIgnored / ManualIgnore: provider-level skip — don't seed, don't search, don't
        //    re-link (both are settled refusals with no expiration).
        //  - AutoMatched / UserConfirmed / Unmatched-with-id: seed normally as the link graph roots.
        var existingMappings = await _db.SeriesMappings
            .Where(m => m.SeriesId == seriesId)
            .ToListAsync(token);

        var blockedIds = new Dictionary<ExternalSeriesProvider, HashSet<string>>();
        var skippedProviders = new HashSet<ExternalSeriesProvider>();
        var seeds = new List<MetadataLinkSeed>();

        foreach (var mapping in existingMappings)
        {
            if (mapping.MappingStatus == SeriesMappingStatus.Blocked)
            {
                if (!string.IsNullOrWhiteSpace(mapping.ExternalSeriesId))
                {
                    if (!blockedIds.TryGetValue(mapping.Provider, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        blockedIds[mapping.Provider] = set;
                    }
                    set.Add(mapping.ExternalSeriesId);
                }
                continue;
            }

            if (mapping.MappingStatus == SeriesMappingStatus.ForeverIgnored ||
                mapping.MappingStatus == SeriesMappingStatus.ManualIgnore)
            {
                // Both are settled refusals with no expiration — never re-searched.
                skippedProviders.Add(mapping.Provider);
                continue;
            }

            if (string.IsNullOrWhiteSpace(mapping.ExternalSeriesId)) continue;
            seeds.Add(new MetadataLinkSeed
            {
                Provider = mapping.Provider,
                ExternalSeriesId = mapping.ExternalSeriesId,
                ExternalSeriesTitle = mapping.ExternalSeriesTitle,
                LinkedSitesIds = mapping.LinkedSitesIds,
                Confidence = 1.0
            });
        }

        // 2. Determine the metadata providers to query (order matters: hub first) and authenticate.
        //    ONLY pending providers are actually searched: a provider row that is already settled
        //    (active link, ForeverIgnored, ManualIgnore, hard-blocked) is never re-evaluated — a
        //    Scan / Scan-All re-scans just the non-matched rows. This is the per-provider equivalent
        //    of SeriesNeedsAttention: matched providers stay untouched, unmatched providers get one
        //    re-evaluation pass.
        var pendingProviders = GetPendingProviders(existingMappings, enabledProviders);
        var providers = _support.GetMetadataProvidersInOrder()
            .Where(p => pendingProviders.Contains(p.ProviderType))
            .ToList();
        if (enabledProviders != null)
            providers = providers.Where(p => enabledProviders.Contains(p.ProviderType)).ToList();
        await _support.EnsureProvidersAuthenticatedAsync(providers, token).ConfigureAwait(false);

        // 2.5 Search gate — BEFORE searching any metadata/scrobbler provider for this series, ask
        //     the provider whether it can search this kind of series at all. Genres come from
        //     SeriesEntity.Genre, category comes from SeriesEntity.Type. When the provider cannot
        //     search the series, the (series, provider) relation is settled as ForeverIgnored
        //     ("ignoredAlways") so that provider is never searched for this series again.
        var now = DateTime.UtcNow;
        foreach (var provider in providers)
        {
            if (!provider.CanSearchSeries(series.Genre, series.Type))
            {
                skippedProviders.Add(provider.ProviderType);
                var ignoredRow = await EnsureRelationIgnoredAsync(seriesId, provider.ProviderType, now, token).ConfigureAwait(false);
                // Reflect the new decision in the in-memory mapping list so the Unmatched
                // materialization below never creates a competing Unmatched row for the same
                // (series, provider) — "ignoredAlways" is the settled provider-level decision.
                if (ignoredRow != null && existingMappings.All(m => m.Id != ignoredRow.Id))
                    existingMappings.Add(ignoredRow);
            }
        }

        // 3. Contribution database FIRST match. The contribution database is the shared, cloud-synced
        //    title → provider graph. When the local series' titles resolve to an existing contribution
        //    mapping, we USE its links instead of searching every service — a HIT means the community
        //    already matched this title, so re-searching every provider is wasted work. On a hit there
        //    is NO metadata/scrobbler search; only series metadata (detail) is fetched afterwards, and
        //    only for providers that are available (connected / enabled).
        var fetchedIds = new HashSet<(ExternalSeriesProvider Site, string Id)>(new MetadataMatchCore.SiteEdgeComparer());
        var contributionLinks = await TryGetContributionLinksAsync(series, existingMappings,
            blockedIds, skippedProviders, token).ConfigureAwait(false);
        var gotContributionHit = contributionLinks.Count > 0;

        MetadataMatchResult? match = null;
        var nodes = new Dictionary<ExternalSeriesProvider, MetaDataLinkEntry>();

        // Local cover + owning Mihon provider for image (PDQ) corroboration of borderline title
        // matches. Absent/empty → the matcher simply behaves as title-only.
        var localCoverUrl = string.IsNullOrWhiteSpace(series.ThumbnailUrl) ? null : series.ThumbnailUrl;
        var mihonProviderId = series.Sources?
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.MihonProviderId))?.MihonProviderId;

        if (gotContributionHit)
        {
            result.ProvidersSearched = 0; // no provider search on a hit
            var hitProviders = new HashSet<ExternalSeriesProvider>();
            foreach (var node in contributionLinks)
            {
                if (enabledProviders != null && !enabledProviders.Contains(node.Provider))
                    continue; // provider not connected/enabled → no metadata fetch, no link
                if (!pendingProviders.Contains(node.Provider))
                    continue; // settled provider → never link it during a pending-only scan
                hitProviders.Add(node.Provider);
                await EnrichFromProviderMetadataAsync(node, fetchedIds, token).ConfigureAwait(false);
                await UpsertMappingAsync(seriesId, node, fetchedIds, token);
                result.Links.Add(node);
            }

            // The contribution DB covered SOME providers, but the MISSING ones (those without a
            // contribution match — the "missing unmatches") still need a LOCAL search. Only run the
            // matcher for the RESIDUAL pending providers: enabled + pending − already-hit.
            var residualProviders = providers
                .Where(p => pendingProviders.Contains(p.ProviderType) && !hitProviders.Contains(p.ProviderType))
                .ToList();
            if (residualProviders.Count > 0)
            {
                var localCandidates = _titleMatcher.BuildTitleCandidates(series);
                var residualMatch = await _matchCore.MatchAsync(localCandidates, residualProviders, seeds, blockedIds,
                    skippedProviders, enabledProviders, token, localCoverUrl, mihonProviderId).ConfigureAwait(false);
                result.ProvidersSearched = residualMatch.ProvidersSearched;
                // Merge residual suggestions.
                foreach (var s in residualMatch.Suggestions)
                    if (!result.Suggestions.Contains(s)) result.Suggestions.Add(s);
                foreach (var node in residualMatch.Links)
                {
                    if (enabledProviders != null && !enabledProviders.Contains(node.Provider))
                        continue; // not enabled
                    if (!pendingProviders.Contains(node.Provider))
                        continue;
                    if (hitProviders.Contains(node.Provider))
                        continue;
                    await UpsertMappingAsync(seriesId, node, residualMatch.FetchedIds, token);
                    result.Links.Add(node);
                }
            }

            nodes = result.Links.ToDictionary(l => l.Provider, l => l);
            _logger.LogDebug("Metadata link: contribution DB hit for series {SeriesId} → {Count} linked, residual search {Residual} providers",
                seriesId, result.Links.Count, residualProviders.Count);
        }
        else
        {
            // 3. Run the shared matcher (search + second pass + cross-site propagation).
            var localCandidates = _titleMatcher.BuildTitleCandidates(series);
            match = await _matchCore.MatchAsync(localCandidates, providers, seeds, blockedIds,
                skippedProviders, enabledProviders, token, localCoverUrl, mihonProviderId).ConfigureAwait(false);
            result.ProvidersSearched = match.ProvidersSearched;
            result.Suggestions = match.Suggestions;

            foreach (var node in match.Links)
            {
                if (enabledProviders != null && !enabledProviders.Contains(node.Provider))
                    continue; // not enabled → don't enrich/fetch metadata for this provider
                if (!pendingProviders.Contains(node.Provider))
                    continue; // propagated node on a SETTLED provider → never upsert (avoids forcing conflicts)
                await UpsertMappingAsync(seriesId, node, match.FetchedIds, token);
                result.Links.Add(node);
            }

            // 5.5 Materialize Unmatched rows: for every enabled metadata provider that ended up with no
            //     active link (and no Blocked/ManualIgnore/ForeverIgnored decision), persist an empty
            //     SeriesMappings row (status = Unmatched, external id = ""). This makes a manual mapping
            //     possible in the External Mappings page even when the automatch found nothing.
            nodes = match.Links.ToDictionary(l => l.Provider, l => l);
        }

        // 5.5 Materialize Unmatched rows (hit path too): for every enabled provider that the
        //     contribution link did not cover and that ended up with no decision, persist an empty
        //     SeriesMappings row (status = Unmatched, external id = "") so it stays manually linkable.
        await MaterializeUnmatchedAsync(seriesId, existingMappings, providers, nodes, skippedProviders, token).ConfigureAwait(false);

        // 6. Sync rensaio.json via the existing service (best effort).
        await SyncRensaioJsonAsync(seriesId, token);

        // 7. Propagate the mapping state into the contribution database (best effort).
        //    No-op unless the user enabled contributions and never throws into the caller.
        try
        {
            await _contributionSync.SyncSeriesAsync(seriesId, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to propagate contribution state for series {SeriesId}", seriesId);
        }

        sw.Stop();
        result.ElapsedMs = sw.ElapsedMilliseconds;

        if (_hubReport != null)
        {
            await _hubReport.ReportProgressAsync(new ProgressState
            {
                Id = $"metadata-link-{seriesId}",
                JobType = JobType.MetadataLink,
                ProgressStatus = ProgressStatus.Completed,
                Percentage = 100,
                Message = $"{series.Title}: {result.Links.Count} linked, {result.Suggestions.Count} suggested, {result.ProvidersSearched} providers searched"
            }).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Contribution-DB first match. Resolves the local series' titles to an existing contribution
    /// mapping (the shared, cloud-synced title graph) and returns its active provider links as
    /// high-confidence nodes. A hit means NO metadata/scrobbler provider search is needed — the
    /// community already established these matches; only metadata detail fetches may follow for
    /// providers that are connected.
    /// Local provider-level refusals (hard Blocked / sealed block / ForeverIgnored / ManualIgnore)
    /// and CanSearchSeries skips always win over the contribution links.
    /// </summary>
    private async Task<List<MetaDataLinkEntry>> TryGetContributionLinksAsync(
        SeriesEntity series,
        List<SeriesMappingEntity> existingMappings,
        Dictionary<ExternalSeriesProvider, HashSet<string>> blockedIds,
        HashSet<ExternalSeriesProvider> skippedProviders,
        CancellationToken token)
    {
        try
        {
            // 1. Deterministic normalized title ids of the local series (main + storage + sources).
            var localCandidates = _titleMatcher.BuildTitleCandidates(series)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (localCandidates.Count == 0) return [];

            var titleIds = localCandidates.Select(TitleEntity.DeriveId).Distinct().ToHashSet();

            // 2. Mappings sharing any of these titles; prefer the one that links the MAIN title,
            //    then the one with the most titles in common.
            // Only LIVE links count: a tombstoned association must not resolve a mapping.
            var seriesTitles = await _contributorDb.SeriesTitles
                .Where(mt => titleIds.Contains(mt.TitleId)
                    && mt.Version != ContributionVersionLogicalDelete
                    && mt.Version != ContributionVersionUploadedDelete)
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);
            if (seriesTitles.Count == 0) return [];

            var mainTitleId = TitleEntity.DeriveId(series.Title);
            var mappingId = seriesTitles
                .GroupBy(mt => mt.SeriesId)
                .OrderByDescending(g => g.Any(mt => mt.TitleId == mainTitleId) ? 1 : 0)
                .ThenByDescending(g => g.Count())
                .First().Key;

            // 3. Active metadata rows of the winning mapping = the community's links.
            //    Uploaded rows are -2 (NOT >= 0), so "not a tombstone" is the correct live test;
            //    a `Version >= 0` filter would silently return nothing after the first upload.
            var metadata = await _contributorDb.Metadata
                .Where(m => m.SeriesId == mappingId
                    && m.Version != ContributionVersionLogicalDelete
                    && m.Version != ContributionVersionUploadedDelete)
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);

            // Provider-level local rules: a locally hard-off provider (Blocked empty id / sealed
            // block / ForeverIgnored / ManualIgnore) or a CanSearchSeries skip
            // cannot receive a contribution link.
            bool ProviderLocallyOff(ExternalSeriesProvider p)
            {
                if (skippedProviders.Contains(p)) return true;
                foreach (var m in existingMappings)
                {
                    if (m.Provider != p) continue;
                    if (m.MappingStatus == SeriesMappingStatus.Blocked &&
                        string.IsNullOrWhiteSpace(m.ExternalSeriesId)) return true;
                    if (m.IsAutoSealedBlock) return true;
                    if (m.MappingStatus == SeriesMappingStatus.ForeverIgnored) return true;
                    if (m.MappingStatus == SeriesMappingStatus.ManualIgnore) return true;
                }
                return false;
            }

            var links = new List<MetaDataLinkEntry>();
            foreach (var row in metadata)
            {
                // Only active links count as a hit; refusals are not applied from the cloud.
                if (row.MappingStatus != SeriesMappingStatus.AutoMatched
                    && row.MappingStatus != SeriesMappingStatus.UserConfirmed) continue;
                if (string.IsNullOrWhiteSpace(row.ProviderKey)) continue;
                if (!Enum.IsDefined(typeof(ExternalSeriesProvider), row.ProviderId)) continue;
                var provider = (ExternalSeriesProvider)row.ProviderId;
                if (ProviderLocallyOff(provider)) continue;
                if (blockedIds.TryGetValue(provider, out var idset) && idset.Contains(row.ProviderKey)) continue;

                links.Add(new MetaDataLinkEntry
                {
                    Provider = provider,
                    ExternalSeriesId = row.ProviderKey,
                    Title = null,
                    Confidence = 1.0,
                    Status = SeriesLinkStatus.Linked
                });
            }
            return links;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Contribution DB first-match failed for series {SeriesId}", series.Id);
            return [];
        }
    }

    /// <summary>
    /// Fetches series metadata (detail) for a contribution-linked provider — only reached when the
    /// provider is available/connected (enabled for the current context). Populates cover, native
    /// metadata, alt titles and linked site ids so the persisted mapping is enriched.
    /// </summary>
    private async Task EnrichFromProviderMetadataAsync(MetaDataLinkEntry node,
        HashSet<(ExternalSeriesProvider Site, string Id)> fetchedIds, CancellationToken token)
    {
        try
        {
            if (!fetchedIds.Add((node.Provider, node.ExternalSeriesId))) return;
            var provider = _factory.GetProvider(node.Provider);
            if (provider == null) return;
            var metadata = await provider.FetchSeriesMetadataAsync(node.ExternalSeriesId, token).ConfigureAwait(false);
            if (metadata == null) return;

            node.CoverUrl ??= metadata.CoverUrl;
            if (!string.IsNullOrWhiteSpace(metadata.MetaData))
                node.MetaData = metadata.MetaData;
            node.Title ??= metadata.Title;
            foreach (var t in metadata.AlternativeTitles)
                if (!node.AlternativeTitles.Contains(t, StringComparer.OrdinalIgnoreCase))
                    node.AlternativeTitles.Add(t);
            foreach (var l in metadata.LinkedSitesIds)
                if (!node.LinkedSitesIds.Contains(l, StringComparer.OrdinalIgnoreCase))
                    node.LinkedSitesIds.Add(l);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch contribution-hit series metadata {Provider} {Id}",
                node.Provider, node.ExternalSeriesId);
        }
    }

    /// <summary>
    /// Settles a (series, provider) relation as ForeverIgnored because the provider itself declared
    /// it cannot search this series (CanSearchSeries == false). Never downgrades a stronger local
    /// decision (UserConfirmed / Blocked / ForeverIgnored). Returns the persisted decision row so
    /// the caller can keep its in-memory mapping list in sync.
    /// </summary>
    private async Task<SeriesMappingEntity?> EnsureRelationIgnoredAsync(Guid seriesId, ExternalSeriesProvider provider,
        DateTime now, CancellationToken token)
    {
        var existing = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == provider, token)
            .ConfigureAwait(false);
        if (existing != null)
        {
            if (existing.MappingStatus == SeriesMappingStatus.UserConfirmed
                || existing.MappingStatus == SeriesMappingStatus.Blocked
                || existing.MappingStatus == SeriesMappingStatus.ForeverIgnored)
                return existing; // user/stronger decision stands
            existing.ExternalSeriesId = string.Empty;
            existing.MappingStatus = SeriesMappingStatus.ForeverIgnored;
            existing.LinkedDate = now;
            existing.UpdateDate = now;
        }
        else
        {
            existing = new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                Provider = provider,
                ExternalSeriesId = string.Empty,
                ExternalSeriesTitle = null,
                UserUid = null,
                UserRole = UserLevel.User,
                MappingStatus = SeriesMappingStatus.ForeverIgnored,
                LinkedDate = now,
                UpdateDate = now
            };
            _db.SeriesMappings.Add(existing);
        }
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        await SyncRensaioJsonAsync(seriesId, token).ConfigureAwait(false);
        try
        {
            await _contributionSync.SyncSeriesAsync(seriesId, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to propagate ignored relation for series {SeriesId} provider {Provider}",
                seriesId, provider);
        }
        return existing;
    }

    private async Task UpsertMappingAsync(Guid seriesId, MetaDataLinkEntry node,
        HashSet<(ExternalSeriesProvider Site, string Id)> fetchedIds, CancellationToken token)
    {
        try
        {
            // CONFLICT GUARD (post-link safety net): never persist an AutoMatched link for this
            // series to a (provider, externalId) that a DIFFERENT series already owns with an
            // active claim — that is exactly the self-reinforcing wrong linkage the mapping
            // conflict repair pass fixes. Refuse the link and persist an id-scoped Blocked row
            // instead so the wrong id stays excluded from future matching. User decisions of
            // THIS series are never downgraded.
            var owner = await MappingOwnershipGuard.FindOwnerAsync(_db, node.Provider,
                node.ExternalSeriesId, seriesId, token).ConfigureAwait(false);
            if (owner != null && MappingOwnershipGuard.IsStrongerThanNewAutoMatch(owner))
            {
                // Guard against the bogus-key bug: an empty/"0" external id is not a real id to
                // refuse — creating/converting to a Blocked row with it would silently hard-disable
                // the provider. Skip the block when the node carries no meaningful id.
                bool hasRealId = !string.IsNullOrWhiteSpace(node.ExternalSeriesId) && node.ExternalSeriesId != "0";
                var blocked = await _db.SeriesMappings
                    .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == node.Provider, token)
                    .ConfigureAwait(false);
                if (blocked == null)
                {
                    // No row for this series+provider yet → add an id-scoped Blocked row (only when
                    // there is a real id to refuse).
                    if (hasRealId)
                    {
                        _db.SeriesMappings.Add(new SeriesMappingEntity
                        {
                            Id = Guid.NewGuid(),
                            SeriesId = seriesId,
                            Provider = node.Provider,
                            ExternalSeriesId = node.ExternalSeriesId,
                            MappingStatus = SeriesMappingStatus.Blocked,
                            LinkedDate = SeriesMappingEntity.AutoSealedSentinel,
                            UserRole = UserLevel.User,
                            UpdateDate = DateTime.UtcNow
                        });
                    }
                }
                else if ((blocked.MappingStatus == SeriesMappingStatus.AutoMatched
                    || blocked.MappingStatus == SeriesMappingStatus.Unmatched
                    || blocked.MappingStatus == SeriesMappingStatus.ManualIgnore)
                    && hasRealId)
                {
                    // Existing row is not a user decision → convert it to the id-scoped block and
                    // clear the seeded columns so it stops propagating the wrong ids (only when
                    // there is a real id to refuse — a "0"/empty id must never hard-disable the
                    // provider). Sealed so the scanner treats it as settled (the wrong id is
                    // excluded and the provider stays available for a different id ONLY after the
                    // user unblocks).
                    blocked.ExternalSeriesId = node.ExternalSeriesId;
                    blocked.MappingStatus = SeriesMappingStatus.Blocked;
                    blocked.LinkedDate = SeriesMappingEntity.AutoSealedSentinel;
                    blocked.UpdateDate = DateTime.UtcNow;
                    blocked.LinkedSitesIds = [];
                    blocked.AlternativeTitles = [];
                    blocked.SeriesCoverUrl = null;
                    blocked.MetaData = null;
                    blocked.ExternalSeriesTitle = null;
                }
                // else: user decision (UserConfirmed / ForeverIgnored / Blocked) — untouched.
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
                return; // refuse the link; do not enrich, do not overwrite
            }

            var existing = await _db.SeriesMappings
                .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == node.Provider, token);

            if (existing != null)
            {
                // Never overwrite a user decision: Blocked / ForeverIgnored / ManualIgnore are
                // kept as-is (their ids are already excluded from search + propagation).
                if (existing.MappingStatus == SeriesMappingStatus.ForeverIgnored ||
                    existing.MappingStatus == SeriesMappingStatus.ManualIgnore)
                    return;

                // Blocked is an id-level rule only: reaching this point means the node carries a
                // DIFFERENT id than the blocked one (search + propagation filter blockedIds), so
                // linking the new id is allowed and always takes priority over the stale block.
                // Only enrich metadata columns on blocked rows / when no external id, or role rules
                // permit overwrite (System-level).
                if (existing.MappingStatus == SeriesMappingStatus.Blocked ||
                    string.IsNullOrWhiteSpace(existing.ExternalSeriesId) ||
                    existing.UserRole <= UserLevel.User)
                {
                    existing.ExternalSeriesId = node.ExternalSeriesId;
                    existing.ExternalSeriesTitle = node.Title;
                    // Persist cover + native metadata captured during the shared matcher's detail
                    // expansion / propagation. Only fills cover when empty; MetaData always refreshed.
                    if (string.IsNullOrWhiteSpace(existing.SeriesCoverUrl) && !string.IsNullOrWhiteSpace(node.CoverUrl))
                        existing.SeriesCoverUrl = node.CoverUrl;
                    if (!string.IsNullOrWhiteSpace(node.MetaData))
                        existing.MetaData = node.MetaData;
                    existing.LinkedSitesIds = MergeLinked(existing.LinkedSitesIds, node.LinkedSitesIds);
                    existing.AlternativeTitles = MergeTitles(existing.AlternativeTitles, node.AlternativeTitles);
                    // Preserve a settled user decision — the link engine never downgrades a
                    // UserConfirmed mapping back to AutoMatched.
                    if (existing.MappingStatus != SeriesMappingStatus.UserConfirmed)
                        existing.MappingStatus = SeriesMappingStatus.AutoMatched;
                    existing.LinkedDate = DateTime.UtcNow;
                    existing.UpdateDate = DateTime.UtcNow;
                }
            }
            else
            {
                _db.SeriesMappings.Add(new SeriesMappingEntity
                {
                    Id = Guid.NewGuid(),
                    SeriesId = seriesId,
                    Provider = node.Provider,
                    ExternalSeriesId = node.ExternalSeriesId,
                    ExternalSeriesTitle = node.Title,
                    SeriesCoverUrl = node.CoverUrl,
                    MetaData = node.MetaData,
                    LinkedSitesIds = node.LinkedSitesIds,
                    AlternativeTitles = node.AlternativeTitles,
                    UserUid = null,
                    UserRole = UserLevel.User,
                    MappingStatus = SeriesMappingStatus.AutoMatched,
                    LinkedDate = DateTime.UtcNow,
                    UpdateDate = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync(token);

            // Enrich the mapping with provider metadata already captured on the node during
            // expansion / propagation (cover + native JSON + linked sites + titles).
            // Only falls back to a fresh provider call when the node has no metadata at all.
            try
            {
                // `existing` is null on the insert path — reload the persisted row so we can enrich it.
                var saved = existing ?? await _db.SeriesMappings
                    .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == node.Provider, token)
                    .ConfigureAwait(false);
                if (saved == null) return;

                if (string.IsNullOrWhiteSpace(saved.SeriesCoverUrl) && !string.IsNullOrWhiteSpace(node.CoverUrl))
                    saved.SeriesCoverUrl = node.CoverUrl;
                if (!string.IsNullOrWhiteSpace(node.MetaData))
                    saved.MetaData = node.MetaData;
                if (node.LinkedSitesIds.Count > 0)
                    saved.LinkedSitesIds = MergeLinked(saved.LinkedSitesIds, node.LinkedSitesIds);
                if (node.AlternativeTitles.Count > 0)
                    saved.AlternativeTitles = MergeTitles(saved.AlternativeTitles, node.AlternativeTitles);

                // Fallback: if the node somehow lacks the metadata (e.g. search-only path), fetch
                // once and persist — dedup via fetchedIds applies to the provider call only.
                if (string.IsNullOrWhiteSpace(node.CoverUrl) && string.IsNullOrWhiteSpace(node.MetaData)
                    && fetchedIds.Add((node.Provider, saved.ExternalSeriesId)))
                {
                    var provider = _factory.GetProvider(node.Provider);
                    var metadata = provider != null
                        ? await provider.FetchSeriesMetadataAsync(saved.ExternalSeriesId, token).ConfigureAwait(false)
                        : null;
                    if (metadata != null)
                    {
                        if (string.IsNullOrWhiteSpace(saved.SeriesCoverUrl) && !string.IsNullOrWhiteSpace(metadata.CoverUrl))
                            saved.SeriesCoverUrl = metadata.CoverUrl;
                        if (!string.IsNullOrWhiteSpace(metadata.MetaData))
                            saved.MetaData = metadata.MetaData;
                    }
                }

                await _db.SaveChangesAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enrich SeriesMapping metadata for series {SeriesId} provider {Provider}", seriesId, node.Provider);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to upsert SeriesMapping for series {SeriesId} provider {Provider}", seriesId, node.Provider);
        }
    }

    /// <summary>
    /// Creates an empty (Unmatched) <see cref="SeriesMappingEntity"/> row for each enabled metadata
    /// provider that ended this pass without a linked node AND without an existing row that already
    /// records a decision (Blocked / ManualIgnore / ForeverIgnored / active id row / Unmatched).
    /// This makes every provider visible and manually linkable in the External Mappings page even when
    /// automatching found nothing, while never overriding user decisions.
    /// </summary>
    private async Task MaterializeUnmatchedAsync(Guid seriesId, List<SeriesMappingEntity> existingMappings,
        List<IExternalSeriesProvider> providers, Dictionary<ExternalSeriesProvider, MetaDataLinkEntry> nodes,
        HashSet<ExternalSeriesProvider>? skippedProviders, CancellationToken token)
    {
        var now = DateTime.UtcNow;
        var added = 0;

        foreach (var provider in providers)
        {
            var providerType = provider.ProviderType;

            // Provider declared it cannot search this series → settled as ignoredAlways; the row
            // exists (created by the search gate) and must never get a competing Unmatched row.
            if (skippedProviders != null && skippedProviders.Contains(providerType)) continue;

            // Had an automatch / propagation already linked it? Nothing to do.
            if (nodes.ContainsKey(providerType)) continue;

            // Existing row for this (series, provider) already records a state (decision ids, blocked,
            // temporary/forever ignored, or even an Unmatched decision) — leave it untouched.
            var existing = existingMappings.FirstOrDefault(m => m.SeriesId == seriesId && m.Provider == providerType);
            if (existing != null) continue;

            // No row at all → materialize an Unmatched row so a manual mapping is possible.
            _db.SeriesMappings.Add(new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                Provider = providerType,
                ExternalSeriesId = string.Empty,
                ExternalSeriesTitle = null,
                SeriesCoverUrl = null,
                MetaData = null,
                LinkedSitesIds = [],
                AlternativeTitles = [],
                UserUid = null,
                UserRole = UserLevel.User,
                MappingStatus = SeriesMappingStatus.Unmatched,
                LinkedDate = null,
                UpdateDate = now
            });
            added++;
        }

        if (added > 0)
        {
            try
            {
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to materialize Unmatched mappings for series {SeriesId}", seriesId);
            }
        }
    }

    private static List<string> MergeLinked(List<string> existing, List<string> newLinked)
    {
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var l in newLinked) set.Add(l);
        return set.ToList();
    }

    private static List<string> MergeTitles(List<string> existing, List<string> newTitles)
    {
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var t in newTitles) set.Add(t);
        return set.ToList();
    }

    /// <summary>
    /// Providers that unattended scanning may query: public providers (no auth) plus OAuth-gated
    /// providers that have at least one enabled config carrying an access token. The background
    /// worker has no request user, so "enabled" = any authenticated config; the Global Scan button
    /// reuses the same rule.
    /// </summary>
    public async Task<HashSet<ExternalSeriesProvider>> GetEnabledProviderSetAsync(CancellationToken token = default)
        => await _support.GetEnabledProviderSetAsync(token).ConfigureAwait(false);

    /// <summary>
    /// True when the series has at least one ENABLED provider that needs (re-)evaluation:
    /// no mapping row, an active link row with an empty/stale external id, a
    /// <see cref="SeriesMappingStatus.Blocked"/> row carrying an id (a DIFFERENT id may still be
    /// matched — the block is id-scoped, not provider-scoped).
    /// Settled states return false: active link with id, <see cref="SeriesMappingStatus.Unmatched"/>
    /// (a previous pass already searched; no match found — re-searching is wasted work), ForeverIgnored,
    /// hard-blocked (empty-id) row, and <see cref="SeriesMappingStatus.ManualIgnore"/>.
    /// A series with an active link on one provider and an Unmatched row on another is SKIPPED.
    /// </summary>
    public bool SeriesNeedsAttention(List<SeriesMappingEntity> seriesMappings,
        HashSet<ExternalSeriesProvider> enabledProviders)
    {
        if (enabledProviders.Count == 0) return false;

        if (seriesMappings.Count == 0) return true; // no rows → every enabled provider is unmatched

        var covered = 0;
        foreach (var m in seriesMappings)
        {
            if (!enabledProviders.Contains(m.Provider)) continue; // provider disabled → not our concern
            covered++;
            switch (m.MappingStatus)
            {
                case SeriesMappingStatus.Unmatched:
                    // An Unmatched row means "searched before, no match" — but for the GLOBAL /
                    // background scan it is PENDING WORK and must be re-searched (Scan / Scan-All
                    // are for exactly this: "re-scan non-matched providers"). The per-provider
                    // GetPendingProviders filter inside LinkSeriesAsync decides WHICH providers to
                    // search, so reaching this gate just means "this series has some pending work".
                    // (The previous behavior — treating Unmatched as settled at the series level —
                    // made Scan-All quit after a few series because it skipped them entirely.)
                    return true;
                case SeriesMappingStatus.AutoMatched:
                case SeriesMappingStatus.UserConfirmed:
                    // Settled only when it actually carries an external id; stale empty-id rows
                    // (e.g. old blocked/ignored cleanup) must be re-scanned and refilled.
                    if (string.IsNullOrWhiteSpace(m.ExternalSeriesId)) return true;
                    break;
                case SeriesMappingStatus.Blocked:
                    // Empty block = hard block (provider completely off); an id block only rules
                    // out that exact id → the provider may still accept a DIFFERENT id.
                    // EXCEPTION: a REPAIR-SEALED id-block (LinkedDate == AutoSealedSentinel) is
                    // settled — the auto-repair already decided the whole provider is wrong for this
                    // series, so re-scanning it every pass is pure waste (it can never match a
                    // different id without the user explicitly unblocking first).
                    if (m.IsAutoSealedBlock) break;
                    if (!string.IsNullOrWhiteSpace(m.ExternalSeriesId)) return true;
                    break;
                case SeriesMappingStatus.ForeverIgnored:
                    break; // settled
                case SeriesMappingStatus.ManualIgnore:
                    break; // settled refusal, no expiration
            }
        }

        // An enabled provider with NO row for this series is genuinely new work (e.g. the user
        // just enabled/connected a provider) — scan it once. After the pass materializes an
        // Unmatched row for it, the series becomes settled again (see the Unmatched case above).
        return covered < enabledProviders.Count;
    }

    /// <summary>
    /// Ordered metadata providers the engine queries (hub first). Delegates to the shared support
    /// helper so the External Mappings page and the Contribution Mappings page render the same set.
    /// </summary>
    public List<IExternalSeriesProvider> GetMetadataProvidersInOrder()
        => _support.GetMetadataProvidersInOrder();

    /// <summary>
    /// Returns the set of providers that have pending (re-)evaluation work for this series:
    /// - NO mapping row at all (never scanned → search),
    /// - an <see cref="SeriesMappingStatus.Unmatched"/> row (previously searched, no match → re-search).
    /// Settled providers (active link with id, stale empty-id active row, ForeverIgnored, ALL
    /// Blocked rows — including id-scoped: the block is a refusal and a different id is only a
    /// manual decision — and ManualIgnore) are EXCLUDED.
    /// Scan/Scan-All must re-scan ONLY these pending cases — never matched or refused providers.
    /// </summary>
    private static HashSet<ExternalSeriesProvider> GetPendingProviders(
        List<SeriesMappingEntity> mappings,
        HashSet<ExternalSeriesProvider>? enabledProviders = null)
    {
        var pending = new HashSet<ExternalSeriesProvider>();
        var settled = new HashSet<ExternalSeriesProvider>();
        foreach (var m in mappings)
        {
            switch (m.MappingStatus)
            {
                case SeriesMappingStatus.Unmatched:
                    pending.Add(m.Provider);
                    break;
                case SeriesMappingStatus.AutoMatched:
                case SeriesMappingStatus.UserConfirmed:
                    if (string.IsNullOrWhiteSpace(m.ExternalSeriesId))
                        pending.Add(m.Provider); // stale empty-id active row
                    else
                        settled.Add(m.Provider);
                    break;
                case SeriesMappingStatus.Blocked:
                    // ALL blocked rows are settled refusals (ProviderLocallyOff in the engine) —
                    // a Blocked row, id-scoped or hard, means the provider is not to be linked to
                    // this series automatically. A different id can only be chosen manually.
                    settled.Add(m.Provider);
                    break;
                case SeriesMappingStatus.ForeverIgnored:
                    settled.Add(m.Provider);
                    break;
                case SeriesMappingStatus.ManualIgnore:
                    settled.Add(m.Provider);
                    break;
            }
        }

        // Include providers that have NO mapping row at all and are enabled (e.g. a newly enabled
        // provider): they have no entry in `mappings`, so they were never searched. They stay
        // pending until this pass materializes an Unmatched row for them.
        if (enabledProviders != null)
        {
            foreach (var provider in enabledProviders)
            {
                if (!mappings.Any(m => m.Provider == provider))
                    pending.Add(provider);
            }
        }

        pending.ExceptWith(settled);
        return pending;
    }

    private async Task SyncRensaioJsonAsync(Guid seriesId, CancellationToken token)
    {
        try
        {
            await _seriesStateService.SyncToRensaioJsonAsync(seriesId, token);
        }
        catch
        {
            // best-effort
        }
    }
}