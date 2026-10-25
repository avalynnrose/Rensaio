using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Scrobbling;
using RensaioBackend.Services.Scrobbling.Abstractions;
using RensaioBackend.Utils.ImageHashing;
using Microsoft.Extensions.Logging;
using System.Text;

namespace RensaioBackend.Services.Metadata;

/// <summary>A pre-existing provider link seeded into the matcher (from either DB).</summary>
public sealed class MetadataLinkSeed
{
    public ExternalSeriesProvider Provider { get; set; }
    public string ExternalSeriesId { get; set; } = string.Empty;
    public string? ExternalSeriesTitle { get; set; }
    public List<string> LinkedSitesIds { get; set; } = [];

    /// <summary>Seed confidence; 1.0 for already-linked rows.</summary>
    public double Confidence { get; set; } = 1.0;
}

/// <summary>Result of running the database-agnostic metadata matcher.</summary>
public sealed class MetadataMatchResult
{
    public List<MetaDataLinkEntry> Links { get; set; } = [];
    public List<MetaDataLinkEntry> Suggestions { get; set; } = [];

    /// <summary>Distinct (provider, id) detail fetches performed during this pass (dedup set).</summary>
    public HashSet<(ExternalSeriesProvider Site, string ExternalId)> FetchedIds { get; set; } = new(new MetadataMatchCore.SiteEdgeComparer());

    public int ProvidersSearched { get; set; }
}

/// <summary>
/// Database-agnostic cross-provider title matcher. Given title candidates, ordered metadata
/// providers, and any pre-existing links, searches every provider, seeds a (site,id) graph,
/// then propagates through provider-declared cross-site links (MangaBaka <c>source</c>,
/// AniList <c>idMal</c>, Kitsu <c>mappings</c>, MangaDex <c>links</c>) to link to ALL providers
/// with high confidence.
///
/// Persistence is the caller's responsibility:
///   - Rensaio DB via <see cref="MetadataLinkEngine"/> (global SeriesMappings),
///   - contribution DB via <c>ContributionMappingService</c> (ContributionMetadata rows).
/// This single implementation replaces duplicated per-DB matchers.
/// </summary>
public sealed class MetadataMatchCore
{
    private const double AutoThreshold = 0.95;
    private const double SuggestThreshold = 0.80;
    private const double HopDecay = 0.9;
    private const int MaxHops = 3;

    /// <summary>Max recursive sweeps allowed in the second pass (safety net — the termination
    /// rule "all titles tested OR all providers matched" normally ends it in one sweep).</summary>
    private const int MaxSecondPassRounds = 5;

    /// <summary>Per-provider cap on how many distinct titles may be searched in the second pass.</summary>
    private const int MaxSecondPassCandidatesPerProvider = 50;

    /// <summary>Title-score window (percentage points) within which candidates are image tie-broken.</summary>
    private const int ImageTieBreakWindow = 5;

    /// <summary>Max distinct candidate ids hashed for an image tie-break.</summary>
    private const int MaxImageContenders = 6;

    /// <summary>Cover distance up to which an image tie-break may override raw title order.</summary>
    private const int ImageTieBreakAcceptThreshold = 64;

    private readonly ExternalSeriesProviderFactory _factory;
    private readonly ImageMatchCore _imageMatch;
    private readonly ILogger<MetadataMatchCore> _logger;

    public MetadataMatchCore(ExternalSeriesProviderFactory factory, ImageMatchCore imageMatch, ILogger<MetadataMatchCore> logger)
    {
        _factory = factory;
        _imageMatch = imageMatch;
        _logger = logger;
    }

    /// <summary>
    /// Ordered metadata providers the matcher queries (hub first), filtered to providers that are
    /// actually instantiated and metadata-capable. Shared by both the External Mappings page and
    /// the Contribution Mappings page so they render and query the same provider set.
    /// </summary>
    public List<IExternalSeriesProvider> GetMetadataProvidersInOrder()
    {
        // Stable order: hub first, then everything else.
        var order = new List<ExternalSeriesProvider>
        {
            ExternalSeriesProvider.MangaBaka,
            ExternalSeriesProvider.AniList,
            ExternalSeriesProvider.MangaUpdates,
            ExternalSeriesProvider.Bangumi,
            ExternalSeriesProvider.MangaDex,
            ExternalSeriesProvider.MyAnimeList,
            ExternalSeriesProvider.Kitsu,
            ExternalSeriesProvider.ComicVine,
            ExternalSeriesProvider.Metron,          // deferred — factory returns null → skipped
            ExternalSeriesProvider.GrandComicsDatabase // deferred — factory returns null → skipped
        };
        return order
            .Select(p => _factory.GetProvider(p))
            .Where(p => p != null && SeriesMetadataResolver.IsMetadataProvider(p))
            .ToList()!;
    }

    /// <summary>
    /// Runs the match algorithm. <paramref name="providers"/> must already be filtered to the
    /// enabled set and authenticated by the caller. <paramref name="blockedIds"/> and
    /// <paramref name="skippedProviders"/> encode id-level and provider-level ignore/block rules
    /// (computed by the caller from its own persisted state). <paramref name="enabledProviders"/>
    /// optionally narrows second-pass and propagation targets (null = all providers enabled).
    /// </summary>
    /// <summary>Convenience overload for callers that have a local cover but no Mihon provider id.</summary>
    public Task<MetadataMatchResult> MatchAsync(
        List<string> titleCandidates,
        IReadOnlyList<IExternalSeriesProvider> providers,
        List<MetadataLinkSeed> seeds,
        Dictionary<ExternalSeriesProvider, HashSet<string>> blockedIds,
        HashSet<ExternalSeriesProvider> skippedProviders,
        HashSet<ExternalSeriesProvider>? enabledProviders,
        string? localCoverUrl,
        CancellationToken token = default)
        => MatchAsync(titleCandidates, providers, seeds, blockedIds, skippedProviders,
            enabledProviders, token, localCoverUrl, null);

