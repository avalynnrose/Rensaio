using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Models.Dto;
using RensaioBackend.Services.Contributions;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Metadata;
using RensaioBackend.Services.Rpc;
using RensaioBackend.Services.Scrobbling.Abstractions;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Extensions;
using Rensaio.RPC.Contracts;

namespace RensaioBackend.Services.Background;

/// <summary>Aggregate outcome of a sweep.</summary>
public sealed class SweepResult
{
    public int ExtensionsSwept { get; set; }
    public int SourcesSwept { get; set; }

    /// <summary>
    /// Number of NEW series-entry rows inserted into contributor.db by this sweep. This is the
    /// count of rows that did not previously exist — re-written (already-covered) entries do NOT
    /// count, so a second sweep over the same catalogue reports 0. Excludes metadata rows.
    /// </summary>
    public int SeriesInserted { get; set; }
}

/// <summary>
/// Scans installed Mihon extensions and their catalogue sources and records the
/// series they expose into the local contribution database (<c>contributor.db</c>),
/// matching provider metadata as it goes.
///
/// This is the re-usable "extension scanning / matching" unit: it owns the
/// per-source sweep (network reads via RPC + metadata match + graph write) but
/// **not** the export. Callers that need the contribution export (e.g. the
/// one-shot seed sweep) invoke it from the outside, so the same scan can be
/// driven from other entry points later.
///
/// Metadata matching is gated by the caller-supplied <c>force</c> flag:
///   * <c>force = true</c>  -> every swept series is matched (historical behavior).
///   * <c>force = false</c> -> a series is matched only when it is NEW (its SeriesEntity was
///     created this sweep) OR has a missing mapping for its seriesSource (at least one enabled
///     metadata provider owns no live metadata row for it). A series already covered by every
///     enabled provider is skipped and the sweep continues.
///
/// Per source the flow is incremental, and every page is fully processed before the
/// next one is fetched (never accumulated into one big batch):
///   1. INCREMENTAL fast path (only when a cursor from a previous run exists): page
///      GetLatest (newest-first) until its ids re-overlap the stored baseline; only the
///      pages newer than the last run are processed, then stop.
///   2. FULL pass (first run — no cursor yet — or the fallback when the incremental
///      sweep never re-reached its baseline):
///      a. GetPopular page by page fills the catalogue. For each page the newly-seen
///         series are ADDED/CONSOLIDATED, WRITTEN with SeenInPopular=true /
///         SeenInLatest=false, then MATCHED.
///      b. GetLatest is read for exactly ONE page to seed the cursor (LatestIds +
///         timestamp) for the next run — never paged to exhaustion, so a source that
///         never reports the end of its paging cannot spin.
///   3. The caller exports once, after all sources are done.
/// </summary>
public sealed class ExtensionSweepService
{
    /// <summary>Minimum ids that must re-overlap the previous GetLatest baseline to stop.</summary>
    private const int LatestOverlapMin = 7;
    /// <summary>Tolerated mismatches while matching the baseline before it counts as a match.</summary>
    private const int MaxOverlapMisses = 3;
    /// <summary>
    /// Consecutive incremental GetLatest pages that yield nothing new before the walk gives up and
    /// falls back to the full pass (guards a source that loops the same non-empty page).
    /// </summary>
    private const int IncrementalStallPages = 5;
    /// <summary>
    /// Hard cap on pages walked by the incremental GetLatest pass before it gives up and falls back
    /// to the full pass (guards a source that keeps yielding new items yet never re-reaches the
    /// baseline, e.g. after a large catalogue reshuffle).
    /// </summary>
    private const int IncrementalMaxPages = 25;

    /// <summary>Attempts (initial + retries) for a single catalogue page fetch.</summary>
    private const int PageFetchMaxAttempts = 3;
    /// <summary>Base delay for the per-page retry backoff (doubles each attempt).</summary>
    private static readonly TimeSpan PageFetchBaseDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Two-phase sweep toggle.
    ///
    /// When <c>true</c> (the NEW flow) the sweep runs in two phases:
    ///   * Phase 1 — SCAN ALL: every package/source is scanned and only titles + series are
    ///     add/consolidated and written (NO per-page metadata match). New series are written with
    ///     the derived <see cref="ContributionSeriesEntity.Metadata"/> flag = false.
    ///   * Phase 2 — METADATA PASS: same-title series are joined first, then the metadata retrieval /
    ///     automatch runs ONCE per candidate series group (not once per source entry), looping
    ///     (join → metadata → provider-title augmentation → re-join) to a fixed point. It therefore
    ///     performs far fewer metadata calls than the legacy per-page flow.
    ///
    /// When <c>false</c> the LEGACY per-page flow is used unchanged (add/consolidate → match →
    /// write, per page). Compile-time so the legacy path stays in the assembly for instant rollback
    /// by flipping this constant.
    /// </summary>
    internal const bool UseTwoPhaseSweep = true;

    /// <summary>
    /// Hard cap on the metadata pass's join/metadata fixed-point iterations. The loop normally
    /// converges in 1-2 passes (it stops as soon as no candidate gains metadata); the cap only
    /// guards a pathological graph.
    /// </summary>
    private const int TwoPhaseSweepMaxPasses = 5;

    private readonly IExtensionRpcBridge _rpc;
    private readonly ContributionGraphWriter _writer;
    private readonly ContributionMappingService _mapping;
    private readonly ExternalMappingSupport _providerSupport;
    private readonly MetadataTypeGate _typeGate;
    private readonly ContributionGraphRepairService _repair;
    private readonly ILogger<ExtensionSweepService> _logger;

    // Memoized content-type gate result per package for the CURRENT enabled-provider set. The
    // metadata pass resolves the allowed providers for every candidate series' packages, so
    // without this the gate (and its "N of M provider(s) run" log) was recomputed and re-emitted
    // once per (series, package) — thousands of identical lines per sweep. One sweep run uses a
    // single enabled-provider set, so the cache is keyed by package and reset when that set
    // instance changes.
    private readonly Dictionary<string, HashSet<ExternalSeriesProvider>> _packageProviderCache =
        new(StringComparer.OrdinalIgnoreCase);
    private HashSet<ExternalSeriesProvider>? _packageProviderCacheFor;

    public ExtensionSweepService(
        IExtensionRpcBridge rpc,
        ContributionGraphWriter writer,
        ContributionMappingService mapping,
        ExternalMappingSupport providerSupport,
        MetadataTypeGate typeGate,
        ContributionGraphRepairService repair,
        ILogger<ExtensionSweepService> logger)
    {
        _rpc = rpc;
        _writer = writer;
        _mapping = mapping;
        _providerSupport = providerSupport;
        _typeGate = typeGate;
        _repair = repair;
        _logger = logger;
    }

