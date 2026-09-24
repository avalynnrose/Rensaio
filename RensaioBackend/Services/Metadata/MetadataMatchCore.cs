using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Scrobbling;
using RensaioBackend.Services.Scrobbling.Abstractions;
using Microsoft.Extensions.Logging;

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

    private readonly ExternalSeriesProviderFactory _factory;
    private readonly ILogger<MetadataMatchCore> _logger;

    public MetadataMatchCore(ExternalSeriesProviderFactory factory, ILogger<MetadataMatchCore> logger)
    {
        _factory = factory;
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
    public async Task<MetadataMatchResult> MatchAsync(
        List<string> titleCandidates,
        IReadOnlyList<IExternalSeriesProvider> providers,
        List<MetadataLinkSeed> seeds,
        Dictionary<ExternalSeriesProvider, HashSet<string>> blockedIds,
        HashSet<ExternalSeriesProvider> skippedProviders,
        HashSet<ExternalSeriesProvider>? enabledProviders = null,
        CancellationToken token = default)
    {
        var result = new MetadataMatchResult();

        // Node set: provider -> external ID + confidence + titles + linked IDs.
        var nodes = new Dictionary<ExternalSeriesProvider, MetaDataLinkEntry>();
        // Edge set: canonical "site:id" -> provider entry.
        var edges = new Dictionary<(ExternalSeriesProvider Site, string ExternalId), double>(new SiteEdgeComparer());

        // 1. Seed from existing links (status-aware rules already applied by the caller).
        foreach (var seed in seeds)
        {
            if (string.IsNullOrWhiteSpace(seed.ExternalSeriesId)) continue;
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

        // Bookkeeping to avoid fetching the same provider detail more than once per link pass
        // (e.g. MangaUpdates id reached directly + via two inbound edges).
        var fetchedIds = result.FetchedIds;

        // 2. For each provider, search if not already directly linked; score via TitleMatcher.
        // Run the per-provider searches in parallel; apply results in a single serial pass so the
        // shared nodes/edges graph stays consistent (providers also share the HttpClient factory).
        // Per-provider history of titles already searched this pass (round 1 records the local
        // candidates; round 2 appends every title it tries). Prevents re-searching the same title
        // on the same provider. NOT cleared between round-1 and round-2.
        var searchedTitles = new Dictionary<ExternalSeriesProvider, HashSet<string>>();

        var providerResults = await Task.WhenAll(
            providers
                .Where(p => !skippedProviders.Contains(p.ProviderType))
                .Select(async provider =>
                {
                    var providerType = provider.ProviderType;
                    var existing = nodes.GetValueOrDefault(providerType);
                    if (existing != null)
                    {
                        // Already linked from seed — later expanded via detail fetch (once per id).
                        return (Provider: provider, Best: (MetaDataLinkEntry?)null, IsExisting: true);
                    }

                    try
                    {
                        blockedIds.TryGetValue(providerType, out var idBlocklist);

                        // Round 1 searches the FULL local candidate set. The per-provider
                        // FilterLookupTitles narrows only which titles are SENT (applied inside
                        // SearchAndScoreAsync); scoring still runs against every local candidate.
                        var best = await SearchAndScoreAsync(provider, titleCandidates, token, idBlocklist);

                        // Record every local candidate as "searched" for this provider so round 2
                        // never retests them.
                        if (!searchedTitles.TryGetValue(providerType, out var history))
                        {
                            history = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            searchedTitles[providerType] = history;
                        }
                        foreach (var title in titleCandidates.Where(t => !string.IsNullOrWhiteSpace(t)))
                            history.Add(title);

                        return (Provider: provider, Best: best, IsExisting: false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch
                    {
                        return (Provider: provider, Best: (MetaDataLinkEntry?)null, IsExisting: false);
                    }
                }));

        foreach (var pr in providerResults)
        {
            var provider = pr.Provider;
            var providerType = provider.ProviderType;
            var existing = nodes.GetValueOrDefault(providerType);
            if (existing != null)
            {
                // Only expand (fetch detail for) providers that are enabled — disabled providers'
                // existing links must not trigger metadata fetches.
                if (enabledProviders == null || enabledProviders.Contains(providerType))
                    await ExpandViaDetailAsync(provider, existing, nodes, edges, fetchedIds, token);
                result.ProvidersSearched++;
                continue;
            }

            var best = pr.Best;
            if (best == null) continue;
            result.ProvidersSearched++;

            if (best.Confidence >= AutoThreshold)
            {
                AddNode(nodes, providerType, best.ExternalSeriesId, best.Title ?? string.Empty, best.Confidence, SeriesLinkStatus.Linked);
                edges[(providerType, best.ExternalSeriesId)] = best.Confidence;
                await ExpandViaDetailAsync(provider, nodes[providerType], nodes, edges, fetchedIds, token);
            }
            else if (best.Confidence >= SuggestThreshold)
            {
                result.Suggestions.Add(best);
            }
        }

        // 3. Second pass: using the titles gathered from ALREADY-LINKED providers only, try to
        // fill the still-unmatched providers. FilterLookupTitles is applied per-provider here
        // (and only here). Runs recursively until all titles are tested on the unmatched providers
        // or all providers are matched. Round-1 titles are never retested (per-provider history).
        await SecondPassLinkUnmatchedAsync(providers, nodes, edges, fetchedIds, searchedTitles,
            enabledProviders, skippedProviders, blockedIds, result, token).ConfigureAwait(false);

        // 3.5. Deduplicate explicit provider-node expansions: skip if already fetched via propagation.
        foreach (var node in nodes.Values.Where(n => n.Status == SeriesLinkStatus.Linked))
        {
            fetchedIds.Add((node.Provider, node.ExternalSeriesId));
        }

        // 4. Propagation: follow provider-declared edges to fill gaps (max hops).
        for (int hop = 1; hop <= MaxHops; hop++)
        {
            var frontier = edges.Where(e => e.Value > 0).ToList();
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

                var metadata = await provider.FetchSeriesMetadataAsync(id, token);
                if (metadata != null)
                {
                    // AlternateTitles never contains the primary title (see TitleListBuilder);
                    // score against title + alternates but persist only the alternates.
                    var altTitles = metadata.AlternativeTitles;
                    var scoringTitles = new List<string> { metadata.Title };
                    scoringTitles.AddRange(altTitles);
                    var matchScore = ScoreAgainstLocal(scoringTitles, titleCandidates);
                    var finalConfidence = Math.Max(conf, matchScore);
                    var status = finalConfidence >= AutoThreshold ? SeriesLinkStatus.Linked : SeriesLinkStatus.Suggested;

                    AddNode(nodes, site, metadata.ExternalId, metadata.Title, finalConfidence, status,
                        metadata.LinkedSitesIds, altTitles);

                    if (status == SeriesLinkStatus.Linked)
                        edges[(site, metadata.ExternalId)] = finalConfidence;

                    foreach (var linked in metadata.LinkedSitesIds)
                    {
                        var (lsite, lid) = SplitSiteId(linked);
                        if (lsite.HasValue && !string.IsNullOrWhiteSpace(lid))
                        {
                            edges[(lsite.Value, lid)] = Math.Max(edges.GetValueOrDefault((lsite.Value, lid)), finalConfidence * HopDecay);
                        }
                    }

                    // expand local title pool with the discovered titles for better matching
                    foreach (var t in scoringTitles)
                        titleCandidates.Add(t);
                    titleCandidates = titleCandidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
            }
        }

        result.Links = nodes.Values.Where(n => n.Status == SeriesLinkStatus.Linked).ToList();
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
                        if (score >= AutoThreshold)
                        {
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
        HashSet<string>? blockedIds = null)
    {
        var candidates = new List<(string SearchTitle, string Id)>();
        var lookup = new Dictionary<string, OscillatingResult>(StringComparer.OrdinalIgnoreCase);

        // Only the titles actually sent to the provider are filtered. The local candidate
        // list stays intact for scoring below, so a title this provider's search cannot use
        // (e.g. ComicVine rejects CJK / Bangumi keeps only CJK) still participates in match
        // scoring against the results returned for the provider's usable titles. Providers
        // that do not narrow (the default pass-through) behave exactly as before.
        var searchTitles = provider.FilterLookupTitles(localCandidates)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase);

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
                lookup[r.ExternalId] = new OscillatingResult { Title = r.Title, Linked = r.LinkedSitesIds, Alt = r.AlternateTitles };
                candidates.Add((r.Title, r.ExternalId));
                foreach (var alt in r.AlternateTitles.Where(a => !string.IsNullOrWhiteSpace(a)))
                    candidates.Add((alt, r.ExternalId));
            }
        }

        if (candidates.Count == 0) return null;

        var scored = TitleMatcher.MatchTitles(localCandidates.ToList(), candidates, 0);
        if (scored.Length == 0) return null;

        var best = scored[0];
        var info = lookup.GetValueOrDefault(best.Id);
        return new MetaDataLinkEntry
        {
            Provider = provider.ProviderType,
            ExternalSeriesId = best.Id,
            Title = info?.Title ?? best.SearchTitle,
            Confidence = best.Percentage / 100.0,
            Status = best.Percentage / 100.0 >= AutoThreshold ? SeriesLinkStatus.Linked : SeriesLinkStatus.Suggested,
            LinkedSitesIds = info?.Linked ?? [],
            AlternativeTitles = info?.Alt ?? []
        };
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
            if (metadata == null) return;

            // capture cover + native metadata so the caller can persist them to the row
            node.CoverUrl ??= metadata.CoverUrl;
            node.MetaData = !string.IsNullOrWhiteSpace(metadata.MetaData) ? metadata.MetaData : node.MetaData;

            // enrich the node
            node.Title ??= metadata.Title;
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

    private static (ExternalSeriesProvider? Site, string Id) SplitSiteId(string linked)
    {
        var idx = linked.IndexOf(':');
        if (idx <= 0 || idx == linked.Length - 1)
            return (null, string.Empty);
        var slug = linked[..idx];
        var id = linked[(idx + 1)..];
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

    private void AddNode(
        Dictionary<ExternalSeriesProvider, MetaDataLinkEntry> nodes,
        ExternalSeriesProvider provider,
        string externalId,
        string title,
        double confidence,
        SeriesLinkStatus status,
        List<string>? linked = null,
        List<string>? titles = null)
    {
        nodes[provider] = new MetaDataLinkEntry
        {
            Provider = provider,
            ExternalSeriesId = externalId,
            Title = title,
            Confidence = confidence,
            Status = status,
            LinkedSitesIds = linked ?? [],
            AlternativeTitles = titles ?? []
        };
    }

    private sealed class OscillatingResult
    {
        public string Title { get; set; } = string.Empty;
        public List<string> Linked { get; set; } = [];
        public List<string> Alt { get; set; } = [];
    }

    internal sealed class SiteEdgeComparer : IEqualityComparer<(ExternalSeriesProvider Site, string ExternalId)>
    {
        public bool Equals((ExternalSeriesProvider Site, string ExternalId) x, (ExternalSeriesProvider Site, string ExternalId) y)
            => x.Site == y.Site && string.Equals(x.ExternalId, y.ExternalId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((ExternalSeriesProvider Site, string ExternalId) obj)
            => HashCode.Combine((int)obj.Site, obj.ExternalId.ToLowerInvariant());
    }
}