    public async Task<MetadataMatchResult> MatchAsync(
        List<string> titleCandidates,
        IReadOnlyList<IExternalSeriesProvider> providers,
        List<MetadataLinkSeed> seeds,
        Dictionary<ExternalSeriesProvider, HashSet<string>> blockedIds,
        HashSet<ExternalSeriesProvider> skippedProviders,
        HashSet<ExternalSeriesProvider>? enabledProviders = null,
        CancellationToken token = default,
        string? localCoverUrl = null,
        string? mihonProviderId = null)
    {
        var result = new MetadataMatchResult();

        // Local cover hash (computed once) used to corroborate borderline title matches.
        // A null/empty hash simply disables image corroboration for this pass.
        PdqHash256? localHash = null;
        if (!string.IsNullOrWhiteSpace(localCoverUrl))
        {
            try
            {
                localHash = await _imageMatch.GetLocalHashAsync(localCoverUrl, mihonProviderId, token).ConfigureAwait(false);
                if (localHash is { IsEmpty: false })
                    _logger.LogInformation("Local cover hash ready — image corroboration ENABLED ({Url})", localCoverUrl);
                else
                    _logger.LogInformation("Local cover could not be hashed — image corroboration DISABLED ({Url})", localCoverUrl);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Local cover hash unavailable — image corroboration DISABLED ({Url})", localCoverUrl);
            }
        }
        else
        {
            _logger.LogInformation("No local cover supplied — image matching SKIPPED for this pass");
        }

        _logger.LogInformation("Matching {CandidateCount} title candidate(s) [image {ImageState}]: {Titles}",
            titleCandidates.Count, localHash is { IsEmpty: false } ? "on" : "off", string.Join(" | ", titleCandidates));

        // Node set: provider -> external ID + confidence + titles + linked IDs.
        var nodes = new Dictionary<ExternalSeriesProvider, MetaDataLinkEntry>();
        // Edge set: canonical "site:id" -> provider entry.
        var edges = new Dictionary<(ExternalSeriesProvider Site, string ExternalId), double>(new SiteEdgeComparer());

        // 1. Seed from existing links (status-aware rules already applied by the caller).
        foreach (var seed in seeds)
        {
            if (string.IsNullOrWhiteSpace(seed.ExternalSeriesId)) continue;
            // A stored placeholder id (e.g. myanimelist:0) is not a real link; seeding it would
            // enqueue a doomed detail fetch and re-persist the bogus edge.
            if (IsBogusExternalId(seed.ExternalSeriesId)) continue;
            // Id-level block: the caller's seeds normally exclude blocked rows, but a seed can be
            // reused from a sibling mapping (cross-mapping reuse). A blocked (provider, id) must
            // NEVER be re-linked, so drop it here regardless of where the seed came from.
            if (blockedIds.TryGetValue(seed.Provider, out var seedBlock) &&
                seedBlock.Contains(seed.ExternalSeriesId)) continue;
            AddNode(nodes, seed.Provider, seed.ExternalSeriesId, seed.ExternalSeriesTitle ?? string.Empty,
                seed.Confidence, SeriesLinkStatus.Linked);
            edges[(seed.Provider, seed.ExternalSeriesId)] = seed.Confidence;
            foreach (var linked in seed.LinkedSitesIds)
            {
                var (site, id) = SplitSiteId(linked);
                if (site.HasValue && !string.IsNullOrWhiteSpace(id))
                    edges[(site.Value, id)] = 1.0;
            }
        }

        _logger.LogDebug("Seeded {SeedCount} existing link(s); {NodeCount} node(s) pre-linked",
            seeds.Count, nodes.Count);

        // Bookkeeping to avoid fetching the same provider detail more than once per link pass
        // (e.g. MangaUpdates id reached directly + via two inbound edges).
        var fetchedIds = result.FetchedIds;

        // Per-provider history of titles already searched this pass (round 1 records the local
        // candidates; the second pass appends every title it tries). Prevents re-searching the
        // same title on the same provider.
        var searchedTitles = new Dictionary<ExternalSeriesProvider, HashSet<string>>();

        // Follows provider-declared edges to resolve ADDITIONAL providers via DETAIL FETCH ONLY
        // (never a search), up to MaxHops. Invoked (a) right after seeding — a series that already
        // has providers must not have those providers' declared siblings searched — and (b) right
        // after EVERY successful search match, so a declared link resolves the next providers
        // before we would otherwise search them.
        async Task PropagateEdgesAsync()
        {
            for (int hop = 1; hop <= MaxHops; hop++)
            {
                var frontier = edges.Where(e => e.Value > 0).ToList();
                bool addedAny = false;
                foreach (var edge in frontier)
                {
                    var (site, id) = edge.Key;
                    var provider = _factory.GetProvider(site);
                    if (provider == null || !SeriesMetadataResolver.IsMetadataProvider(provider)) continue;
                    if (enabledProviders != null && !enabledProviders.Contains(site)) continue; // not enabled
                    if (skippedProviders.Contains(site)) continue; // provider-level ignore
                    if (blockedIds.TryGetValue(site, out var idset) && idset.Contains(id)) continue; // id-level block
                    if (nodes.ContainsKey(site)) continue; // already linked
                    if (!fetchedIds.Add((site, id))) continue; // already fetched this pass

                    var conf = edge.Value * HopDecay;
                    if (conf < SuggestThreshold) continue;

                    _logger.LogInformation("Hop {Hop} following declared edge {Site}:{Id} (edge conf {Conf:F2}) — detail fetch + map, no search",
                        hop, site, id, edge.Value);

                    var metadata = await provider.FetchSeriesMetadataAsync(id, token);
                    if (metadata == null) continue;

                    // AlternateTitles never contains the primary title (see TitleListBuilder);
                    // score against title + alternates but persist only the alternates.
                    var altTitles = metadata.AlternativeTitles;
                    var scoringTitles = new List<string> { metadata.Title };
                    scoringTitles.AddRange(altTitles);
                    var matchScore = ScoreAgainstLocal(scoringTitles, titleCandidates);
                    var finalConfidence = Math.Max(conf, matchScore);
                    // Cover corroboration: a borderline title (suggest band) whose cover matches the
                    // local series cover is approved to Linked — same rule as the round-1 search.
                    if (localHash is { IsEmpty: false }
                        && finalConfidence >= SuggestThreshold && finalConfidence < AutoThreshold
                        && await IsCoverCorroboratedAsync(metadata.CoverUrl, localHash.Value, token).ConfigureAwait(false))
                    {
                        finalConfidence = AutoThreshold;
                    }
                    var status = finalConfidence >= AutoThreshold ? SeriesLinkStatus.Linked : SeriesLinkStatus.Suggested;

                    // Pass the detail fetch's cover + native metadata through: this path has the
                    // payload in hand and, unlike ExpandViaDetailAsync, would otherwise drop it —
                    // leaving propagated providers with no provider cover on the Contribution page.
                    AddNode(nodes, site, metadata.ExternalId, metadata.Title, finalConfidence, status,
                        metadata.LinkedSitesIds, altTitles, metadata.CoverUrl, metadata.MetaData);

                    if (status == SeriesLinkStatus.Linked)
                        edges[(site, metadata.ExternalId)] = finalConfidence;

                    foreach (var linked in metadata.LinkedSitesIds)
                    {
                        var (lsite, lid) = SplitSiteId(linked);
                        if (lsite.HasValue && !string.IsNullOrWhiteSpace(lid))
                            edges[(lsite.Value, lid)] = Math.Max(edges.GetValueOrDefault((lsite.Value, lid)), finalConfidence * HopDecay);
                    }

                    // expand local title pool with the discovered titles for better matching
                    foreach (var t in scoringTitles)
                        titleCandidates.Add(t);
                    titleCandidates = titleCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    addedAny = true;
                }
                if (!addedAny) break;
            }
        }

        // (a) Expand the seeded nodes once (fetch detail → cover/metadata + declared links).
        foreach (var seedNode in nodes.Values.ToList())
        {
            var provider = _factory.GetProvider(seedNode.Provider);
            if (provider == null) continue;
            if (enabledProviders != null && !enabledProviders.Contains(seedNode.Provider)) continue;
            await ExpandViaDetailAsync(provider, seedNode, nodes, edges, fetchedIds, token);
            result.ProvidersSearched++;
        }

        // (a) Follow the seeds' declared links BEFORE any search: providers already known for this
        //     series are not searched, and their declared siblings are resolved via detail only.
        await PropagateEdgesAsync();

        // 2. Search the still-unmatched providers ONE BY ONE (hub first, per the caller's order).
        //    After EACH match the declared links are propagated immediately, so a hub that declares
        //    the other providers resolves them via detail — they are never searched. Providers
        //    resolved by a seed or by propagation are skipped entirely. This is what removes the
        //    "same title searched on every provider" waste.
        foreach (var provider in providers)
        {
            var providerType = provider.ProviderType;
            if (skippedProviders.Contains(providerType)) continue;
            if (nodes.ContainsKey(providerType)) continue; // already matched via seed / propagation

            result.ProvidersSearched++;

            MetaDataLinkEntry? best;
            try
            {
                blockedIds.TryGetValue(providerType, out var idBlocklist);

                // Round 1 searches the FULL local candidate set. The per-provider
                // FilterLookupTitles narrows only which titles are SENT (applied inside
                // SearchAndScoreAsync); scoring still runs against every local candidate.
                best = await SearchAndScoreAsync(provider, titleCandidates, token, idBlocklist, localHash);

                if (!searchedTitles.TryGetValue(providerType, out var history))
                {
                    history = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    searchedTitles[providerType] = history;
                }
                foreach (var title in titleCandidates.Where(t => !string.IsNullOrWhiteSpace(t)))
                    history.Add(title);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                best = null;
            }

            if (best == null)
            {
                _logger.LogInformation("Round-1 {Provider} → no link", providerType);
                continue;
            }

            if (best.Confidence >= AutoThreshold)
            {
                AddNode(nodes, providerType, best.ExternalSeriesId, best.Title ?? string.Empty, best.Confidence, SeriesLinkStatus.Linked);
                edges[(providerType, best.ExternalSeriesId)] = best.Confidence;
                await ExpandViaDetailAsync(provider, nodes[providerType], nodes, edges, fetchedIds, token);
                _logger.LogInformation("Round-1 {Provider} → {Id} '{Title}' (Linked, conf {Conf:F2})",
                    providerType, best.ExternalSeriesId, best.Title, best.Confidence);

                // Resolve this match's declared links BEFORE searching the remaining providers.
                await PropagateEdgesAsync();
            }
            else if (best.Confidence >= SuggestThreshold)
            {
                result.Suggestions.Add(best);
                _logger.LogInformation("Round-1 {Provider} → {Id} '{Title}' (Suggested, conf {Conf:F2})",
                    providerType, best.ExternalSeriesId, best.Title, best.Confidence);
            }
            else
            {
                _logger.LogInformation("Round-1 {Provider} → no link (best {Best:F2})", providerType, best.Confidence);
            }
        }

        // 3. Second pass: using the titles gathered from ALREADY-LINKED providers only, try to
        //    fill the still-unmatched providers (titles-based search). FilterLookupTitles is applied
        //    per-provider here. Round-1 titles are never retested (per-provider history).
        await SecondPassLinkUnmatchedAsync(providers, nodes, edges, fetchedIds, searchedTitles,
            enabledProviders, skippedProviders, blockedIds, result, localHash, token).ConfigureAwait(false);

        // 3.5. Deduplicate explicit provider-node expansions: skip if already fetched via propagation.
        foreach (var node in nodes.Values.Where(n => n.Status == SeriesLinkStatus.Linked))
        {
            fetchedIds.Add((node.Provider, node.ExternalSeriesId));
        }

        // 4. Final propagation: follow any declared edges surfaced by the second pass.
        await PropagateEdgesAsync();

        result.Links = nodes.Values.Where(n => n.Status == SeriesLinkStatus.Linked).ToList();
        _logger.LogInformation(
            "Done — {Linked} linked, {Suggested} suggestion(s), {Edges} edge(s), {Searched} provider(s) searched",
            result.Links.Count, result.Suggestions.Count, edges.Count, result.ProvidersSearched);
        return result;
    }