    /// <summary>
    /// Sweeps every installed extension, optionally starting at
    /// <paramref name="startPackage"/>. Returns the aggregate result.
    ///
    /// <paramref name="force"/> controls metadata matching: when <c>true</c> EVERY swept series is
    /// matched (historical behavior); when <c>false</c> a series is matched only when it is newly
    /// created this sweep OR at least one enabled metadata provider has a missing (absent) row for
    /// it — a series already covered by a live row for every enabled provider is skipped, and the
    /// sweep continues.
    /// </summary>
    public async Task<SweepResult> SweepAllAsync(string? startPackage, bool force, CancellationToken token)
    {
        var result = new SweepResult();

        // Enabled background metadata providers (public + authenticated OAuth). These are the
        // "expected" providers for the gated (non-force) coverage check: providers that can never
        // run cannot be expected to own a row, so they are excluded — otherwise coverage would
        // never be complete and the gate would never skip anything.
        HashSet<ExternalSeriesProvider> enabledProviders;
        try
        {
            enabledProviders = await _providerSupport.GetEnabledProviderSetAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to resolve the enabled provider set; gating disabled (all series matched).");
            enabledProviders = [];
            force = true;
        }

        List<RepositoryGroup> groups;
        try
        {
            groups = await _rpc.ListExtensionsAsync(includeTemporary: false, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to list extensions from RPC; aborting.");
            return result;
        }

        int startIndex = 0;
        if (!string.IsNullOrWhiteSpace(startPackage))
        {
            startIndex = groups.FindIndex(g => g.Entries.Any(e =>
                string.Equals(e.Extension?.Package, startPackage, StringComparison.OrdinalIgnoreCase)));

            if (startIndex < 0)
            {
                _logger.LogError(
                    "Start package '{Package}' was not found among installed extensions; aborting.",
                    startPackage);
                return result;
            }
        }

        for (int i = startIndex; i < groups.Count && !token.IsCancellationRequested; i++)
        {
            RepositoryGroup group = groups[i];
            RepositoryEntry? active = group.Entries.ElementAtOrDefault(group.ActiveEntry)
                                      ?? group.Entries.FirstOrDefault();
            string? package = active?.Extension?.Package;
            if (string.IsNullOrWhiteSpace(package))
                continue;

            // Content-type gate: a package whose content is only usercreatedcontent/doujinshi is
            // skipped ENTIRELY from the scan — no sources are listed or swept and no metadata
            // provider runs.
            if (_typeGate.ShouldSkipAll(package))
            {
                _logger.LogInformation(
                    "Content-type gate: skipping extension {Package} entirely (content: {Content}).",
                    package, string.Join(",", _typeGate.GetContentTypes(package)));
                continue;
            }

            _logger.LogInformation("Started with extension {Name} ({Package}).",
                active?.Extension?.Name ?? group.Name, package);

            IReadOnlyList<SourceDescriptor> sources;
            try
            {
                sources = await _rpc.ListSourceDescriptorsAsync(package, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to list sources for {Package}; skipping extension.", package);
                continue;
            }

            result.ExtensionsSwept++;

            // Content-type gate: restrict the enabled providers to those that support the content
            // types mapped to this package (TypeMappings). A package whose content is only
            // usercreatedcontent/doujinshi skips metadata matching entirely.
            HashSet<ExternalSeriesProvider> packageProviders =
                ResolveEnabledProvidersForPackage(package, enabledProviders);

            foreach (SourceDescriptor source in sources)
            {
                if (token.IsCancellationRequested) break;
                result.SourcesSwept++;

                int swept = await SweepSourceAsync(package, source, force, packageProviders, token).ConfigureAwait(false);
                result.SeriesInserted += swept;
            }
        }

        // ── Post-scan phase ──
        // Two-phase flow: join same-title series FIRST, then run the metadata pass once per
        // candidate group (far fewer metadata calls than the legacy per-page match). Legacy flow:
        // just the auto-merge/un-merge reconcile, exactly as before.
        if (UseTwoPhaseSweep)
        {
            await RunMetadataPassAsync(enabledProviders, token).ConfigureAwait(false);
        }
        else
        {
            // Auto-merge (or un-merge) series that share provider identity now that the sweep is done.
            await _mapping.ReconcileSeriesGroupingAsync(token).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Sweeps an explicit set of packages, whether or not they are installed. An installed package
    /// is swept through the existing local path; an online-only package is prepared transiently,
    /// swept, then released. Serial number of extension/source/series counters are aggregated.
    /// </summary>
    public async Task<SweepResult> SweepPackagesAsync(
        IReadOnlyList<string> packages, bool force, CancellationToken token)
    {
        var result = new SweepResult();

        HashSet<ExternalSeriesProvider> enabledProviders;
        try
        {
            enabledProviders = await _providerSupport.GetEnabledProviderSetAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to resolve the enabled provider set; gating disabled (all series matched).");
            enabledProviders = [];
            force = true;
        }

        // Temporarily prepared (online-only) packages are kept alive until AFTER the post-scan
        // metadata pass: the pass resolves source urls through the extension RPC (MangaUrlResolver),
        // so releasing them at the end of each package's sweep would break its scan.
        var preparedTemporary = new List<string>();
        try
        {
            foreach (string package in packages)
            {
                if (token.IsCancellationRequested)
                    break;
                if (string.IsNullOrWhiteSpace(package))
                    continue;

                bool preparedHere = false;
                try
                {
                    // Ensure available: installed packages resolve as-is; online-only ones are
                    // prepared transiently (Temporary=true) and released after the post-scan phase.
                    bool installed = await IsInstalledAsync(package, token).ConfigureAwait(false);
                    if (!installed)
                    {
                        OnlineExtensionDescriptor? descriptor = await _rpc
                            .PrepareOnlineExtensionAsync(package, token).ConfigureAwait(false);
                        if (descriptor == null)
                        {
                            _logger.LogWarning("Package {Package} is neither installed nor available online; skipping.", package);
                            continue;
                        }
                        preparedHere = descriptor.Temporary;
                    }

                    await SweepOnePackageAsync(package, force, enabledProviders, result, token).ConfigureAwait(false);

                    if (preparedHere)
                        preparedTemporary.Add(package);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Package {Package} sweep failed; continuing.", package);
                    if (preparedHere)
                        preparedTemporary.Add(package);
                }
            }

            if (UseTwoPhaseSweep)
            {
                await RunMetadataPassAsync(enabledProviders, token).ConfigureAwait(false);
            }
            else
            {
                await _mapping.ReconcileSeriesGroupingAsync(token).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (string package in preparedTemporary)
            {
                try { await _rpc.ReleaseOnlineExtensionAsync(package, token).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to release temporary package {Package}.", package); }
            }
        }

        return result;
    }

    /// <summary>
    /// Sweeps every extension exposed by the configured online repositories. Installed packages
    /// use the local path; all others are prepared transiently and released after their sweep.
    ///
    /// When <paramref name="startPackage"/> is supplied the sweep begins at that online package
    /// (mirroring <see cref="SweepAllAsync"/>'s start semantics) and continues to the end of the
    /// online list. A start package that is not among the online packages aborts the sweep.
    /// </summary>
    public async Task<SweepResult> SweepAllOnlineAsync(string? startPackage, bool force, CancellationToken token)
    {
        List<TachiyomiRepository> repositories;
        try
        {
            repositories = await _rpc.ListOnlineRepositoriesAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to list online repositories; aborting sweep-all.");
            return new SweepResult();
        }

        List<string> packages = repositories
            .SelectMany(r => r.Extensions)
            .Select(e => e.Package)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(startPackage))
        {
            int startIndex = packages.FindIndex(p =>
                string.Equals(p, startPackage, StringComparison.OrdinalIgnoreCase));

            if (startIndex < 0)
            {
                _logger.LogError(
                    "Start package '{Package}' was not found among online extensions; aborting.",
                    startPackage);
                return new SweepResult();
            }

            if (startIndex > 0)
                packages = packages.Skip(startIndex).ToList();
        }

        _logger.LogInformation("Sweep-all: {Count} online package(s) to sweep.", packages.Count);
        return await SweepPackagesAsync(packages, force, token).ConfigureAwait(false);
    }

    /// <summary>Sweeps one package's sources and accumulates the counters into <paramref name="result"/>.</summary>
    private async Task SweepOnePackageAsync(
        string package, bool force, HashSet<ExternalSeriesProvider> enabledProviders,
        SweepResult result, CancellationToken token)
    {
        // Content-type gate: skip the whole package when its content is only
        // usercreatedcontent/doujinshi — no sources listed/swept, no metadata provider runs.
        if (_typeGate.ShouldSkipAll(package))
        {
            _logger.LogInformation(
                "Content-type gate: skipping extension {Package} entirely (content: {Content}).",
                package, string.Join(",", _typeGate.GetContentTypes(package)));
            return;
        }

        IReadOnlyList<SourceDescriptor> sources;
        try
        {
            sources = await _rpc.ListSourceDescriptorsAsync(package, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to list sources for {Package}; skipping extension.", package);
            return;
        }

        _logger.LogInformation("Sweeping extension {Package} ({Count} source(s)).", package, sources.Count);
        result.ExtensionsSwept++;

        // Content-type gate: restrict the enabled providers to those supporting this package's
        // content types before matching its sources.
        HashSet<ExternalSeriesProvider> packageProviders =
            ResolveEnabledProvidersForPackage(package, enabledProviders);

        foreach (SourceDescriptor source in sources)
        {
            if (token.IsCancellationRequested)
                break;
            result.SourcesSwept++;
            int swept = await SweepSourceAsync(package, source, force, packageProviders, token).ConfigureAwait(false);
            result.SeriesInserted += swept;
        }
    }

    /// <summary>
    /// Narrows the enabled-provider set to those allowed for <paramref name="package"/> per the
    /// root <c>TypeMappings</c> content types (see <see cref="MetadataTypeGate"/>). A package whose
    /// content is only <c>usercreatedcontent</c>/<c>doujinshi</c> yields an EMPTY set, which makes
    /// the sweep record the series without running any metadata provider.
    /// </summary>
    private HashSet<ExternalSeriesProvider> ResolveEnabledProvidersForPackage(
        string package, HashSet<ExternalSeriesProvider> enabledProviders)
    {
        if (enabledProviders.Count == 0)
            return enabledProviders;

        // Recompute the memo when the enabled-provider set instance changes (one sweep run uses
        // one set). The metadata pass calls this per candidate series, so caching collapses the
        // repeated gate evaluation AND its log line down to once per package.
        if (!ReferenceEquals(_packageProviderCacheFor, enabledProviders))
        {
            _packageProviderCache.Clear();
            _packageProviderCacheFor = enabledProviders;
        }
        if (_packageProviderCache.TryGetValue(package, out var cached))
            return cached;

        var allowed = _typeGate.FilterProviders(package, _providerSupport.GetMetadataProvidersInOrder())
            .Select(p => p.ProviderType)
            .ToHashSet();

        var filtered = enabledProviders.Where(allowed.Contains).ToHashSet();
        if (filtered.Count != enabledProviders.Count)
        {
            _logger.LogInformation(
                "Content-type gate for {Package} ({Content}): {Kept} of {Total} provider(s) run.",
                package, string.Join(",", _typeGate.GetContentTypes(package)),
                filtered.Count, enabledProviders.Count);
        }
        _packageProviderCache[package] = filtered;
        return filtered;
    }

    /// <summary>True when the package is among the installed (non-temporary) extensions.</summary>
    private async Task<bool> IsInstalledAsync(string package, CancellationToken token)
    {
        try
        {
            List<RepositoryGroup> installed = await _rpc.ListExtensionsAsync(includeTemporary: false, token).ConfigureAwait(false);
            return installed.Any(g => g.Entries.Any(e =>
                string.Equals(e.Extension?.Package, package, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "IsInstalled check failed for {Package}; treating as not installed.", package);
            return false;
        }
    }

    /// <summary>
    /// Sweeps one source and returns the number of series recorded.
    ///
    /// Each page is processed independently and immediately (PER PAGE), never accumulated
    /// into one big batch:
    ///   1) ADD / CONSOLIDATE — the page's new series are resolved against the title/series
    ///      graph: brand-new titles/series are created, existing ones are merged onto.
    ///   2) MATCH — the mappings that own this page's titles are scanned so only providers
    ///      with pending work (never matched, or with a matching gap) are searched.
    ///   3) WRITE — the collected series records + the scan's metadata rows are persisted.
    /// The GetLatest tail follows the same per-page flow; finally the caller (SeedService)
    /// exports once after every source has been swept.
    /// </summary>
    public async Task<int> SweepSourceAsync(
        string package, SourceDescriptor source, bool force,
        HashSet<ExternalSeriesProvider> enabledProviders, CancellationToken token)
    {
        string label = $"{source.Name} ({source.Language})";
        try
        {
            ISourceInterop interop = await _rpc.GetSourceInteropAsync(package, source.SourceId, token).ConfigureAwait(false);

            Guid sourceEntityId = ContributionSourceEntity.DeriveId(package, source.SourceId);
            string sourceName = string.IsNullOrWhiteSpace(source.Name) ? "Unknown" : source.Name;
            string sourceLanguage = source.Language ?? string.Empty;

            // BATCH PERFORMANCE: this source is paged against ONE scoped ContributionDbContext.
            // Auto change detection (default on) re-scans every tracked entity before each of the
            // many writer queries/saves per page, so per-page cost grows with the pages already
            // processed (the "later pages take ~1 min" symptom). Suppress it for the source pass;
            // SaveChanges forces a single DetectChanges and the writer sets IsModified explicitly.
            using var autoDetect = _writer.SuppressAutoDetectChanges();

            // Baseline cursor from the previous run — drives the incremental GetLatest heuristic.
            ContributionSourceEntity? baseline = await _writer.GetSourceAsync(package, source.SourceId, token).ConfigureAwait(false);
            await _writer.EnsureSourceAsync(
                package, source.SourceId, sourceName, sourceLanguage,
                baseline?.LastBatchExecutionUTC, baseline?.LatestIds, token).ConfigureAwait(false);

            // Series already written by this source pass — never re-written/re-matched, and
            // used to detect popular→latest duplicates without re-matching them.
            var swept = new Dictionary<string, SweptSeries>(StringComparer.Ordinal);
            int written = 0;
            int matched = 0;
            var latestIds = new List<string>();

            bool hasBaseline = source.SupportsLatest
                && baseline?.LastBatchExecutionUTC is not null
                && baseline.LatestIds.Count > 0;

            // ── GetLatest incremental fast path ──
            // Taken ONLY when a cursor from a previous run exists. Pages newest-first until the ids
            // re-overlap the stored baseline; everything newer than the baseline is processed as it
            // goes (SeenInLatest). On the FIRST run there is no baseline, so GetLatest is skipped
            // here entirely and the full pass below builds the catalogue before seeding the cursor.
            bool incrementalDone = false;
            if (source.SupportsLatest && hasBaseline)
            {
                var incremental = await RunLatestPagesAsync(
                    interop, package, source, sourceEntityId, swept, latestIds, baseline!.LatestIds,
                    stopOnOverlap: true, force, enabledProviders, token).ConfigureAwait(false);
                written += incremental.Written;
                matched += incremental.Matched;
                incrementalDone = incremental.Stop;

                if (!incrementalDone)
                {
                    _logger.LogWarning(
                        "[{Source}] incremental GetLatest did not reach the previous baseline after {Pages} page(s); falling back to a full GetPopular pass.",
                        label, latestIds.Count);
                }
            }

            // ── Full pass: GetPopular (all pages) then ONE GetLatest page to seed the cursor. Used
            //    on the first run (no baseline) and as the fallback when an incremental sweep failed
            //    to re-reach its baseline. ──
            if (!incrementalDone)
            {
                // ── 1. GetPopular: per-page loop (add+consolidate → match → write) ──
                int page = 1;
                int repCnt = 0;
                // Consecutive pages that were non-empty but contained nothing NEW. A source that
                // ignores/loops the page number would otherwise spin forever: the empty-list guard
                // above never fires (the page is not empty) and HasNextPage stays true.
                int noFreshPages = 0;
                while (true)
                {
                    MangaList list = await FetchPageWithRetryAsync(
                        label, $"popular {page}", ct => interop.GetPopularAsync(page, ct), token).ConfigureAwait(false);
                    if (list.Mangas.Count == 0)
                    {
                        repCnt++;
                        if (repCnt > 5)
                        {
                            _logger.LogWarning("[{Source}] GetPopular returned empty list on page {Page} 5 times; aborting.", label, page);
                            break;
                        }
                        _logger.LogWarning("[{Source}] GetPopular returned empty list on page {Page}.", label, page);
                        await Task.Delay(30000, token).ConfigureAwait(false);
                        continue;
                    }
                    repCnt = 0;
                    List<ParsedManga> fresh = CollectNew(list.Mangas, swept);
                    int pageInserted = 0;
                    int pageWritten = 0;
                    if (fresh.Count > 0)
                    {
                        (int w, int m) = await ProcessPageAsync(
                            package, source, sourceEntityId, fresh, swept, seenInLatest: false, force, enabledProviders, token).ConfigureAwait(false);
                        written += w;
                        matched += m;
                        pageInserted = w;
                        pageWritten = fresh.Count;
                        noFreshPages = 0;
                    }
                    else
                    {
                        noFreshPages++;
                    }

                    // {Fresh} is measured against THIS source pass (entries not already handled in
                    // this run), NOT against the database; {Inserted} is the true "row created in
                    // contributor.db" count. A re-swept, already-covered page reports Fresh>0 but
                    // Inserted=0 — the entries still get their records refreshed, they are just not
                    // new series.
                    _logger.LogInformation(
                        "[{Source}] popular page {Page}: {Items} item(s), {Fresh} new this pass, {Inserted} inserted, {Updated} record(s) written (hasNext={HasNext}).",
                        label, page, list.Mangas.Count, fresh.Count, pageInserted, pageWritten, list.HasNextPage);

                    // Stall guard: too many consecutive pages with nothing new means the source is
                    // looping/repeating — stop instead of spinning forever.
                    if (noFreshPages >= 10)
                    {
                        _logger.LogWarning("[{Source}] {Count} consecutive pages with no new series at page {Page}; assuming a paging loop, aborting popular pass.",
                            label, noFreshPages, page);
                        break;
                    }

                    if (!list.HasNextPage)
                        break;
                    page++;
                }

                _logger.LogInformation("[{Source}] Popular pass inserted {Count} new series entry(ies).", label, written);

                // ── 2. GetLatest anchor (only when supported): a SINGLE page seeds the cursor
                //    (LatestIds + timestamp) so the next run can take the incremental fast path.
                //    Series already written from popular are flipped to SeenInLatest; anything the
                //    newest page adds that popular missed is recorded too. One page is all the
                //    overlap heuristic needs, so a source that never reports the end of its paging
                //    cannot spin. ──
                if (source.SupportsLatest)
                {
                    _logger.LogInformation("[{Source}] Source supports GetLatest — recording one page as the baseline.", label);

                    // Single page → its ids become the cursor (recorded inside the helper).
                    var anchor = await RunLatestPagesAsync(
                        interop, package, source, sourceEntityId, swept, latestIds, [],
                        stopOnOverlap: false, force, enabledProviders, token, maxPages: 1).ConfigureAwait(false);
                    written += anchor.Written;
                    matched += anchor.Matched;
                }
            }

            // Persist the sweep cursor: the ordered GetLatest ids + the run timestamp, so the next
            // sweep can run the incremental heuristic.
            if (source.SupportsLatest && latestIds.Count > 0)
            {
                await _writer.EnsureSourceAsync(
                    package, source.SourceId, sourceName, sourceLanguage,
                    DateTime.UtcNow, latestIds, token).ConfigureAwait(false);
                await _writer.SaveAsync(token).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "[{Source}] Done — {Inserted} new series entry(ies) inserted, {Matched} mappings matched.",
                label, written, matched);
            return written;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Source}] sweep failed; continuing.", label);
            return 0;
        }
    }

    /// <summary>
    /// Pages GetLatest (newest-first) for one source, processing each page's new series with the
    /// same add+consolidate → match → write flow (SeenInLatest=true). Already-swept series that
    /// reappear are simply flipped to SeenInLatest=true without re-matching.
    ///
    /// The ordered, duplicate-free ids of the FIRST (newest) page are appended to
    /// <paramref name="ids"/> — the stored cursor is ALWAYS page 1. Later pages are traversed only
    /// to process their own new series and to reach the baseline, and are NOT recorded.
    ///
    /// The baseline is looked for across the WHOLE run: GetLatest is newest-first, so the previous
    /// baseline reappears shifted down by whatever was published since, and an id already recorded
    /// on an earlier page is removed from later pages. The overlap is therefore tested against the
    /// ordered ids accumulated across every page (see <see cref="CountBaselineMatches"/>, which
    /// leaves the leading new items free and tolerates a few gaps). When
    /// <paramref name="stopOnOverlap"/> is true the loop pages until the accumulated ids re-overlap
    /// <paramref name="baseline"/>, the page is empty, a stall guard trips, or
    /// <see cref="IncrementalMaxPages"/> is reached — it does NOT trust <c>HasNextPage</c> alone
    /// (many sources omit it on <c>latestUpdates</c>). The anchor pass
    /// (<paramref name="stopOnOverlap"/> false) stops when the source reports no next page or stops
    /// yielding new items. <paramref name="maxPages"/> caps the walk (the single-page anchor pass
    /// uses 1).
    ///
    /// Returns a tuple whose <c>Stop</c> member is <c>true</c> when the loop stopped because the
    /// baseline was reached — the signal for the caller to keep the cursor instead of falling back
    /// to a full popular pass. When <paramref name="stopOnOverlap"/> is false <c>Stop</c> only
    /// reports whether at least one page was read (so the caller knows the cursor is worth
    /// persisting). <c>Written</c>/<c>Matched</c> are the per-page processing totals.
    /// </summary>
    private async Task<(bool Stop, int Written, int Matched)> RunLatestPagesAsync(
        ISourceInterop interop, string package, SourceDescriptor source, Guid sourceEntityId,
        Dictionary<string, SweptSeries> swept, List<string> ids, IReadOnlyList<string> baseline,
        bool stopOnOverlap, bool force, HashSet<ExternalSeriesProvider> enabledProviders,
        CancellationToken token, int? maxPages = null)
    {
        // Series already collected during THIS GetLatest sweep. It persists across every page of
        // the loop so a series that GetLatest republishes several times before the baseline is
        // re-reached is processed/matched exactly once.
        var processedThisLatest = new HashSet<string>(StringComparer.Ordinal);
        // Ids already recorded in the ordered cursor during THIS sweep. GetLatest is newest-first
        // but repeats an entry whenever it changes, so an id must be recorded only at its FIRST
        // (newest) occurrence — otherwise the stored baseline fills with duplicates and the
        // positional overlap match desyncs.
        var recordedIds = new HashSet<string>(StringComparer.Ordinal);
        // A legacy stored baseline may itself contain duplicate ids (written before ids were
        // deduped); compare against a duplicate-free copy so the positional match stays aligned.
        // Distinct keeps first occurrence, i.e. the newest position — same rule as this sweep.
        List<string> storedBaseline = baseline.Count == 0
            ? []
            : baseline.Distinct(StringComparer.Ordinal).ToList();
        int threshold = LatestOverlapMin;
        int latestPage = 1;
        bool readAny = false;
        int written = 0;
        int matched = 0;

        // Ordered, duplicate-free ids accumulated across EVERY page of THIS run, newest-first. The
        // baseline is looked for across the whole run: page one's stored items reappear shifted down
        // by whatever was published since, and an id already recorded (deduped) is removed from
        // later pages — so the overlap MUST be tested against the accumulated run, never the
        // per-page slice. Only page 1's ids are persisted as the cursor (via <paramref name="ids"/>).
        var accumulatedIds = new List<string>();
        // Consecutive pages that yielded nothing new to process. Bounds the incremental walk on a
        // source that loops the same (non-empty) page instead of reporting no next page.
        int noFreshLatestPages = 0;

        string label = $"{source.Name} ({source.Language})";
        while (true)
        {
            MangaList list = await FetchPageWithRetryAsync(
                label, $"latest {latestPage}", ct => interop.GetLatestAsync(latestPage, ct), token).ConfigureAwait(false);
            if (list?.Mangas == null || list.Mangas.Count == 0)
                break;

            readAny = true;
            if (latestPage == 1)
                threshold = Math.Min(LatestOverlapMin, list.Mangas.Count);

            List<string> pageIds = list.Mangas
                .Select(m => m.Url)
                .Where(u => !string.IsNullOrEmpty(u))
                .Select(u => ContributionSeriesEntity.DeriveId(package, u).ToString())
                .ToList();

            // Ordered, duplicate-free slice of this page, newest-first: keep each id only at its
            // first occurrence, then extend the run-wide accumulation.
            List<string> freshIds = pageIds.Where(recordedIds.Add).ToList();
            accumulatedIds.AddRange(freshIds);

            // The stored cursor is ALWAYS page 1 (the first items of GetLatest): only the first
            // page's ids are recorded. Later pages are traversed purely to process their new
            // series and to reach the baseline — their ids must not grow the cursor.
            if (latestPage == 1)
                ids.AddRange(freshIds);

            // New series this page (not seen on Popular nor earlier in this GetLatest sweep) get
            // the full flow exactly once.
            List<ParsedManga> newSeries = CollectNew(list.Mangas, swept, processedThisLatest);

            // Already-written popular series that reappear here are updates only: flip
            // SeenInLatest=true without re-matching (they were matched on the Popular pass and any
            // still-pending provider is re-evaluated on the next full scan).
            List<string> duplicates = list.Mangas
                .Select(m => m.Url)
                .Where(k => !string.IsNullOrEmpty(k) && swept.ContainsKey(k))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            bool addedAny = false;
            if (newSeries.Count > 0)
            {
                (int w, int m) = await ProcessPageAsync(
                    package, source, sourceEntityId, newSeries, swept, seenInLatest: true, force, enabledProviders, token).ConfigureAwait(false);
                written += w;
                matched += m;
                addedAny = true;
            }

            if (duplicates.Count > 0)
                await FlagLatestAsync(package, source, sourceEntityId, duplicates, swept, token).ConfigureAwait(false);

            // Match against the ids accumulated across the WHOLE run, not this page alone. The
            // leading new items are free, so a busy source whose baseline scrolled far down still
            // re-matches as soon as enough of the old ids reappear in order.
            if (stopOnOverlap)
            {
                int reMatched = CountBaselineMatches(accumulatedIds, storedBaseline, MaxOverlapMisses);
                if (latestPage == 1)
                {
                    _logger.LogDebug(
                        "[{Source}] incremental GetLatest page 1: {Seen} id(s) seen, {Matched}/{Baseline} baseline id(s) re-matched (threshold {Threshold}).",
                        label, accumulatedIds.Count, reMatched, storedBaseline.Count, threshold);
                }
                if (reMatched >= threshold)
                    return (true, written, matched);
            }

            if (maxPages is not null && latestPage >= maxPages.Value)
                break;

            if (!addedAny && duplicates.Count == 0)
            {
                noFreshLatestPages++;
                if (noFreshLatestPages >= IncrementalStallPages)
                {
                    _logger.LogWarning(
                        "[{Source}] incremental GetLatest: {Count} consecutive pages with nothing new at page {Page}; assuming a paging loop, stopping.",
                        label, noFreshLatestPages, latestPage);
                    break;
                }
            }
            else
            {
                noFreshLatestPages = 0;
            }

            // Incremental mode pages until the baseline is found, the page is empty, a stall trips,
            // or the hard cap is hit: do NOT trust HasNextPage alone (many sources omit it on
            // latestUpdates). The anchor pass stops when the source reports no next page.
            if (stopOnOverlap)
            {
                if (latestPage >= IncrementalMaxPages)
                {
                    _logger.LogWarning(
                        "[{Source}] incremental GetLatest: reached the {Cap}-page cap without re-matching the baseline; falling back to a full GetPopular pass.",
                        label, IncrementalMaxPages);
                    break;
                }
            }
            else if (!list.HasNextPage)
            {
                break;
            }

            latestPage++;
        }

        return (readAny && !stopOnOverlap, written, matched);
    }

    /// <summary>
    /// Processes a single page's new series in order: ADD/CONSOLIDATE the titles+series
    /// (creating new rows or merging onto existing ones), WRITE them, then MATCH the resulting
    /// mappings (pending providers only) and persist the metadata rows. The write MUST precede
    /// the match because the match resolves mapping ids through a database query, which cannot
    /// see rows that are only tracked. Returns the number of series written and the number of
    /// mappings matched.
    /// </summary>
    private async Task<(int Written, int Matched)> ProcessPageAsync(
        string package, SourceDescriptor source, Guid sourceEntityId,
        List<ParsedManga> fresh, Dictionary<string, SweptSeries> swept, bool seenInLatest,
        bool force, HashSet<ExternalSeriesProvider> enabledProviders, CancellationToken token)
    {
        // 1) ADD / CONSOLIDATE: resolve titles + series (creates new rows, merges existing).
        //    Nothing is saved yet, but ResolveTitlesAsync/ResolveSeriesAsync consult the change
        //    tracker, so the mappings this page just created are visible to the matcher below.
        Dictionary<string, Guid> titleIds = await _writer
            .ResolveTitlesAsync(fresh.Select(m => m.Title ?? string.Empty), token)
            .ConfigureAwait(false);

        var seriesByUrl = new Dictionary<string, (Guid SeriesId, ContributionRecordV2 Record)>(StringComparer.Ordinal);
        // Series ids for entries whose SeriesEntity was created by ResolveSeriesAsync during THIS
        // page (i.e. genuinely new series). Used by the gated (non-force) match.
        var newSeriesIds = new HashSet<Guid>();
        foreach (ParsedManga manga in fresh)
        {
            string url = manga.Url;
            if (string.IsNullOrWhiteSpace(manga.Title) || string.IsNullOrEmpty(url))
                continue;

            var seriesTitles = new Dictionary<string, Guid>(StringComparer.Ordinal)
            {
                [TitleEntity.Normalize(manga.Title)] = titleIds[TitleEntity.Normalize(manga.Title)]
            };
            (Guid seriesId, bool seriesCreated) = await _writer.ResolveSeriesAsync(seriesTitles, manga.Title, token).ConfigureAwait(false);
            await _writer.SyncSeriesTitlesAsync(seriesId, seriesTitles.Values, token).ConfigureAwait(false);
            if (seriesCreated)
                newSeriesIds.Add(seriesId);

            seriesByUrl[url] = (seriesId, new ContributionRecordV2
            {
                SchemaVersion = ContributionRecordV2.CurrentSchemaVersion,
                Url = url,
                TitleId = titleIds[TitleEntity.Normalize(manga.Title)],
                ThumbnailUrl = manga.ThumbnailUrl,
                Status = (int)manga.Status,
                SeenInPopular = !seenInLatest,
                SeenInLatest = seenInLatest,
                LastUpdateUTC = DateTime.UtcNow
            });
        }

        if (seriesByUrl.Count == 0)
            return (0, 0);

        // 2) WRITE: persist the page's series records + title associations FIRST.
        //    The match below reads the mapping's pending providers (and its title candidates)
        //    through DATABASE queries that cannot see rows only tracked in the change tracker.
        //    Saving here is what makes the page's OWN new series/titles visible to its match
        //    pass — otherwise the matcher finds nothing and no Metadata row is ever written.
        // Count how many of the page's entries were genuinely NEW rows (inserted) versus existing
        // rows re-written. `seriesByUrl.Count` is NOT that number: UpsertSeriesRecordAsync rewrites
        // (and reports) an already-covered series too, so a re-swept popular page would otherwise
        // look like it created everything from scratch. The insert count is the honest signal.
        int inserted = 0;
        foreach (var (url, entry) in seriesByUrl)
        {
            bool isNew = await _writer.UpsertSeriesRecordAsync(
                entry.SeriesId, sourceEntityId, package, url, entry.Record, token).ConfigureAwait(false);
            if (isNew)
                inserted++;
            swept[url] = new SweptSeries(entry.SeriesId, entry.Record);
        }
        await _writer.SaveAsync(token).ConfigureAwait(false);

        // 3) MATCH: the page's rows now exist in the DB (saved above), so the matcher can resolve
        //    the series' pending providers through its own DB reads. The page's mapping ids are
        //    already known here (ResolveSeriesAsync), so no title→mapping query is needed.
        //
        //    GATE (non-force): match only a series that is NEW (created this page) OR has a missing
        //    mapping for its seriesSource — i.e. at least one enabled provider owns no live metadata
        //    row for it. A series already covered by every enabled provider (even with Unmatched
        //    placeholder rows) is skipped; the sweep continues. With --force every series is matched.
        List<Guid> pageMappingIds = seriesByUrl.Values
            .Select(v => v.SeriesId)
            .Distinct()
            .ToList();

        // ── Two-phase flow: NO in-page metadata match. ──
        // Every entry written above carries the derived Metadata flag = false (new rows default
        // false; existing rows keep their value). The sweep's post-scan metadata pass joins
        // same-title series FIRST, then runs the metadata retrieval ONCE per candidate group —
        // which is the whole point (far fewer metadata calls than one search per source entry).
        // The page's series records were already persisted at step 2, so just bound the tracker
        // and return.
        if (UseTwoPhaseSweep)
        {
            _writer.DetachAll();
            return (inserted, 0);
        }

        List<Guid> mappingIdsToScan = pageMappingIds;
        if (!force)
        {
            List<Guid> existingIdsToCheck = pageMappingIds.Where(id => !newSeriesIds.Contains(id)).ToList();
            HashSet<Guid> uncovered = existingIdsToCheck.Count > 0 && enabledProviders.Count > 0
                ? await _mapping.FindMappingIdsMissingProviderCoverageAsync(existingIdsToCheck, enabledProviders, token)
                    .ConfigureAwait(false)
                : [];

            var toScan = new HashSet<Guid>(newSeriesIds);
            toScan.UnionWith(uncovered);
            mappingIdsToScan = pageMappingIds.Where(toScan.Contains).ToList();

            int skipped = pageMappingIds.Count - mappingIdsToScan.Count;
            if (skipped > 0)
            {
                _logger.LogInformation(
                    "Gated match — {New} new, {Uncovered} missing provider coverage, {Skipped} skipped (already covered).",
                    newSeriesIds.Count, uncovered.Count, skipped);
            }
        }

        int matched = await MatchMappingsAsync(mappingIdsToScan, enabledProviders, token).ConfigureAwait(false);

        // 4) WRITE: persist the metadata rows the matcher added.
        await _writer.SaveAsync(token).ConfigureAwait(false);

        // Bound the change tracker to THIS page's rows. The whole source is swept against one
        // scoped context; without detaching, every row ever written stays tracked and each later
        // page's queries/saves scan an ever-growing identity map (see ExtensionSweepService).
        _writer.DetachAll();

        return (inserted, matched);
    }

    /// <summary>
    /// Fetches one catalogue page, retrying transient failures with a small exponential backoff so
    /// a single blip on one page does not abandon the whole source. Transient = a transport failure
    /// (an <see cref="HttpRequestException"/>, including a connect/DNS failure with no HTTP status)
    /// or an HTTP 408/429/5xx response. Non-transient failures, caller cancellation and the final
    /// attempt propagate to the caller (which logs and continues with the next source).
    /// </summary>
    private async Task<MangaList> FetchPageWithRetryAsync(
        string label, string what, Func<CancellationToken, Task<MangaList>> fetch, CancellationToken token)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await fetch(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < PageFetchMaxAttempts && IsTransientFetch(ex))
            {
                TimeSpan delay = TimeSpan.FromTicks(PageFetchBaseDelay.Ticks * (1L << (attempt - 1)));
                _logger.LogWarning(ex,
                    "[{Source}] {What} page fetch failed (attempt {Attempt}/{Max}); retrying in {Delay}s.",
                    label, what, attempt, PageFetchMaxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A transient catalogue failure worth retrying: any transport-level
    /// <see cref="HttpRequestException"/> (connect/DNS/timeout — frequently with no HTTP status at
    /// all), an HTTP 408/429/5xx response, or the Android-compat WebView wait timeout (the
    /// challenge page is often still initialising and the same fetch succeeds on a retry).
    /// </summary>
    private static bool IsTransientFetch(Exception ex)
    {
        // The WebView wait timeout arrives as a reconstructed RemoteSourceException (remote
        // java.lang.Exception) rather than an HttpRequestException, so it is classified first.
        if (SourceTransientErrors.IsWebViewTimeout(ex))
            return true;

        if (ex is not HttpRequestException http)
            return false;

        int? status = (int?)http.StatusCode;
        return status is null || status == 408 || status == 429 || status >= 500;
    }

    /// <summary>Filters a page down to the series not yet processed in this source pass.</summary>
    private static List<ParsedManga> CollectNew(IEnumerable<ParsedManga> mangas, Dictionary<string, SweptSeries> swept)
    {
        var fresh = new List<ParsedManga>();
        foreach (ParsedManga m in mangas)
        {
            string key = SeriesKey(m);
            if (!string.IsNullOrEmpty(key) && !swept.ContainsKey(key))
                fresh.Add(m);
        }
        return fresh;
    }

    /// <summary>
    /// Variant for the GetLatest sweep: keeps only entries not already swept and not already
    /// collected earlier in the SAME run (<paramref name="processedThisLatest"/>). GetLatest
    /// republishes a series each time it changes, so the same entry can recur on several pages
    /// before the baseline is re-reached — this set guarantees it is matched only once.
    /// </summary>
    private static List<ParsedManga> CollectNew(IEnumerable<ParsedManga> mangas, Dictionary<string, SweptSeries> swept, HashSet<string> processedThisLatest)
    {
        var fresh = new List<ParsedManga>();
        foreach (ParsedManga m in mangas)
        {
            string key = SeriesKey(m);
            if (!string.IsNullOrEmpty(key) && !swept.ContainsKey(key) && processedThisLatest.Add(key))
                fresh.Add(m);
        }
        return fresh;
    }

    /// <summary>
    /// Canonical identity of a series entry within a source pass. The SAME value keys the
    /// <c>swept</c> map, the ordered GetLatest baseline ids and both <see cref="CollectNew"/>
    /// filters, so "already processed" is consistent no matter which pass produced the entry.
    /// <see cref="ParsedManga.RealUrl"/> is deliberately NOT used: it can differ from
    /// <see cref="Manga.Url"/> for some sources and would make the same series look new on every
    /// page (and therefore be re-matched repeatedly).
    /// </summary>
    private static string SeriesKey(ParsedManga m) =>
        string.IsNullOrEmpty(m.Url) ? (m.Title ?? string.Empty) : m.Url;

    /// <summary>
    /// Runs the metadata scan for an explicit set of mapping (series) ids, returning the number of
    /// mappings scanned. Must be called AFTER the batch has been written (and saved) so the series
    /// rows exist in the contribution DB. The caller decides WHICH mappings to scan: the sweep's
    /// gated mode passes only the new / not-fully-covered mappings, while --force passes them all.
    /// </summary>
    private async Task<int> MatchMappingsAsync(
        List<Guid> mappingIds, HashSet<ExternalSeriesProvider> enabledProviders, CancellationToken token)
    {
        if (mappingIds.Count == 0)
            return 0;

        _logger.LogInformation("Match pass over {MappingCount} mapping(s) to scan.", mappingIds.Count);

        // Restrict to the content-type-allowed providers (an empty set means the package's content
        // is skip-only, so the series is recorded without running any metadata provider).
        List<IExternalSeriesProvider> providers = _providerSupport.GetMetadataProvidersInOrder()
            .Where(p => enabledProviders.Contains(p.ProviderType))
            .ToList();
        if (providers.Count == 0)
        {
            _logger.LogInformation("No metadata provider allowed for this package's content; skipping match.");
            return 0;
        }
        _logger.LogInformation("Authenticating {ProviderCount} metadata provider(s)…", providers.Count);
        await _providerSupport.EnsureProvidersAuthenticatedAsync(providers, token).ConfigureAwait(false);
        _logger.LogInformation("Providers ready; scanning {MappingCount} mapping(s)…", mappingIds.Count);
        await _mapping.ScanMappingsAsync(mappingIds, providers, token).ConfigureAwait(false);
        _logger.LogInformation("Metadata scan completed for {MappingCount} mapping(s).", mappingIds.Count);
        return mappingIds.Count;
    }

    /// <summary>
    /// The two-phase sweep's post-scan metadata pass. Everything has already been scanned by
    /// Phase 1; this runs the metadata retrieval ONCE per candidate series GROUP (not once per
    /// source entry), which is the whole optimization.
    ///
    /// Fixed-point loop (bounded by <see cref="TwoPhaseSweepMaxPasses"/>):
    ///   1. JOIN pass — reconcile the grouping so series sharing a title (or a provider anchor)
    ///      collapse into ONE, so the metadata search runs once per group rather than per entry.
    ///   2. CANDIDATES — every live series entry with <c>Metadata = false</c>, grouped by series and
    ///      paired with the source packages its entries belong to.
    ///   3. METADATA — authenticate the union of providers those packages are ALLOWED (per-package
    ///      content-type gate) and scan the candidate series: retrieval + automatch + provider-title
    ///      augmentation. Every scanned series ends with at least one live metadata row (a real link
    ///      or an Unmatched placeholder), so the flag flips true and the loop makes progress.
    ///   4. The next iteration's JOIN re-collapses any series the augmentation just made share a
    ///      title with another (possible auto-join), and the flag recompute settles each group.
    ///
    /// Series whose packages allow NO metadata provider are EXCLUDED (they can never gain
    /// metadata), so the loop always converges. After the loop the JOIN is settled once more and the
    /// derived flag is recomputed for the WHOLE graph.
    /// </summary>
    private async Task RunMetadataPassAsync(
        HashSet<ExternalSeriesProvider> enabledProviders, CancellationToken token)
    {
        if (enabledProviders.Count == 0)
        {
            _logger.LogInformation("Two-phase sweep: no enabled metadata provider; skipping the metadata pass.");
            return;
        }

        _logger.LogInformation(
            "Two-phase sweep: starting the post-scan metadata pass (max {Max} pass(es)).", TwoPhaseSweepMaxPasses);

        bool converged = false;
        for (int pass = 1; pass <= TwoPhaseSweepMaxPasses && !token.IsCancellationRequested; pass++)
        {
            // 1. JOIN pass — collapse same-title / same-anchor series FIRST so the metadata search
            //    below runs once per group (join-before-metadata is the core optimization).
            int joins = await _mapping.ReconcileSeriesGroupingAsync(token).ConfigureAwait(false);

            // 2. Candidates: the Metadata = false series + the packages their entries belong to.
            Dictionary<Guid, HashSet<string>> pending =
                await _mapping.FindSeriesPendingMetadataAsync(token).ConfigureAwait(false);
            if (pending.Count == 0)
            {
                converged = true;
                _logger.LogInformation(
                    "Two-phase sweep: metadata pass converged after {Pass} pass(es) ({Joins} join/split this pass).",
                    pass - 1, joins);
                break;
            }

            // 3. Resolve the providers ALLOWED for the candidates' packages (content-type gate).
            //    Series whose packages allow no provider can never gain metadata -> excluded, so the
            //    loop cannot spin on them.
            var providerSet = new HashSet<ExternalSeriesProvider>();
            var candidates = new List<Guid>();
            foreach (var (seriesId, packages) in pending)
            {
                bool anyAllowed = false;
                foreach (string package in packages)
                {
                    foreach (var p in ResolveEnabledProvidersForPackage(package, enabledProviders))
                    {
                        providerSet.Add(p);
                        anyAllowed = true;
                    }
                }
                if (anyAllowed)
                    candidates.Add(seriesId);
            }

            if (candidates.Count == 0 || providerSet.Count == 0)
            {
                converged = true;
                _logger.LogInformation(
                    "Two-phase sweep: {Count} series still lack metadata but none has an allowed provider; stopping.",
                    pending.Count);
                break;
            }

            List<IExternalSeriesProvider> providers = _providerSupport.GetMetadataProvidersInOrder()
                .Where(p => providerSet.Contains(p.ProviderType))
                .ToList();
            await _providerSupport.EnsureProvidersAuthenticatedAsync(providers, token).ConfigureAwait(false);

            _logger.LogInformation(
                "Two-phase sweep metadata pass {Pass}: scanning {Count} candidate series across {Providers} provider(s).",
                pass, candidates.Count, providers.Count);

            // 4. METADATA: retrieval + automatch + provider-title augmentation for the group.
            await _mapping.ScanMappingsAsync(candidates, providers, token).ConfigureAwait(false);
        }

        if (!converged && !token.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Two-phase sweep: metadata pass hit the {Max}-pass cap with candidates still pending; " +
                "stopping to avoid an unbounded loop (the remaining series will be retried on the next sweep).",
                TwoPhaseSweepMaxPasses);
        }

        // Final settle: JOIN once more (idempotent) then recompute the derived flag over the whole
        // graph so merged groups and scanned series end in a consistent state.
        await _mapping.ReconcileSeriesGroupingAsync(token).ConfigureAwait(false);
        await _repair.RecomputeSeriesMetadataFlagsAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Flips SeenInLatest on series already written from the popular pass that the
    /// latest tail also returned. Reuses the stored record/mapping so no re-match
    /// is needed.
    /// </summary>
    private async Task<int> FlagLatestAsync(
        string package, SourceDescriptor source, Guid sourceEntityId, List<string> urls,
        Dictionary<string, SweptSeries> swept, CancellationToken token)
    {
        int updated = 0;
        foreach (string url in urls)
        {
            if (!swept.TryGetValue(url, out SweptSeries? series) || series.Record.SeenInLatest)
                continue;

            // Build a fresh record (not mutate the tracked instance): UpsertSeriesRecordAsync
            // detects changes by comparing serialized payloads, so a mutated instance would
            // compare equal to itself and be skipped.
            ContributionRecordV2 flipped = CopyWithLatest(series.Record);

            // Reuse the stored mapping id so no re-match is needed; the package and source id
            // are the same for every series in this source pass.
            await _writer.UpsertSeriesRecordAsync(
                series.SeriesId, sourceEntityId, package, url, flipped, token).ConfigureAwait(false);
            series.Record = flipped;
            updated++;
        }

        if (updated > 0)
            await _writer.SaveAsync(token).ConfigureAwait(false);
        return updated;
    }

    private static ContributionRecordV2 CopyWithLatest(ContributionRecordV2 source) => new()
    {
        SchemaVersion = source.SchemaVersion,
        Url = source.Url,
        TitleId = source.TitleId,
        ThumbnailUrl = source.ThumbnailUrl,
        Status = source.Status,
        Memo = source.Memo,
        SeenInPopular = source.SeenInPopular,
        SeenInLatest = true,
        LastChapter = source.LastChapter,
        LastUpdateUTC = DateTime.UtcNow
    };

    /// <summary>
    /// Order-preserving match of <paramref name="current"/> against <paramref name="stored"/>
    /// (both newest-first), returning how many stored baseline ids were re-matched in order.
    ///
    /// The leading items of <paramref name="current"/> that precede the first baseline hit are
    /// FREE: they are exactly the new series/chapters published since the last run and must not
    /// count against the tolerance. Once matching has begun, up to <paramref name="maxMisses"/>
    /// interior gaps (a re-ordered/re-published item) are absorbed; a longer gap means the runs
    /// diverged and the count stops there. The caller compares the result to its threshold.
    /// </summary>
 internal static int CountBaselineMatches(
 IReadOnlyList<string> current, IReadOnlyList<string> stored, int maxMisses)
 {
 if (stored.Count == 0 || current.Count == 0)
 return 0;

 int i = 0;
 int matched = 0;
 int misses = 0;
 for (int k = 0; k < current.Count && i < stored.Count; k++)
 {
 if (current[k] == stored[i])
 {
 matched++;
 i++;
 misses = 0;
 }
 else if (i > 0)
 {
 // Only count a gap once matching has begun — anything before the first hit is a leading
 // new item and is free.
 misses++;
 if (misses > maxMisses)
 break;
 }
 }
 return matched;
 }

 private sealed class SweptSeries
 {
        public SweptSeries(Guid seriesId, ContributionRecordV2 record)
        {
            SeriesId = seriesId;
            Record = record;
        }

        public Guid SeriesId { get; }
        public ContributionRecordV2 Record { get; set; }
    }
}