    /// <summary>
    /// Round-2 recursive sweep: using titles gathered from ALREADY-LINKED providers, attempts to
    /// link the still-unmatched providers. Runs until every unmatched provider has been searched
    /// with all its candidate titles, or all providers are matched. A matched provider that
    /// contributes NEW titles does NOT extend this pass — those titles surface on the next
    /// MatchAsync invocation. FilterLookupTitles is applied here (per-provider) and ONLY here.
    /// </summary>
    private async Task SecondPassLinkUnmatchedAsync(
        IReadOnlyList<IExternalSeriesProvider> providers,
        Dictionary<ExternalSeriesProvider, MetaDataLinkEntry> nodes,
        Dictionary<(ExternalSeriesProvider Site, string ExternalId), double> edges,
        HashSet<(ExternalSeriesProvider Site, string Id)> fetchedIds,
        Dictionary<ExternalSeriesProvider, HashSet<string>> searchedTitles,
        HashSet<ExternalSeriesProvider>? enabledProviders,
        HashSet<ExternalSeriesProvider> skippedProviders,
        Dictionary<ExternalSeriesProvider, HashSet<string>> blockedIds,
        MetadataMatchResult result,
        PdqHash256? localHash,
        CancellationToken token)
    {
        // Already-linked providers contribute titles; unmatched providers are the targets.
        var linked = nodes.Where(kv => kv.Value.Status == SeriesLinkStatus.Linked).Select(kv => kv.Value).ToList();
        if (linked.Count == 0) return;
        if (providers.Count == 0) return;

        for (int round = 0; round < MaxSecondPassRounds; round++)
        {
            // Destination providers = enabled providers that are NOT yet linked and NOT skipped.
            var unmatchedProviders = providers
                .Where(p =>
                    (enabledProviders == null || enabledProviders.Contains(p.ProviderType))
                    && !skippedProviders.Contains(p.ProviderType)
                    && !nodes.ContainsKey(p.ProviderType))
                .ToList();
            if (unmatchedProviders.Count == 0) return; // (b) all providers matched

            // Pool = distinct titles from the already-linked providers (current linked nodes).
            var pool = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes.Values.Where(n => n.Status == SeriesLinkStatus.Linked))
            {
                if (!string.IsNullOrWhiteSpace(node.Title)) pool.Add(node.Title);
                foreach (var t in node.AlternativeTitles)
                    if (!string.IsNullOrWhiteSpace(t)) pool.Add(t);
            }

            foreach (var provider in unmatchedProviders)
            {
                var providerType = provider.ProviderType;
                var history = searchedTitles.TryGetValue(providerType, out var h) ? h :
                    (searchedTitles[providerType] = new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                // Round 2 uses provider.FilterLookupTitles (only here) then subtracts history.
                var filtered = provider.FilterLookupTitles(pool);
                var pending = filtered
                    .Where(t => !string.IsNullOrWhiteSpace(t) && !history.Contains(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Bounded sweep per provider (safety cap).
                var searchedThisProvider = 0;
                foreach (var title in pending)
                {
                    if (searchedThisProvider >= MaxSecondPassCandidatesPerProvider) break;
                    searchedThisProvider++;
                    history.Add(title);
                    if (token.IsCancellationRequested) return;

                    try
                    {
                        var results = await provider.SearchSeriesAsync(title, token);
                        ScrobblerSearchResult? best = null;
                        foreach (var r in results)
                        {
                            if (string.IsNullOrWhiteSpace(r.ExternalId) || string.IsNullOrWhiteSpace(r.Title)) continue;
                            if (blockedIds.TryGetValue(providerType, out var idset) && idset.Contains(r.ExternalId)) continue;
                            if (best == null) best = r;
                        }
                        if (best == null) continue;

                        var candidateTitles = new List<string>();
                        candidateTitles.Add(best.Title);
                        if (best.AlternateTitles != null)
                            foreach (var alt in best.AlternateTitles)
                                candidateTitles.Add(alt);
                        var score = ScoreAgainstLocal(candidateTitles, pool);
                        // Cover corroboration: a borderline title (suggest band) whose cover matches
                        // the local series cover is approved to Linked — same rule as the round-1 search.
                        if (localHash is { IsEmpty: false }
                            && score >= SuggestThreshold && score < AutoThreshold
                            && await IsCoverCorroboratedAsync(best.CoverUrl, localHash.Value, token).ConfigureAwait(false))
                        {
                            score = AutoThreshold;
                        }
                        if (score >= AutoThreshold)
                        {
                            _logger.LogInformation(
                                "Second pass linked {Provider}:{Id} ('{Title}', score {Score:F2})",
                                providerType, best.ExternalId, best.Title, score);
                            AddNode(nodes, providerType, best.ExternalId, best.Title ?? string.Empty, score, SeriesLinkStatus.Linked);
                            edges[(providerType, best.ExternalId)] = score;
                            foreach (var l in best.LinkedSitesIds ?? [])
                            {
                                var (site, id) = SplitSiteId(l);
                                if (site.HasValue && !string.IsNullOrWhiteSpace(id))
                                    edges[(site.Value, id)] = Math.Max(edges.GetValueOrDefault((site.Value, id)), score * HopDecay);
                            }
                            // Record the matched result's titles as searched for this provider
                            // (they are now "known" — a future pass can use them on other providers,
                            // but we must never re-search THIS provider with them).
                            foreach (var t in candidateTitles)
                                if (!string.IsNullOrWhiteSpace(t)) history.Add(t);

                            // Expand + persist via the existing path.
                            await ExpandViaDetailAsync(provider, nodes[providerType], nodes, edges, fetchedIds, token);
                            result.Links.Add(nodes[providerType]);
                            break; // this provider is now matched — stop searching it
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        // provider failure — continue with next title
                    }
                }
            }

            // Termination: (a) every provider with an unmatched row has been searched with ALL its
            // candidate titles (its filtered-pool set is fully covered by history), OR (b) all
            // providers are matched. A match that introduced new titles does NOT extend this pass —
            // they surface on the next MatchAsync call.
            var stillPending = unmatchedProviders.Any(p =>
                !nodes.ContainsKey(p.ProviderType)
                && providerHasPendingTitles(p, pool, searchedTitles));
            if (!stillPending) break;
        }
    }

    private static bool providerHasPendingTitles(
        IExternalSeriesProvider provider,
        HashSet<string> pool,
        Dictionary<ExternalSeriesProvider, HashSet<string>> searchedTitles)
    {
        var history = searchedTitles.TryGetValue(provider.ProviderType, out var h) ? h
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return provider.FilterLookupTitles(pool).Any(t =>
            !string.IsNullOrWhiteSpace(t) && !history.Contains(t));
    }

    private async Task<MetaDataLinkEntry?> SearchAndScoreAsync(
        IExternalSeriesProvider provider, IEnumerable<string> localCandidates, CancellationToken token,
        HashSet<string>? blockedIds = null, PdqHash256? localHash = null)
    {
        // Materialize once: used for the incremental short-circuit probe and the final score.
        var local = localCandidates as IList<string> ?? localCandidates.ToList();

        var candidates = new List<(string SearchTitle, string Id)>();
        var lookup = new Dictionary<string, OscillatingResult>(StringComparer.OrdinalIgnoreCase);

        // Only the titles actually sent to the provider are filtered. The local candidate
        // list stays intact for scoring below, so a title this provider's search cannot use
        // (e.g. ComicVine rejects CJK / Bangumi keeps only CJK) still participates in match
        // scoring against the results returned for the provider's usable titles. Providers
        // that do not narrow (the default pass-through) behave exactly as before.
        //
        // Near-duplicate collapse: spellings that differ only by quote style, punctuation or a
        // trailing "(Polish)"/"(Korean)" qualifier map to one key, so the provider is queried
        // ONCE per distinct title instead of once per spelling (was ~21 searches per provider).
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var searchTitles = provider.FilterLookupTitles(local)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Where(t => seenKeys.Add(SearchTitleKey(t)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var title in searchTitles)
        {
            IReadOnlyList<ScrobblerSearchResult> results;
            try
            {
                results = await provider.SearchSeriesAsync(title, token);
            }
            catch
            {
                continue;
            }
            foreach (var r in results)
            {
                if (string.IsNullOrWhiteSpace(r.ExternalId) || string.IsNullOrWhiteSpace(r.Title)) continue;
                if (blockedIds != null && blockedIds.Contains(r.ExternalId)) continue; // id-level block rule
                if (lookup.ContainsKey(r.ExternalId)) continue;
                lookup[r.ExternalId] = new OscillatingResult { Title = r.Title, Linked = r.LinkedSitesIds, Alt = r.AlternateTitles, CoverUrl = r.CoverUrl };
                candidates.Add((r.Title, r.ExternalId));
                foreach (var alt in r.AlternateTitles.Where(a => !string.IsNullOrWhiteSpace(a)))
                    candidates.Add((alt, r.ExternalId));
            }

            // Short-circuit: once the accumulated results already auto-match, further title
            // variants cannot improve the winner — stop searching this provider.
            if (candidates.Count > 0)
            {
                var probe = TitleMatcher.MatchTitles(local.ToList(), candidates, 0);
                if (probe.Length > 0 && probe[0].Percentage / 100.0 >= AutoThreshold)
                    break;
            }
        }

        if (candidates.Count == 0) return null;

        var scored = TitleMatcher.MatchTitles(local.ToList(), candidates, 0);
        if (scored.Length == 0) return null;

        var best = scored[0];
        var info = lookup.GetValueOrDefault(best.Id);
        var titleConfidence = best.Percentage / 100.0;

        // Image (PDQ) tie-break + corroboration.
        //
        // The title matcher can return several DISTINCT candidates within a few points of the top
        // (same title, different edition/region/entry). When a local cover hash is available we
        // hash the covers of every candidate within ImageTieBreakWindow of the top title score and
        // pick the closest cover:
        //   * a candidate whose cover is the SAME image wins the tie-break outright, and if its
        //     title is in the suggest band it is promoted to Linked,
        //   * a candidate with a meaningfully closer (but not identical) cover is preferred over
        //     the raw title order,
        //   * unrelated covers fall through to the title order.
        // Image alone never creates a link: promotion requires the title to be >= SuggestThreshold.
        var imageCorroborated = false;
        var imageUsed = false;
        int? imageDistance = null;
        var selected = best;
        var selectedInfo = info;

        if (localHash is { IsEmpty: false })
        {
            var contenders = scored
                .GroupBy(x => x.Id)
                .Select(g => g.First())
                .Where(x => x.Percentage >= best.Percentage - ImageTieBreakWindow)
                .Take(MaxImageContenders)
                .ToList();

            var needImage = contenders.Count > 1
                || (titleConfidence >= SuggestThreshold && titleConfidence < AutoThreshold);

            if (needImage)
            {
                try
                {
                    var localPdq = localHash.Value;
                    var coverUrls = contenders
                        .Select(c => lookup.GetValueOrDefault(c.Id)?.CoverUrl)
                        .Where(u => !string.IsNullOrWhiteSpace(u))
                        .Select(u => u!)
                        .ToList();

                    var hashes = await _imageMatch.GetProviderHashesAsync(coverUrls, token).ConfigureAwait(false);

                    var bestDistance = int.MaxValue;
                    var closest = best;
                    OscillatingResult? closestInfo = info;
                    foreach (var c in contenders)
                    {
                        var cover = lookup.GetValueOrDefault(c.Id)?.CoverUrl;
                        if (string.IsNullOrWhiteSpace(cover) || !hashes.TryGetValue(cover, out var candidateHash))
                            continue;

                        var distance = PdqHashUtility.Compare(in localPdq, in candidateHash);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            closest = c;
                            closestInfo = lookup.GetValueOrDefault(c.Id);
                        }
                    }

                    if (bestDistance != int.MaxValue)
                        imageDistance = bestDistance;

                    if (bestDistance <= ImageMatchScoring.SameImageThreshold)
                    {
                        // Same cover image → authoritative tie-break + (band-limited) promotion.
                        selected = closest;
                        selectedInfo = closestInfo;
                        imageUsed = true;
                        if (closest.Percentage / 100.0 >= SuggestThreshold)
                            imageCorroborated = true;
                        _logger.LogInformation(
                            "Image matched {Provider}:{Id} (image dist {Distance}, similarities {Pct}%) over {Contenders} title candidate(s)",
                            provider.ProviderType, selected.Id, bestDistance, ImageMatchScoring.DistanceToPercentage(bestDistance), contenders.Count);
                    }
                    else if (bestDistance <= ImageTieBreakAcceptThreshold && closest.Id != best.Id)
                    {
                        // Meaningfully closer cover → prefer it over the raw title order.
                        selected = closest;
                        selectedInfo = closestInfo;
                        imageUsed = true;
                        _logger.LogInformation(
                            "Image tie-break {Provider}:{Id} (image dist {Distance}, similarities {Pct}%) over {Contenders} title candidate(s)",
                            provider.ProviderType, selected.Id, bestDistance, ImageMatchScoring.DistanceToPercentage(bestDistance), contenders.Count);
                    }
                    else
                    {
                        // Covers are unrelated → keep the title order. Still record the distance of
                        // the best (unrelated) cover so the diagnostic is not silently lost.
                        if (imageDistance is { } imgDist)
                            _logger.LogInformation(
                                "Image checked {Provider} — closest cover dist {Distance} (similarity {Pct}%) over {Contenders} candidate(s); title order kept",
                                provider.ProviderType, imgDist, ImageMatchScoring.DistanceToPercentage(imgDist), contenders.Count);
                        else
                            _logger.LogInformation(
                                "Image checked {Provider} but no candidate cover could be hashed ({Contenders} candidate(s))",
                                provider.ProviderType, contenders.Count);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // image unavailable — fall back to title-only behaviour
                    _logger.LogInformation(ex, "Image comparison failed for {Provider}; title-only result used", provider.ProviderType);
                }
            }
            else
            {
                _logger.LogInformation(
                    "Image not needed for {Provider} (title {Title:F2}, {Contenders} candidate(s))",
                    provider.ProviderType, titleConfidence, contenders.Count);
            }
        }

        var selectedTitleConfidence = selected.Percentage / 100.0;
        var finalConfidence = imageCorroborated && selectedTitleConfidence >= SuggestThreshold
            ? AutoThreshold
            : selectedTitleConfidence;

        return new MetaDataLinkEntry
        {
            Provider = provider.ProviderType,
            ExternalSeriesId = selected.Id,
            Title = selectedInfo?.Title ?? selected.SearchTitle,
            Confidence = finalConfidence,
            Status = finalConfidence >= AutoThreshold ? SeriesLinkStatus.Linked : SeriesLinkStatus.Suggested,
            LinkedSitesIds = selectedInfo?.Linked ?? [],
            AlternativeTitles = selectedInfo?.Alt ?? [],
            CoverUrl = selectedInfo?.CoverUrl,
            ImageDistance = imageDistance,
            ImageSimilarity = imageDistance.HasValue ? ImageMatchScoring.DistanceToPercentage(imageDistance.Value) : null,
            ImageMatched = imageDistance is { } d && ImageMatchScoring.IsSameImage(d),
            ImageUsed = imageUsed
        };
    }

    /// <summary>
    /// Collapses near-duplicate title spellings for SEARCH purposes only: strips a trailing
    /// parenthetical language/qualifier, folds quote/punctuation variants, lower-cases. Scoring
    /// still uses the exact local candidates, so accuracy is unaffected.
    /// </summary>
    private static string SearchTitleKey(string title)
    {
        var t = title.Trim();

        // Strip a trailing "(Polish)"/"(Korean)"/"(Official)" style qualifier.
        if (t.Length > 0 && t[^1] == ')')
        {
            int open = t.LastIndexOf('(');
            if (open > 0)
            {
                var inner = t[(open + 1)..^1];
                if (inner.Length > 0 && inner.Length <= 20
                    && inner.All(c => char.IsLetter(c) || c == ' ' || c == '-' || c == '_'))
                {
                    t = t[..open].Trim();
                }
            }
        }

        var sb = new StringBuilder(t.Length);
        foreach (var ch in t.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else sb.Append(' '); // quotes, punctuation and whitespace all become separators
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task ExpandViaDetailAsync(
        IExternalSeriesProvider provider,
        MetaDataLinkEntry node,
        Dictionary<ExternalSeriesProvider, MetaDataLinkEntry> nodes,
        Dictionary<(ExternalSeriesProvider, string), double> edges,
        HashSet<(ExternalSeriesProvider Site, string Id)> fetchedIds,
        CancellationToken token)
    {
        try
        {
            // Dedup: never fetch the same (provider, id) detail more than once per link pass —
            // e.g. an id reached directly AND via propagation from another provider's edge.
            if (!fetchedIds.Add((node.Provider, node.ExternalSeriesId))) return;

            var metadata = await provider.FetchSeriesMetadataAsync(node.ExternalSeriesId, token);
            if (metadata == null)
            {
                _logger.LogInformation("Detail fetch {Provider}:{Id} returned nothing",
                    node.Provider, node.ExternalSeriesId);
                return;
            }

            // capture cover + native metadata so the caller can persist them to the row
            node.CoverUrl ??= metadata.CoverUrl;

            _logger.LogInformation(
                "Detail {Provider}:{Id} → '{Title}', cover {Cover}, {Links} declared link(s), {Alts} alternate(s)",
                node.Provider, node.ExternalSeriesId, metadata.Title,
                string.IsNullOrWhiteSpace(node.CoverUrl) ? "(none)" : "yes",
                metadata.LinkedSitesIds.Count, metadata.AlternativeTitles.Count);
            node.MetaData = !string.IsNullOrWhiteSpace(metadata.MetaData) ? metadata.MetaData : node.MetaData;

            // enrich the node. Seed nodes carry an EMPTY (non-null) title, and `??=` would not
            // replace it — so fall back on whitespace too, otherwise the provider's canonical
            // title is lost from the node's title set.
            if (string.IsNullOrWhiteSpace(node.Title))
                node.Title = metadata.Title;
            foreach (var t in metadata.AlternativeTitles)
                if (!node.AlternativeTitles.Contains(t, StringComparer.OrdinalIgnoreCase))
                    node.AlternativeTitles.Add(t);
            foreach (var l in metadata.LinkedSitesIds)
                if (!node.LinkedSitesIds.Contains(l, StringComparer.OrdinalIgnoreCase))
                    node.LinkedSitesIds.Add(l);

            foreach (var l in metadata.LinkedSitesIds)
            {
                var (site, id) = SplitSiteId(l);
                if (site.HasValue && !string.IsNullOrWhiteSpace(id))
                {
                    var conf = node.Confidence * HopDecay;
                    edges[(site.Value, id)] = Math.Max(edges.GetValueOrDefault((site.Value, id)), conf);
                }
            }
        }
        catch
        {
            // ignore detail failures
        }
    }

    private double ScoreAgainstLocal(IEnumerable<string> candidateTitles, IEnumerable<string> localCandidates)
    {
        var candidates = candidateTitles
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select((t, i) => (t, (string)("__id__" + i)))
            .ToList();
        var scored = TitleMatcher.MatchTitles(localCandidates.ToList(), candidates, 0);
        return scored.Length > 0 ? scored[0].Percentage / 100.0 : 0;
    }

    /// <summary>
    /// True when the provider cover is the SAME image as the local cover (within
    /// <see cref="ImageMatchScoring.SameImageThreshold"/>) — used to approve a borderline title in
    /// the propagation and second-pass paths, mirroring the round-1 search. Best-effort: an
    /// unavailable/unhashable cover returns false so the caller keeps the title-only score.
    /// </summary>
    private async Task<bool> IsCoverCorroboratedAsync(
        string? providerCoverUrl, PdqHash256 localHash, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(providerCoverUrl) || localHash.IsEmpty)
            return false;
        try
        {
            var hashes = await _imageMatch
                .GetProviderHashesAsync(new[] { providerCoverUrl }, token).ConfigureAwait(false);
            if (!hashes.TryGetValue(providerCoverUrl, out var candidateHash))
                return false;
            var distance = PdqHashUtility.Compare(in localHash, in candidateHash);
            if (distance <= ImageMatchScoring.SameImageThreshold)
            {
                _logger.LogInformation("Cover corroboration approved a borderline title (image dist {Distance}, similarity {Pct}%)",
                    distance, ImageMatchScoring.DistanceToPercentage(distance));
                return true;
            }
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cover corroboration failed for {Url}", providerCoverUrl);
            return false;
        }
    }

    private static (ExternalSeriesProvider? Site, string Id) SplitSiteId(string linked)
    {
        var idx = linked.IndexOf(':');
        if (idx <= 0 || idx == linked.Length - 1)
            return (null, string.Empty);
        var slug = linked[..idx];
        var id = linked[(idx + 1)..];
        // Reject bogus placeholder ids (e.g. AniList's idMal=0 for manga with no MAL entry,
        // MangaBaka/MangaDex "0"). Following such an edge only produces a doomed detail fetch.
        if (IsBogusExternalId(id))
            return (null, string.Empty);
        if (!Enum.TryParse<ExternalSeriesProvider>(slug, true, out var parsed))
        {
            // Try slug-name resolution via resolver is not available here (static); fallback:
            parsed = slug.ToLowerInvariant() switch
            {
                "myanimelist" => ExternalSeriesProvider.MyAnimeList,
                "anilist" => ExternalSeriesProvider.AniList,
                "comicvine" => ExternalSeriesProvider.ComicVine,
                "kitsu" => ExternalSeriesProvider.Kitsu,
                "mangadex" => ExternalSeriesProvider.MangaDex,
                "mangabaka" => ExternalSeriesProvider.MangaBaka,
                "bgm" => ExternalSeriesProvider.Bangumi,
                "mangaupdates" => ExternalSeriesProvider.MangaUpdates,
                "gcd" => ExternalSeriesProvider.GrandComicsDatabase,
                "metron" => ExternalSeriesProvider.Metron,
                _ => (ExternalSeriesProvider)int.MaxValue
            };
            if (parsed > ExternalSeriesProvider.Metron) return (null, string.Empty);
        }
        return (parsed, id);
    }

    /// <summary>
    /// True when an external id is a placeholder rather than a real one. Providers encode
    /// "unknown" inconsistently as 0 (AniList idMal=0, MangaBaka/MangaDex "0"), and chasing
    /// such an edge only produces a doomed detail fetch. Purely-numeric ids must be positive;
    /// non-numeric ids (MangaDex UUIDs, MangaUpdates base36, ...) pass through.
    /// </summary>
    private static bool IsBogusExternalId(string id)
        => long.TryParse(id.Trim(), out long numeric) && numeric <= 0;

    private void AddNode(
        Dictionary<ExternalSeriesProvider, MetaDataLinkEntry> nodes,
        ExternalSeriesProvider provider,
        string externalId,
        string title,
        double confidence,
        SeriesLinkStatus status,
        List<string>? linked = null,
        List<string>? titles = null,
        string? coverUrl = null,
        string? metaData = null)
    {
        nodes[provider] = new MetaDataLinkEntry
        {
            Provider = provider,
            ExternalSeriesId = externalId,
            Title = title,
            Confidence = confidence,
            Status = status,
            LinkedSitesIds = linked ?? [],
            AlternativeTitles = titles ?? [],
            // Carried through from a detail fetch that is already in hand (propagation path);
            // callers that still need to fetch pass null and let ExpandViaDetailAsync fill it.
            CoverUrl = coverUrl,
            MetaData = metaData
        };
    }

    private sealed class OscillatingResult
    {
        public string Title { get; set; } = string.Empty;
        public List<string> Linked { get; set; } = [];
        public List<string> Alt { get; set; } = [];
        public string? CoverUrl { get; set; }
    }

    internal sealed class SiteEdgeComparer : IEqualityComparer<(ExternalSeriesProvider Site, string ExternalId)>
    {
        public bool Equals((ExternalSeriesProvider Site, string ExternalId) x, (ExternalSeriesProvider Site, string ExternalId) y)
            => x.Site == y.Site && string.Equals(x.ExternalId, y.ExternalId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((ExternalSeriesProvider Site, string ExternalId) obj)
            => HashCode.Combine((int)obj.Site, obj.ExternalId.ToLowerInvariant());
    }
}
