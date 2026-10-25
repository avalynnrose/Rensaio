using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RensaioBackend.Data;
using RensaioBackend.Models;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Jobs;
using RensaioBackend.Services.Metadata;
using RensaioBackend.Services.Scrobbling;
using RensaioBackend.Services.Scrobbling.Abstractions;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// Read/write service for the Contribution Mappings page (source scope). Reads and mutates the
/// local contribution database (<c>contributor.db</c>) as the source of truth.
///
/// Auto-merge semantics: sources whose normalized titles collide (they resolve to the same
/// <see cref="TitleEntity"/>) are grouped under one <see cref="SeriesEntity"/>. All writes stamp
/// <c>Version = 0</c> (add/update) or <c>-1</c> (tombstone) so the existing
/// <see cref="ContributionUploadService"/> picks them up for cloud sync unchanged.
///
/// The cross-provider search/score/propagation algorithm is delegated to
/// <see cref="MetadataMatchCore"/> (shared with the Rensaio-DB External Mappings flow) — nothing
/// is copy-pasted.
/// </summary>
public sealed partial class ContributionMappingService
{
    private const int VersionAddOrUpdate = ContributionVersions.AddOrUpdate;
    private const int VersionLogicalDelete = ContributionVersions.PendingDelete;
    private const int VersionUploadedDelete = ContributionVersions.UploadedDelete;

    // SignalR progress ids — distinct from the External Mappings engine's "metadata-link-*".
    public const string LinkAllJobId = "contribution-link-all";

    private readonly ContributionDbContext _contributorDb;
    private readonly AppDbContext _db;
    private readonly MetadataMatchCore _matchCore;
    private readonly JobHubReportService _hubReport;
    private readonly MetadataTypeGate _typeGate;
    private readonly ContributionTitleHeuristicsService _titleHeuristics;
    private readonly ContributionGraphWriter _graphWriter;
    private readonly ContributionGraphRepairService _graphRepair;
    private readonly ExternalSeriesProviderFactory _providerFactory;
    private readonly ContributionDbGate _gate;
    private readonly MangaUrlResolver _urlResolver;
    private readonly ImageMatchCore _imageMatchCore;
    private readonly ILogger<ContributionMappingService> _logger;

    public ContributionMappingService(
        ContributionDbContext contributorDb,
        AppDbContext db,
        MetadataMatchCore matchCore,
        JobHubReportService hubReport,
        MetadataTypeGate typeGate,
        ContributionTitleHeuristicsService titleHeuristics,
        ContributionGraphWriter graphWriter,
        ContributionGraphRepairService graphRepair,
        ExternalSeriesProviderFactory providerFactory,
        ContributionDbGate gate,
        MangaUrlResolver urlResolver,
        ImageMatchCore imageMatchCore,
        ILogger<ContributionMappingService> logger)
    {
        _contributorDb = contributorDb;
        _db = db;
        _matchCore = matchCore;
        _hubReport = hubReport;
        _typeGate = typeGate;
        _titleHeuristics = titleHeuristics;
        _graphWriter = graphWriter;
        _graphRepair = graphRepair;
        _providerFactory = providerFactory;
        _gate = gate;
        _urlResolver = urlResolver;
        _imageMatchCore = imageMatchCore;
        _logger = logger;
    }

    /// <summary>
    /// Lists contribution mappings grouped by mapping (source scope), paginated over mappings.
    /// filter=all|unmatched|blocked: `unmatched` keeps only mappings with at least one Unmatched
    /// provider row; `blocked` keeps only mappings with at least one Blocked provider row.
    /// <paramref name="name"/> is an additional GLOBAL filter that is ANDed with <paramref name="filter"/>:
    /// a mapping is kept only when the term is contained (case-insensitively) in one of its titles,
    /// member source names/keys, or the display title.
    /// </summary>
    public async Task<ContributionMappingsPageDto> ListMappingsAsync(
        string filter, int page, int pageSize, ExternalSeriesProvider? provider, SeriesMappingStatus? status,
        string? name, string verified, List<IExternalSeriesProvider> enabledProviders, CancellationToken token)
    {
        var isUnmatchedFilter = string.Equals(filter, "unmatched", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(filter);
        var isBlockedFilter = string.Equals(filter, "blocked", StringComparison.OrdinalIgnoreCase);
        var isManualIgnoredFilter = string.Equals(filter, "manualignored", StringComparison.OrdinalIgnoreCase);
        // Verified combo: "unverified" = any live entry with a null date; "verified" = has a live
        // entry and no live entry with a null date; anything else ("all") = no filter.
        var isUnverifiedFilter = string.Equals(verified, "unverified", StringComparison.OrdinalIgnoreCase);
        var isVerifiedFilter = string.Equals(verified, "verified", StringComparison.OrdinalIgnoreCase);
        var normalizedName = name?.Trim();
        var hasNameFilter = !string.IsNullOrWhiteSpace(normalizedName);

        var result = new ContributionMappingsPageDto
        {
            Page = page,
            PageSize = pageSize,
            Total = 0,
            ProviderMeta = ExternalMappingSupport.BuildProviderMeta(enabledProviders)
        };

        // ── SQL-side page selection ─────────────────────────────────────────────────────────
        // The page must NOT materialize the whole graph. Previously this method loaded EVERY
        // live SeriesTitle + SeriesEntry (each carrying the Data JSON blob) + Metadata row,
        // grouped/sorted ALL mappings in memory, applied the unmatched/blocked filter AFTER the
        // fact, and only then Skip/Take'd the page — so a 50-row page cost the whole graph
        // (several seconds on a large contributor.db). Now the filter, sort and paging run in
        // SQL and only the page's detail rows are loaded.
        //
        // LIVE semantics (unchanged): a row is live when Version is not -1 (PendingDelete) and
        // not -3 (UploadedDelete); -2 (Uploaded) and 0 (pending) are live.
        var liveTitle = _contributorDb.SeriesTitles
            .Where(st => st.Version != VersionLogicalDelete
                && st.Version != VersionUploadedDelete
                && st.Title != null);

        // One row per mapping that owns at least one live title link, with a deterministic
        // display title (MIN of the linked titles) used for BOTH ordering and display so the
        // rendered order always matches the visible name. (Previously the "first" title of an
        // unordered in-memory group was shown, which could not be reproduced in SQL.)
        var titleGroups = liveTitle
            .GroupBy(st => st.SeriesId)
            .Select(g => new
            {
                MappingId = g.Key,
                DisplayTitle = g.Min(st => st.Title!.Title)
            });

        // Per-mapping provider flags from the LIVE metadata rows, honouring the provider/status
        // filters (a ManualIgnore is a settled refusal, never treated as Unmatched).
        var liveMeta = _contributorDb.Metadata
            .Where(m => m.Version != VersionLogicalDelete
                && m.Version != VersionUploadedDelete);

        if (provider.HasValue)
        {
            int providerId = (int)provider.Value;
            liveMeta = liveMeta.Where(m => m.ProviderId == providerId);
        }

        if (status.HasValue && status.Value == SeriesMappingStatus.Unmatched)
        {
            liveMeta = liveMeta.Where(m => m.MappingStatus == SeriesMappingStatus.Unmatched);
        }
        else if (status.HasValue)
        {
            var wantedStatus = status.Value;
            liveMeta = liveMeta.Where(m => m.MappingStatus == wantedStatus);
        }

        // Mapping ids that own ≥1 qualifying live metadata row. Used as a translation-safe
        // "has metadata" gate (a correlated Contains / EXISTS instead of a group-by join, which
        // is more portable across EF providers).
        var qualifyingMappingIds = liveMeta
            .Select(m => m.SeriesId)
            .Distinct();

        var query = titleGroups.Where(t => qualifyingMappingIds.Contains(t.MappingId));

        if (isUnmatchedFilter)
        {
            query = query.Where(x => _contributorDb.Metadata.Any(m =>
                m.SeriesId == x.MappingId
                && m.Version != VersionLogicalDelete
                && m.Version != VersionUploadedDelete
                && m.MappingStatus == SeriesMappingStatus.Unmatched));
        }

        if (isBlockedFilter)
        {
            query = query.Where(x => _contributorDb.Metadata.Any(m =>
                m.SeriesId == x.MappingId
                && m.Version != VersionLogicalDelete
                && m.Version != VersionUploadedDelete
                && m.MappingStatus == SeriesMappingStatus.Blocked));
        }

        if (isManualIgnoredFilter)
        {
            query = query.Where(x => _contributorDb.Metadata.Any(m =>
                m.SeriesId == x.MappingId
                && m.Version != VersionLogicalDelete
                && m.Version != VersionUploadedDelete
                && m.MappingStatus == SeriesMappingStatus.ManualIgnore));
        }

        if (isUnverifiedFilter)
        {
            query = query.Where(x => _contributorDb.SeriesEntries.Any(se =>
                se.SeriesId == x.MappingId
                && se.Version != VersionLogicalDelete
                && se.Version != VersionUploadedDelete
                && se.VerifiedDateUTC == null));
        }

        if (isVerifiedFilter)
        {
            // Has at least one live entry AND no live entry with a null date.
            query = query.Where(x =>
                _contributorDb.SeriesEntries.Any(se =>
                    se.SeriesId == x.MappingId
                    && se.Version != VersionLogicalDelete
                    && se.Version != VersionUploadedDelete)
                && !_contributorDb.SeriesEntries.Any(se =>
                    se.SeriesId == x.MappingId
                    && se.Version != VersionLogicalDelete
                    && se.Version != VersionUploadedDelete
                    && se.VerifiedDateUTC == null));
        }

        // GLOBAL name filter (ANDed with the status combo): kept server-side so paging stays
        // correct. Matches a live title OR a member source's name/package.
        if (hasNameFilter)
        {
            var pattern = $"%{normalizedName}%";
            query = query.Where(x =>
                _contributorDb.SeriesTitles.Any(st => st.SeriesId == x.MappingId
                    && st.Version != VersionLogicalDelete
                    && st.Version != VersionUploadedDelete
                    && st.Title != null
                    && EF.Functions.Like(st.Title.Title, pattern))
                || _contributorDb.SeriesEntries.Any(se => se.SeriesId == x.MappingId
                    && se.Version != VersionLogicalDelete
                    && se.Version != VersionUploadedDelete
                    && se.Sources.Any(ss => ss.Version != VersionLogicalDelete
                        && ss.Version != VersionUploadedDelete
                        && ss.Source != null
                        && (EF.Functions.Like(ss.Source.SourceName, pattern)
                            || EF.Functions.Like(ss.Source.Package, pattern)))));
        }

        result.Total = await query.CountAsync(token).ConfigureAwait(false);

        var pageRows = await query
            .OrderBy(x => x.DisplayTitle)
            .Skip(page * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        if (pageRows.Count == 0)
            return result;

        var pageIds = pageRows.Select(r => r.MappingId).ToList();

        // ── Detail rows for the PAGE ONLY ──
        var seriesTitles = await _contributorDb.SeriesTitles
            .Include(mt => mt.Title)
            .Where(mt => pageIds.Contains(mt.SeriesId)
                && mt.Version != VersionLogicalDelete
                && mt.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        var seriesRows = await _contributorDb.SeriesEntries
            .Include(s => s.Sources)
                .ThenInclude(ss => ss.Source)
            .Where(s => pageIds.Contains(s.SeriesId)
                && s.Version != VersionLogicalDelete
                && s.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        var metadataRows = await _contributorDb.Metadata
            .Where(m => pageIds.Contains(m.SeriesId)
                && m.Version != VersionLogicalDelete
                && m.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        // Resolve the provider-reported titles behind the PAGE's rows' ProviderTitleIds, so the
        // provider columns can show what that link actually vouches for. Scoped to the page —
        // previously this IN(...) spanned the whole graph.
        var providerTitleIdSet = metadataRows
            .SelectMany(m => m.ProviderTitleIds)
            .Where(g => g != Guid.Empty)
            .ToHashSet();
        var titleById = providerTitleIdSet.Count == 0
            ? new Dictionary<Guid, string>()
            : await _contributorDb.Titles
                .Where(t => providerTitleIdSet.Contains(t.Id))
                .AsNoTracking()
                .ToDictionaryAsync(t => t.Id, t => t.Title, token).ConfigureAwait(false);

        // Index the page's row collections by mapping for O(1) per-mapping lookups.
        var seriesTitlesByMapping = seriesTitles
            .GroupBy(mt => mt.SeriesId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var seriesRowsByMapping = seriesRows
            .GroupBy(s => s.SeriesId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var metadataRowsByMapping = metadataRows
            .GroupBy(m => m.SeriesId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var groups = new List<ContributionMappingGroupDto>(pageRows.Count);

        foreach (var pageRow in pageRows)
        {
            var mappingId = pageRow.MappingId;

            var titles = seriesTitlesByMapping.TryGetValue(mappingId, out var mappingTitleRows)
                ? mappingTitleRows
                    .Where(mt => mt.Title != null && !string.IsNullOrWhiteSpace(mt.Title.Title))
                    .Select(mt => mt.Title!.Title!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<string>();

            var titleIndex = BuildTitleIndex(mappingTitleRows ?? (IEnumerable<SeriesTitleEntity>)Array.Empty<SeriesTitleEntity>());
            List<ContributionMappingSourceDto> sources;
            if (seriesRowsByMapping.TryGetValue(mappingId, out var mappingSeriesRows))
            {
                var built = await Task.WhenAll(
                    mappingSeriesRows.Select(s => ToSourceDtosAsync(s, titleIndex, token))).ConfigureAwait(false);
                sources = built.SelectMany(x => x).ToList();
            }
            else
            {
                sources = new List<ContributionMappingSourceDto>();
            }

            var providerRows = new List<ExternalMappingsSeriesProviderDto>();
            // D: hide historical same-key duplicates on read (Blocked rows pass through).
            var mappingMetadataRows = metadataRowsByMapping.TryGetValue(mappingId, out var metaRows)
                ? metaRows
                : (IEnumerable<ContributionMetadataEntity>)Array.Empty<ContributionMetadataEntity>();

            foreach (var m in DedupeProviderRows(mappingMetadataRows))
            {
                if (provider.HasValue && (ExternalSeriesProvider)m.ProviderId != provider.Value) continue;

                // Provider-reported titles, canonical first (ProviderTitleIds order), then the rest.
                var providerTitles = m.ProviderTitleIds
                    .Where(g => g != Guid.Empty && titleById.ContainsKey(g))
                    .Select(g => titleById[g])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var row = new ExternalMappingsSeriesProviderDto
                {
                    ProviderCoverUrl = m.CoverUrl,
                    Provider = (ExternalSeriesProvider)m.ProviderId,
                    ExternalSeriesId = m.ProviderKey ?? string.Empty,
                    ExternalSeriesTitle = providerTitles.FirstOrDefault(),
                    MappingStatus = m.MappingStatus,
                    LinkedDate = m.LinkedDate,
                    LinkedSitesIds = [],
                    AlternativeTitles = providerTitles.Skip(1).ToList()
                };

                if (status.HasValue && row.MappingStatus != status.Value) continue;
                if (!status.HasValue && !IsActiveStatus(row.MappingStatus)) continue;

                providerRows.Add(row);
            }

            if (providerRows.Count == 0) continue;

            // Verified state derived from the mapping's live entries: Verified only when every
            // live entry carries a date; Unverified when any live entry is null. The date shown is
            // the most recent one among the entries.
            var liveEntries = seriesRowsByMapping.TryGetValue(mappingId, out var entryRows)
                ? entryRows.Where(e => e.VerifiedDateUTC is not null).Select(e => e.VerifiedDateUTC!.Value).ToList()
                : new List<DateTime>();
            var mappingHasEntries = seriesRowsByMapping.ContainsKey(mappingId) && seriesRowsByMapping[mappingId].Count > 0;
            var isVerified = mappingHasEntries && liveEntries.Count == seriesRowsByMapping[mappingId].Count;
            DateTime? verifiedDateUtc = isVerified ? liveEntries.Max() : null;

            groups.Add(new ContributionMappingGroupDto
            {
                MappingId = mappingId,
                DisplayTitle = string.IsNullOrWhiteSpace(pageRow.DisplayTitle) ? "Untitled" : pageRow.DisplayTitle,
                CoverUrl = sources.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.ThumbnailUrl))?.ThumbnailUrl,
                Titles = titles,
                Sources = sources,
                Providers = providerRows,
                VerifiedDateUtc = verifiedDateUtc
            });
        }

        result.Groups = groups;
        return result;
    }

    /// <summary>Single contribution mapping group (all provider rows + merged sources + titles).</summary>
    public async Task<ContributionMappingGroupDto?> GetMappingAsync(
        Guid mappingId, List<IExternalSeriesProvider> enabledProviders, CancellationToken token)
    {
        var seriesTitles = await _contributorDb.SeriesTitles
            .Include(mt => mt.Title)
            .Where(mt => mt.SeriesId == mappingId
                && mt.Version != VersionLogicalDelete
                && mt.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        if (seriesTitles.Count == 0) return null;

        var titles = seriesTitles
            .Where(mt => mt.Title != null && !string.IsNullOrWhiteSpace(mt.Title.Title))
            .Select(mt => mt.Title!.Title!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var seriesRows = await _contributorDb.SeriesEntries
            .Include(s => s.Sources)
                .ThenInclude(ss => ss.Source)
            .Where(s => s.SeriesId == mappingId
                && s.Version != VersionLogicalDelete
                && s.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        var titleIndex = BuildTitleIndex(seriesTitles);
        var builtSources = await Task.WhenAll(
            seriesRows.Select(s => ToSourceDtosAsync(s, titleIndex, token))).ConfigureAwait(false);
        var sources = builtSources.SelectMany(x => x).ToList();

        var metadataRows = await _contributorDb.Metadata
            .Where(m => m.SeriesId == mappingId
                && m.Version != VersionLogicalDelete
                && m.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        // Resolve the provider-reported titles behind each row's ProviderTitleIds.
        var providerTitleIdSet = metadataRows
            .SelectMany(m => m.ProviderTitleIds)
            .Where(g => g != Guid.Empty)
            .ToHashSet();
        var titleById = providerTitleIdSet.Count == 0
            ? new Dictionary<Guid, string>()
            : await _contributorDb.Titles
                .Where(t => providerTitleIdSet.Contains(t.Id))
                .AsNoTracking()
                .ToDictionaryAsync(t => t.Id, t => t.Title, token).ConfigureAwait(false);

        var providerRows = DedupeProviderRows(metadataRows).Select(m =>
        {
            var providerTitles = m.ProviderTitleIds
                .Where(g => g != Guid.Empty && titleById.ContainsKey(g))
                .Select(g => titleById[g])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new ExternalMappingsSeriesProviderDto
            {
                ProviderCoverUrl = m.CoverUrl,
                Provider = (ExternalSeriesProvider)m.ProviderId,
                ExternalSeriesId = m.ProviderKey ?? string.Empty,
                ExternalSeriesTitle = providerTitles.FirstOrDefault(),
                MappingStatus = m.MappingStatus,
                LinkedDate = m.LinkedDate,
                LinkedSitesIds = [],
                AlternativeTitles = providerTitles.Skip(1).ToList()
            };
        }).ToList();

        // Verified state derived from the mapping's live entries (same rule as the list).
        var verifiedDates = seriesRows
            .Where(s => s.VerifiedDateUTC is not null)
            .Select(s => s.VerifiedDateUTC!.Value)
            .ToList();
        var isVerified = seriesRows.Count > 0 && verifiedDates.Count == seriesRows.Count;

        return new ContributionMappingGroupDto
        {
            MappingId = mappingId,
            DisplayTitle = titles.FirstOrDefault() ?? "Untitled",
            CoverUrl = sources.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.ThumbnailUrl))?.ThumbnailUrl,
            Titles = titles,
            Sources = sources,
            Providers = providerRows,
            VerifiedDateUtc = isVerified ? verifiedDates.Max() : null
        };
    }

    /// <summary>
    /// Resolves the titles of a mapping, builds seeds/rules from its metadata rows, runs the shared
    /// matcher and persists the linked results + materialized Unmatched rows into the contribution
    /// DB. Mirrors <c>MetadataLinkEngine.LinkSeriesAsync</c> but against <c>ContributionDbContext</c>.
    /// </summary>
    public async Task<Metadata.MetadataMatchResult> ScanMappingAsync(
        Guid mappingId, List<IExternalSeriesProvider> providers, CancellationToken token)
    {
        // 1. Gather the mapping's title candidates (the automerge identity set).
        var seriesTitles = await _contributorDb.SeriesTitles
            .Include(mt => mt.Title)
            .Where(mt => mt.SeriesId == mappingId)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        var titleCandidates = seriesTitles
            .Where(mt => mt.Title != null && !string.IsNullOrWhiteSpace(mt.Title.Title))
            .Select(mt => mt.Title!.Title!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (titleCandidates.Count == 0) return new Metadata.MetadataMatchResult();

        // 2. Derive seeds, blocked ids and skipped providers from the existing metadata rows.
        //    Tombstoned rows (PendingDelete / UploadedDelete) MUST be excluded: a stale
        //    tombstoned Unmatched row would otherwise put its provider into pendingProviders,
        //    defeat IsSettled for a provider that already holds a live AutoMatched/UserConfirmed
        //    link, and make the scan re-search an already-matched provider.
        var existing = await _contributorDb.Metadata
            .Where(m => m.SeriesId == mappingId
                && m.Version != VersionLogicalDelete
                && m.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        var seeds = new List<MetadataLinkSeed>();
        var blockedIds = new Dictionary<ExternalSeriesProvider, HashSet<string>>();
        var skippedProviders = new HashSet<ExternalSeriesProvider>();
        var now = DateTime.UtcNow;

        foreach (var m in existing)
        {
            var p = (ExternalSeriesProvider)m.ProviderId;
            switch (m.MappingStatus)
            {
                case SeriesMappingStatus.Blocked:
                    if (!string.IsNullOrWhiteSpace(m.ProviderKey))
                    {
                        if (!blockedIds.TryGetValue(p, out var set))
                            blockedIds[p] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(m.ProviderKey);
                    }
                    break;
                case SeriesMappingStatus.ForeverIgnored:
                    skippedProviders.Add(p);
                    break;
                case SeriesMappingStatus.ManualIgnore:
                    // A manual ignore is a settled refusal: never re-searched or re-evaluated.
                    skippedProviders.Add(p);
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(m.ProviderKey))
                    {
                        seeds.Add(new MetadataLinkSeed
                        {
                            Provider = p,
                            ExternalSeriesId = m.ProviderKey,
                            Confidence = 1.0
                        });
                    }
                    break;
            }
        }

        // 2.5 CanSearchSeries gate — BEFORE searching any provider for this mapping, ask the
        //     provider whether it can search this kind of series at all. Genres come from the
        //     matching local SeriesEntity.Genre and the category from SeriesEntity.Type (the
        //     contribution schema stores neither). When a provider cannot search the series, the
        //     (mapping, provider) relation is settled as "ignoredAlways" (ForeverIgnored) so it
        //     is never searched again.
        var (genres, category) = await ResolveSeriesGenreAsync(titleCandidates, token).ConfigureAwait(false);

        // Local cover for image (PDQ) corroboration/tie-break of borderline title matches: the
        // mapping's source image from the contribution DB (preferred), else a local library cover.
        var (localCoverUrl, localMihonProviderId) = await ResolveLocalCoverAsync(mappingId, titleCandidates, token).ConfigureAwait(false);
        foreach (var provider in providers)
        {
            if (!provider.CanSearchSeries(genres, category))
            {
                skippedProviders.Add(provider.ProviderType);
                await EnsureProviderIgnoredAsync(mappingId, provider.ProviderType, now, token).ConfigureAwait(false);
            }
        }

        // 2.6 Content-type gate — the mapping's source packages are looked up in the root
        //     "TypeMappings" list to derive the content types its sources carry. A provider is only
        //     run when it declares support for at least one of those types; providers that support
        //     NONE are settled as "ignored always" (ForeverIgnored). A package whose content is only
        //     usercreatedcontent/doujinshi skips EVERY provider. An existing active link is a user
        //     decision and is never downgraded by the gate.
        try
        {
            var sourcePackages = await _contributorDb.SourceSeries
                .Where(ss => ss.SeriesEntity!.SeriesId == mappingId
                    && ss.Version != VersionLogicalDelete
                    && ss.Version != VersionUploadedDelete)
                .Select(ss => ss.Source!.Package)
                .Distinct()
                .ToListAsync(token).ConfigureAwait(false);

            // No known source package → no content to classify: leave every provider pending rather
            // than blanket-ignoring them on a mapping nothing can vouch for.
            if (sourcePackages.Count > 0)
            {
                foreach (var provider in providers)
                {
                    // Skip providers that already hold a settled decision (never downgrade a decision).
                    if (IsDecidedProvider(existing, provider.ProviderType))
                        continue;

                    if (!_typeGate.AnyPackageSupports(sourcePackages, provider))
                    {
                        skippedProviders.Add(provider.ProviderType);
                        await EnsureProviderIgnoredAsync(mappingId, provider.ProviderType, now, token)
                            .ConfigureAwait(false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Content-type gate: failed to resolve source packages for mapping {MappingId}; leaving providers unchanged.", mappingId);
        }

        // The rows loaded above are AsNoTracking snapshots, but EnsureProviderIgnoredAsync (just
        // above) re-queried them WITH tracking, so the context may now hold a DIFFERENT instance
        // for the same primary key. Re-bind every snapshot to the tracked instance:
        //   * without this, the Attach calls below throw "another instance with the same key
        //     value for {'Id'} is already being tracked"; and
        //   * the decision checks would evaluate against stale state — e.g. step 6 would tombstone
        //     a row that this pass just settled as ForeverIgnored, because the snapshot still
        //     shows the previous Unmatched status.
        var trackedMetadataById = _contributorDb.ChangeTracker.Entries<ContributionMetadataEntity>()
            .ToDictionary(e => e.Entity.Id, e => e.Entity);
        if (trackedMetadataById.Count > 0)
        {
            for (int i = 0; i < existing.Count; i++)
                if (trackedMetadataById.TryGetValue(existing[i].Id, out var trackedExisting))
                    existing[i] = trackedExisting;
        }

        // 3. Contribution-DB first match is intrinsic here: existing active links (AutoMatched /
        //    UserConfirmed rows with a provider key) are seeded into the matcher and never
        //    re-searched — HIT = no provider search, only metadata fetch when available.
        //
        //    SEARCH SCOPE: only providers with PENDING work are searched and seeded. A provider is
        //    SETTLED — and must never be re-searched, seeded or (re)linked — when it has an active
        //    link (AutoMatched/UserConfirmed), a refusal (Blocked), or an ignore
        //    (ManualIgnore / ForeverIgnored — both settled, no expiration). PENDING providers are:
        //    Unmatched (searched before, no match — re-search) and a provider with NO row at all
        //    (never searched). This mirrors the rensaio.db engine's GetPendingProviders so both
        //    scanners re-scan exactly the same set.
        bool IsPendingRow(ContributionMetadataEntity m) => m.MappingStatus switch
        {
            SeriesMappingStatus.Unmatched => true,
            // A Blocked row is NOT a settled decision: it only forbids auto-matching that exact
            // provider key. The provider stays pending so it can still match a DIFFERENT key
            // (the blocked key itself is filtered out via blockedIds).
            SeriesMappingStatus.Blocked => true,
            SeriesMappingStatus.ManualIgnore => false,
            SeriesMappingStatus.AutoMatched => false,
            SeriesMappingStatus.UserConfirmed => false,
            SeriesMappingStatus.ForeverIgnored => false,
            _ => true,
        };

        var pendingProviders = existing.Where(IsPendingRow)
            .Select(m => (ExternalSeriesProvider)m.ProviderId)
            .ToHashSet();

        // A provider holding an authoritative decision row is settled even when another row for
        // the SAME provider is pending: a live decision must always outrank a co-existing pending
        // duplicate (e.g. a leftover Unmatched row for a provider that is already AutoMatched).
        // Blocked/ManualIgnore/ForeverIgnored/UserConfirmed/AutoMatched are all decisions.
        bool IsDecidedRow(ContributionMetadataEntity m) => m.MappingStatus switch
        {
            // Blocked is deliberately EXCLUDED: a block is an id-scoped rule, not a provider-level
            // decision, so it must never settle the provider.
            SeriesMappingStatus.AutoMatched => true,
            SeriesMappingStatus.UserConfirmed => true,
            SeriesMappingStatus.ForeverIgnored => true,
            SeriesMappingStatus.ManualIgnore => true,
            _ => false,
        };
        var decidedProviders = existing.Where(IsDecidedRow)
            .Select(m => (ExternalSeriesProvider)m.ProviderId)
            .ToHashSet();

        // A provider is settled when it holds a decision, or has any non-pending row. A provider
        // with no row at all has never been searched and stays pending.
        bool IsSettled(ExternalSeriesProvider provider)
            => decidedProviders.Contains(provider)
                || (existing.Any(m => m.ProviderId == (int)provider)
                    && !pendingProviders.Contains(provider));

        var enabled = providers.Select(p => p.ProviderType).ToHashSet();

        // 3.5 Cross-mapping reuse: a provider link proven on ANOTHER mapping that shares one of
        //     this mapping's titles is a valid seed here too — the title identity is the same
        //     canonical TitleEntity, so the provider's external id is the same series. Without
        //     this, a series split across two SeriesEntity rows (two source entries) re-searches
        //     every provider from scratch on each mapping. Only Auto/User links count, and only
        //     for providers this mapping has NOT refused (block/ignore) or already settled.
        var localTitleIds = seriesTitles.Select(st => st.TitleId).Distinct().ToHashSet();
        if (localTitleIds.Count > 0)
        {
            var siblingMappingIds = await _contributorDb.SeriesTitles
                .Where(st => localTitleIds.Contains(st.TitleId)
                    && st.SeriesId != mappingId
                    && st.Version != VersionLogicalDelete
                    && st.Version != VersionUploadedDelete)
                .Select(st => st.SeriesId)
                .Distinct()
                .ToListAsync(token).ConfigureAwait(false);

            if (siblingMappingIds.Count > 0)
            {
                var siblingLinks = await _contributorDb.Metadata
                    .Where(m => siblingMappingIds.Contains(m.SeriesId)
                        && m.Version != VersionLogicalDelete
                        && m.Version != VersionUploadedDelete
                        && (m.MappingStatus == SeriesMappingStatus.AutoMatched
                            || m.MappingStatus == SeriesMappingStatus.UserConfirmed)
                        && m.ProviderKey != null && m.ProviderKey != string.Empty)
                    .AsNoTracking()
                    .Select(m => new { m.ProviderId, m.ProviderKey })
                    .ToListAsync(token).ConfigureAwait(false);

                var seededProviders = seeds.Select(s => s.Provider).ToHashSet();
                foreach (var link in siblingLinks)
                {
                    var p = (ExternalSeriesProvider)link.ProviderId;
                    if (!enabled.Contains(p)) continue;
                    if (skippedProviders.Contains(p) || IsSettled(p)) continue; // this mapping refused/decided
                    // A block on this exact id means it must never be reused as a seed here, even
                    // though the sibling mapping legitimately links it.
                    if (blockedIds.TryGetValue(p, out var idBlock) && idBlock.Contains(link.ProviderKey!)) continue;
                    if (!seededProviders.Add(p)) continue;                      // already seeded here
                    seeds.Add(new MetadataLinkSeed
                    {
                        Provider = p,
                        ExternalSeriesId = link.ProviderKey!,
                        Confidence = 1.0
                    });
                }
            }
        }

        var searchProviders = providers
            .Where(p => !skippedProviders.Contains(p.ProviderType) && !IsSettled(p.ProviderType))
            .ToList();

        Metadata.MetadataMatchResult match = searchProviders.Count > 0
            ? await _matchCore.MatchAsync(titleCandidates, searchProviders, seeds, blockedIds,
                skippedProviders, enabled, token: token, localCoverUrl: localCoverUrl,
                mihonProviderId: localMihonProviderId).ConfigureAwait(false)
            : new Metadata.MetadataMatchResult();

        // 4. Persist linked nodes as ContributionMetadata rows.
        var desired = new HashSet<(int ProviderId, string ProviderKey)>();
        var byIdentity = existing.ToDictionary(m => (m.ProviderId, m.ProviderKey ?? string.Empty));

        // Titles the providers vouched for this pass (canonical + alternates). Materialized into the
        // title graph below so the Contribution page shows EVERY associated title, not just the
        // source title the sweep linked.
        var providerTitles = new List<string>();

        // Guarantee every persisted LINK gets a cover ATTEMPT. The matcher marks a provider
        // Linked from the search result but only attaches a cover from a separate, best-effort
        // detail fetch — which is silently skipped when the (provider,id) was already fetched via
        // edge propagation (shared fetchedIds) or when the detail call fails. Without this fallback
        // such rows persisted as AutoMatched with CoverUrl = NULL (the "thousands of missing covers"
        // the startup backfill then repaired). Resolve the cover once per linked node here, so the
        // row is written with a cover whenever one is obtainable. CoverUrl is [JsonIgnore] — local
        // only — so this never affects Version or the pending-upload set.
        var coverFetchAttempted = new HashSet<(int ProviderId, string ProviderKey)>();

        void CollectProviderTitles(string? title, IEnumerable<string>? alternates)
        {
            if (!string.IsNullOrWhiteSpace(title)) providerTitles.Add(title!);
            if (alternates == null) return;
            foreach (var alt in alternates)
                if (!string.IsNullOrWhiteSpace(alt)) providerTitles.Add(alt);
        }

        foreach (var node in match.Links)
        {
            var nodeIdentity = ((int)node.Provider, node.ExternalSeriesId);

            // Never (re)link a settled provider. The matcher only searches pending providers, but a
            // seed-derived node can still surface on a provider that already holds a decision — an
            // active link, Blocked, ForeverIgnored or a ManualIgnore refusal.
            if (IsSettled(node.Provider))
            {
                if (byIdentity.TryGetValue(nodeIdentity, out var settledRow))
                {
                    // Backfill a missing LOCAL provider cover. CoverUrl is [JsonIgnore] (never
                    // exported), so this does not touch Version or the upload set.
                    string? settledCover = node.CoverUrl;
                    if (string.IsNullOrWhiteSpace(settledRow.CoverUrl) && string.IsNullOrWhiteSpace(settledCover)
                        && coverFetchAttempted.Add(nodeIdentity))
                        settledCover = await TryFetchCoverAsync(node.Provider, node.ExternalSeriesId, token)
                            .ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(settledRow.CoverUrl) && !string.IsNullOrWhiteSpace(settledCover))
                    {
                        _contributorDb.Metadata.Attach(settledRow);
                        settledRow.CoverUrl = settledCover;
                        _contributorDb.Entry(settledRow).Property(x => x.CoverUrl).IsModified = true;
                    }
                    // Refresh the local-only provider title ids for legacy rows created before this
                    // field existed, so the provider columns can render. [JsonIgnore]: local-only.
                    var settledTitleIds = ContributionTitleHeuristicsService.DeriveProviderTitleIds(node.Title, node.AlternativeTitles);
                    if (settledTitleIds.Count > 0 && !settledRow.ProviderTitleIds.SequenceEqual(settledTitleIds))
                    {
                        _contributorDb.Metadata.Attach(settledRow);
                        settledRow.ProviderTitleIds = settledTitleIds;
                        _contributorDb.Entry(settledRow).Property(x => x.ProviderTitleIds).IsModified = true;
                    }
                }
                // A settled decision still vouches for its titles — surface them even though the
                // row itself is not rewritten.
                CollectProviderTitles(node.Title, node.AlternativeTitles);
                continue;
            }

            // Persist the provider's reported titles (canonical + alternates) so this AutoMatched
            // row authoritatively vouches for exactly those titles at reconcile time — without any
            // rensaio.db lookup.
            var providerTitleIds = ContributionTitleHeuristicsService.DeriveProviderTitleIds(node.Title, node.AlternativeTitles);


            // Feed the newly-matched node's titles into the materialization set so step 4.5 links
            // them (this branch runs on a FRESH scan, where nothing is settled yet). Without this
            // the provider titles were fetched but never written to the title graph.
            CollectProviderTitles(node.Title, node.AlternativeTitles);
            var identity = ((int)node.Provider, node.ExternalSeriesId);
            desired.Add(identity);
            if (byIdentity.TryGetValue(identity, out var row))
            {
                // Fill the LOCAL provider cover (resolved by the matcher from GetDetails) when
                // missing. CoverUrl is [JsonIgnore] — never exported — so this must NOT touch
                // the replication Version or the pending upload set.
                string? nodeCover = node.CoverUrl;
                if (string.IsNullOrWhiteSpace(row.CoverUrl) && string.IsNullOrWhiteSpace(nodeCover)
                    && coverFetchAttempted.Add(identity))
                    nodeCover = await TryFetchCoverAsync(node.Provider, node.ExternalSeriesId, token)
                        .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(row.CoverUrl) && !string.IsNullOrWhiteSpace(nodeCover))
                {
                    _contributorDb.Metadata.Attach(row);
                    row.CoverUrl = nodeCover;
                    _contributorDb.Entry(row).Property(x => x.CoverUrl).IsModified = true;
                }

                var changed = row.MappingStatus != SeriesMappingStatus.AutoMatched
                    || row.Version == VersionLogicalDelete
                    || row.Version == VersionUploadedDelete
                    || row.LinkedDate == null;
                if (changed)
                {
                    _contributorDb.Metadata.Attach(row);
                    row.MappingStatus = SeriesMappingStatus.AutoMatched;
                    row.LinkedDate = now;
                    row.Version = VersionAddOrUpdate;
                    _contributorDb.Entry(row).Property(x => x.MappingStatus).IsModified = true;
                    _contributorDb.Entry(row).Property(x => x.LinkedDate).IsModified = true;
                    _contributorDb.Entry(row).Property(x => x.Version).IsModified = true;
                }

                // Always refresh the local-only provider title set (it does not affect replication).
                if (!row.ProviderTitleIds.SequenceEqual(providerTitleIds))
                {
                    _contributorDb.Metadata.Attach(row);
                    row.ProviderTitleIds = providerTitleIds;
                    _contributorDb.Entry(row).Property(x => x.ProviderTitleIds).IsModified = true;
                }
            }
            else
            {
                // Guarantee a cover attempt for a brand-new link too (the matcher's detail fetch
                // may have been deduped/skipped). Best-effort — the startup backfill retries later.
                string? newNodeCover = node.CoverUrl;
                if (string.IsNullOrWhiteSpace(newNodeCover) && coverFetchAttempted.Add(identity))
                    newNodeCover = await TryFetchCoverAsync(node.Provider, node.ExternalSeriesId, token)
                        .ConfigureAwait(false);

                _contributorDb.Metadata.Add(new ContributionMetadataEntity
                {
                    Id = Guid.NewGuid(),
                    SeriesId = mappingId,
                    ProviderId = (int)node.Provider,
                    ProviderKey = node.ExternalSeriesId,
                    MappingStatus = SeriesMappingStatus.AutoMatched,
                    LinkedDate = now,
                    CoverUrl = newNodeCover,
                    ProviderTitleIds = providerTitleIds,
                    Version = VersionAddOrUpdate
                });
            }
        }

        // 4.5 Materialize the provider-vouched titles (canonical + alternates) into the title graph,
        //     so the Contribution page surfaces EVERY associated title — not just the source title
        //     the sweep linked. ProviderTitleIds (step 4) only KEEP existing links at reconcile time;
        //     this is what actually CREATES the links in the first place.
        if (providerTitles.Count > 0)
        {
            var providerTitleMap = await _graphWriter
                .ResolveTitlesAsync(providerTitles, token).ConfigureAwait(false);
            if (providerTitleMap.Count > 0)
            {
                // Union with the mapping's CURRENT live title links: SyncSeriesTitlesAsync REPLACES
                // the set (tombstoning anything absent), so passing only the provider titles would
                // drop the source title.
                var liveTitleIds = await _contributorDb.SeriesTitles
                    .Where(mt => mt.SeriesId == mappingId
                        && mt.Version != VersionLogicalDelete
                        && mt.Version != VersionUploadedDelete)
                    .Select(mt => mt.TitleId)
                    .ToListAsync(token).ConfigureAwait(false);

                var desiredTitleIds = new HashSet<Guid>(liveTitleIds);
                desiredTitleIds.UnionWith(providerTitleMap.Values);
                await _graphWriter.SyncSeriesTitlesAsync(mappingId, desiredTitleIds, token).ConfigureAwait(false);
            }
        }

        // 5. Materialize Unmatched rows for enabled providers that ended with no link and no decision.
        //    A provider already having ANY active metadata row (matched or decided) must never get
        //    a second (provider, "") Unmatched row — the "Not matched" duplicate from the bug report.
        foreach (var p in providers)
        {
            // Provider declared it cannot search this series → settled as ignoredAlways by the
            // search gate; never materialize a competing Unmatched row for it.
            if (skippedProviders.Contains(p.ProviderType)) continue;

            var identity = ((int)p.ProviderType, string.Empty);
            if (desired.Contains(identity) || byIdentity.ContainsKey(identity)) continue;
            var hasAnyActive = existing.Any(m => m.ProviderId == (int)p.ProviderType
                && m.Version != VersionLogicalDelete
                && m.Version != VersionUploadedDelete);
            if (hasAnyActive) continue;
            var hasDecision = existing.Any(m => m.ProviderId == (int)p.ProviderType
                && m.MappingStatus is SeriesMappingStatus.Blocked
                    or SeriesMappingStatus.ManualIgnore
                    or SeriesMappingStatus.ForeverIgnored
                    or SeriesMappingStatus.UserConfirmed
                    or SeriesMappingStatus.AutoMatched);
            if (hasDecision || match.Links.Any(l => l.Provider == p.ProviderType)) continue;

            _contributorDb.Metadata.Add(new ContributionMetadataEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = mappingId,
                ProviderId = (int)p.ProviderType,
                ProviderKey = string.Empty,
                MappingStatus = SeriesMappingStatus.Unmatched,
                LinkedDate = null,
                Version = VersionAddOrUpdate
            });
        }

        // 6. Remove rows whose (provider,key) is no longer desired and not a preserved decision.
        //    AutoMatched rows with a real provider key are preserved too: a link that was matched
        //    by a previous scan (or promoted from the External Mappings page) must not be dropped
        //    just because this pass didn't re-confirm it — otherwise the match would silently
        //    revert to Unmatched on the Contribution Mappings page.
        //
        //    An empty-key Unmatched row is the PERSISTENT "not matched" state (what the page renders
        //    as a "Not matched" provider row). `desired` only ever holds REAL link ids, so without
        //    this guard every scan removed the still-unmatched providers' rows → they vanished from
        //    the UI. Keep such a row alive while the provider is still enabled and was NOT linked
        //    this pass; remove it when a link supersedes it (the keyed AutoMatched row) or the
        //    provider is gone.
        //
        //    VERSION-AWARE removal: a tombstone (-1) means "delete this row in the cloud", so it is
        //    only meaningful for a row that ALREADY exists in the cloud (Version == -2, Uploaded).
        //    A never-uploaded row (Version == 0) has no cloud copy, so exporting a delete for it is
        //    a no-op and only churns the local DB — hard-delete it locally (no tombstone, no export).
        foreach (var row in existing)
        {
            var identity = (row.ProviderId, row.ProviderKey ?? string.Empty);
            var isDecision = row.MappingStatus is SeriesMappingStatus.Blocked
                or SeriesMappingStatus.ManualIgnore
                or SeriesMappingStatus.ForeverIgnored
                or SeriesMappingStatus.UserConfirmed
                or SeriesMappingStatus.AutoMatched;

            var providerStillPresent = providers.Any(p => (int)p.ProviderType == row.ProviderId
                && !skippedProviders.Contains(p.ProviderType));
            var providerLinkedThisPass = match.Links.Any(l => (int)l.Provider == row.ProviderId);
            var keepUnmatched = row.MappingStatus == SeriesMappingStatus.Unmatched
                && string.IsNullOrEmpty(row.ProviderKey)
                && providerStillPresent
                && !providerLinkedThisPass;

            if (desired.Contains(identity) || isDecision || keepUnmatched) continue;
            if (!ContributionVersions.IsLive(row.Version)) continue; // already a tombstone (-1 / -3)

            if (row.Version == ContributionVersions.Uploaded)
            {
                // -2: the cloud HAS this row → tombstone so the delete propagates on the next export.
                _contributorDb.Metadata.Attach(row);
                row.Version = ContributionVersions.PendingDelete;
                _contributorDb.Entry(row).Property(x => x.Version).IsModified = true;
            }
            else
            {
                // 0: never uploaded → no cloud copy to delete; drop the row locally, no tombstone.
                _contributorDb.Metadata.Remove(row);
            }
        }

        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
        // B: enforce one live row per (provider, key) for this series (Blocked exempt).
        await ConsolidateProviderRowsForSeriesAsync(mappingId, token).ConfigureAwait(false);

        // C: enforce the SAME "link only what you can back" invariant the propagation and cloud
        //    import paths already enforce. Step 4.5 materializes every provider-vouched title into
        //    the title graph at Version 0; nothing trimmed that set, so an entry-less cloud fixture
        //    re-acquired an unbacked title association on every scan and the next startup reconcile
        //    retired it again — a non-convergent loop. Reconciling here keeps exactly the titles a
        //    live source entry or an Auto/User ProviderTitleIds row vouches for, so the scan can no
        //    longer re-create what the reconcile removes.
        await _titleHeuristics.ReconcileSeriesAsync(mappingId, token).ConfigureAwait(false);
        return match;
    }

    /// <summary>
    /// Confirms a manual mapping for a contribution metadata row (upsert, Version = 0).
    /// Because the pre-existing Unmatched row (ProviderKey = "") for the same provider must not
    /// survive as an orphan, any OTHER row for this (mapping, provider) is tombstoned, so the
    /// confirmed (provider, externalId) row is the only active one.
    /// </summary>
    public async Task ConfirmAsync(Guid mappingId, ExternalSeriesProvider provider,
        string externalSeriesId, string? externalSeriesTitle = null, CancellationToken token = default)
    {
        var now = DateTime.UtcNow;
        var allForProvider = await _contributorDb.Metadata
            .Where(m => m.SeriesId == mappingId && m.ProviderId == (int)provider)
            .ToListAsync(token);

        // The row we will turn into the confirmed match: prefer an existing row with the
        // SAME external id (idempotent re-confirm), else any active row (rename), else create.
        var existing = allForProvider.FirstOrDefault(m => m.ProviderKey == externalSeriesId)
            ?? allForProvider.FirstOrDefault(m => m.Version != VersionLogicalDelete
                && m.Version != VersionUploadedDelete);

        // Resolve the provider cover now, so the user-confirmed row gets one immediately (the
        // same cover the scan path fills). Best-effort: a provider without a detail endpoint,
        // or an unreachable one, simply leaves the row cover-less (the startup backfill retries).
        string? coverUrl = await TryFetchCoverAsync(provider, externalSeriesId, token).ConfigureAwait(false);

        // The confirmed title(s) this provider link vouches for; the reconcile keeps exactly these.
        var providerTitleIds = ContributionTitleHeuristicsService.DeriveProviderTitleIds(externalSeriesTitle, null);

        if (existing != null)
        {
            // Blocked is id-level: matching a DIFFERENT id is allowed.
            if (existing.MappingStatus == SeriesMappingStatus.Blocked &&
                string.Equals(existing.ProviderKey, externalSeriesId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Cannot match mapping {mappingId} to the blocked id '{externalSeriesId}' on {provider}.");
            }
            existing.ProviderKey = externalSeriesId;
            existing.MappingStatus = SeriesMappingStatus.UserConfirmed;
            existing.LinkedDate = now;
            existing.Version = VersionAddOrUpdate;
            _contributorDb.Entry(existing).Property(x => x.ProviderKey).IsModified = true;
            _contributorDb.Entry(existing).Property(x => x.MappingStatus).IsModified = true;
            _contributorDb.Entry(existing).Property(x => x.LinkedDate).IsModified = true;
            _contributorDb.Entry(existing).Property(x => x.Version).IsModified = true;

            // Refresh the LOCAL cover for the possibly-new external id. CoverUrl is [JsonIgnore]
            // (never exported), so this must NOT touch Version.
            if (!string.IsNullOrWhiteSpace(coverUrl) && existing.CoverUrl != coverUrl)
            {
                existing.CoverUrl = coverUrl;
                _contributorDb.Entry(existing).Property(x => x.CoverUrl).IsModified = true;
            }

            // The confirmed title is what this link vouches for; only overwrite when we actually
            // have one (an empty confirm must not wipe a previously persisted set).
            if (providerTitleIds.Count > 0 && !existing.ProviderTitleIds.SequenceEqual(providerTitleIds))
            {
                existing.ProviderTitleIds = providerTitleIds;
                _contributorDb.Entry(existing).Property(x => x.ProviderTitleIds).IsModified = true;
            }
        }
        else
        {
            existing = new ContributionMetadataEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = mappingId,
                ProviderId = (int)provider,
                ProviderKey = externalSeriesId,
                MappingStatus = SeriesMappingStatus.UserConfirmed,
                LinkedDate = now,
                CoverUrl = coverUrl,
                ProviderTitleIds = providerTitleIds,
                Version = VersionAddOrUpdate
            };
            _contributorDb.Metadata.Add(existing);
        }

        // Retire any OTHER rows for this provider (most commonly the Unmatched (ProviderKey="")
        // row materialized by a scan) so only the confirmed row stays active.
        foreach (var row in allForProvider)
        {
            if (row == existing || row.Version == VersionLogicalDelete || row.Version == VersionUploadedDelete)
                continue;
            ContributionRowLifecycle.Retire(_contributorDb, row);
        }

        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort provider detail fetch used to obtain the cover for a just-confirmed mapping.
    /// Returns null when the provider has no detail endpoint, is unavailable, or reports no cover.
    /// </summary>
    private async Task<string?> TryFetchCoverAsync(
        ExternalSeriesProvider provider, string externalSeriesId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(externalSeriesId))
            return null;
        try
        {
            var instance = _providerFactory.GetProvider(provider);
            if (instance == null)
                return null;
            var detail = await instance.FetchSeriesMetadataAsync(externalSeriesId, token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(detail?.CoverUrl) ? null : detail!.CoverUrl;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cover fetch failed for {Provider} id {Id}", provider, externalSeriesId);
            return null;
        }
    }

    /// <summary>
    /// Blocks a SPECIFIC provider identity (<paramref name="providerKey"/>). The block is
    /// id-scoped: it only forbids auto-matching that exact key; the provider stays searchable for
    /// a DIFFERENT key. The blocked row RETAINS its provider key. Multiple keys can be blocked for
    /// the same provider (each its own row). A hard (empty-key) block is NEVER created.
    /// </summary>
    public async Task BlockAsync(Guid mappingId, ExternalSeriesProvider provider, string providerKey,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
            throw new InvalidOperationException("A block requires a specific provider id to refuse.");

        var existing = await _contributorDb.Metadata
            .FirstOrDefaultAsync(m => m.SeriesId == mappingId && m.ProviderId == (int)provider
                && m.ProviderKey == providerKey
                && m.Version != VersionLogicalDelete && m.Version != VersionUploadedDelete, token)
            .ConfigureAwait(false);

        if (existing == null)
        {
            _contributorDb.Metadata.Add(new ContributionMetadataEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = mappingId,
                ProviderId = (int)provider,
                ProviderKey = providerKey,
                MappingStatus = SeriesMappingStatus.Blocked,
                LinkedDate = DateTime.UtcNow,
                Version = VersionAddOrUpdate
            });
        }
        else
        {
            existing.MappingStatus = SeriesMappingStatus.Blocked;
            existing.LinkedDate ??= DateTime.UtcNow;
            existing.Version = VersionAddOrUpdate;
            _contributorDb.Entry(existing).Property(x => x.MappingStatus).IsModified = true;
            _contributorDb.Entry(existing).Property(x => x.Version).IsModified = true;
        }

        // Collapse only EXACT same-key duplicates (a redundant second row for the same id). Other
        // keys — including a live link on a different id — are independent and left untouched.
        var duplicates = await _contributorDb.Metadata
            .Where(m => m.SeriesId == mappingId && m.ProviderId == (int)provider
                && m.ProviderKey == providerKey && m.Id != (existing != null ? existing.Id : Guid.Empty)
                && m.Version != VersionLogicalDelete && m.Version != VersionUploadedDelete)
            .ToListAsync(token).ConfigureAwait(false);
        foreach (var row in duplicates)
        {
            ContributionRowLifecycle.Retire(_contributorDb, row);
        }

        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);

        // The block is id-scoped: the provider stays searchable for a DIFFERENT id. Surface it as a
        // "Not matched" placeholder (unless it still holds a live link on another key) so the user
        // can search/scan it again.
        await EnsureUnmatchedPlaceholderAsync(mappingId, provider, token).ConfigureAwait(false);

        // A block invalidates the titles that were only linked because of the blocked provider
        // id; drop those associations (Auto/User + source titles are kept).
        await _titleHeuristics.ReconcileSeriesAsync(mappingId, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears a refusal on a provider identity and returns it to a single Unmatched placeholder.
    /// Handles an id-scoped <see cref="SeriesMappingStatus.Blocked"/> row (matched by
    /// <paramref name="providerKey"/>), and also a provider-level
    /// <see cref="SeriesMappingStatus.ManualIgnore"/> / <see cref="SeriesMappingStatus.ForeverIgnored"/>
    /// row (un-ignore). Unmatched is provider-level, so the key is cleared and every Unmatched row
    /// for the provider collapses to exactly ONE.
    /// </summary>
    public async Task UnblockAsync(Guid mappingId, ExternalSeriesProvider provider, string providerKey,
        CancellationToken token = default)
    {
        var refusals = await _contributorDb.Metadata
            .Where(m => m.SeriesId == mappingId && m.ProviderId == (int)provider
                && (m.MappingStatus == SeriesMappingStatus.Blocked
                    || m.MappingStatus == SeriesMappingStatus.ManualIgnore
                    || m.MappingStatus == SeriesMappingStatus.ForeverIgnored)
                && m.Version != VersionLogicalDelete && m.Version != VersionUploadedDelete)
            .ToListAsync(token).ConfigureAwait(false);

        // Prefer the row matching the supplied key (an id block); otherwise fall back to a
        // provider-level refusal (ignore rows, or a legacy keyless block).
        var target = (!string.IsNullOrWhiteSpace(providerKey)
                ? refusals.FirstOrDefault(m => string.Equals(m.ProviderKey, providerKey, StringComparison.Ordinal))
                : null)
            ?? refusals.FirstOrDefault(m => string.IsNullOrWhiteSpace(m.ProviderKey))
            ?? refusals.FirstOrDefault();
        if (target == null) return;

        target.ProviderKey = string.Empty;
        target.MappingStatus = SeriesMappingStatus.Unmatched;
        target.LinkedDate = DateTime.UtcNow;
        target.Version = VersionAddOrUpdate;
        _contributorDb.Entry(target).Property(x => x.ProviderKey).IsModified = true;
        _contributorDb.Entry(target).Property(x => x.MappingStatus).IsModified = true;
        _contributorDb.Entry(target).Property(x => x.LinkedDate).IsModified = true;
        _contributorDb.Entry(target).Property(x => x.Version).IsModified = true;

        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);

        // Collapse any other Unmatched rows for this provider — exactly ONE is required.
        await ConsolidateUnmatchedProviderAsync(mappingId, provider, token).ConfigureAwait(false);
    }

    /// <summary>Applies ManualIgnore or ForeverIgnored to a contribution metadata row.</summary>
    public async Task IgnoreAsync(Guid mappingId, ExternalSeriesProvider provider, bool forever, CancellationToken token = default)
    {
        var status = forever ? SeriesMappingStatus.ForeverIgnored : SeriesMappingStatus.ManualIgnore;
        var existing = await GetRowAsync(mappingId, provider, string.Empty, token);
        if (existing != null)
        {
            existing.MappingStatus = status;
            existing.LinkedDate = DateTime.UtcNow;
            existing.Version = VersionAddOrUpdate;
        }
        else
        {
            _contributorDb.Metadata.Add(new ContributionMetadataEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = mappingId,
                ProviderId = (int)provider,
                ProviderKey = string.Empty,
                MappingStatus = status,
                LinkedDate = DateTime.UtcNow,
                Version = VersionAddOrUpdate
            });
        }
        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks every Not Matched (Unmatched) provider of a contribution mapping as ManualIgnore
    /// ("Ignore All" = manual ignore, NOT ignore-always). Providers already carrying a decision
    /// (auto/user matched, blocked, ManualIgnore, or ForeverIgnored) are left untouched;
    /// providers with no metadata row are materialized as ManualIgnore. Returns the list of
    /// providers that were actually ignored (empty when none).
    /// </summary>
    public async Task<List<ExternalSeriesProvider>> IgnoreAllUnmatchedAsync(
        Guid mappingId, List<IExternalSeriesProvider> providers, CancellationToken token = default)
    {
        // Live rows only — a tombstoned Unmatched row is already gone.
        var existing = await _contributorDb.Metadata
            .Where(m => m.SeriesId == mappingId
                && m.Version != VersionLogicalDelete && m.Version != VersionUploadedDelete)
            .ToListAsync(token).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var ignored = new List<ExternalSeriesProvider>();
        var handled = new HashSet<int>();

        // Primary: ignore EVERY live Unmatched row (a provider may hold more than one). Previously
        // this inspected only the FIRST row per enabled provider, so a duplicate or a stale row
        // shadowed the real Unmatched one and nothing was ignored.
        foreach (var grp in existing.Where(m => m.MappingStatus == SeriesMappingStatus.Unmatched)
                     .GroupBy(m => m.ProviderId))
        {
            foreach (var row in grp)
            {
                row.MappingStatus = SeriesMappingStatus.ManualIgnore;
                row.LinkedDate = now;
                row.Version = VersionAddOrUpdate;
                _contributorDb.Entry(row).Property(x => x.MappingStatus).IsModified = true;
                _contributorDb.Entry(row).Property(x => x.LinkedDate).IsModified = true;
                _contributorDb.Entry(row).Property(x => x.Version).IsModified = true;
            }
            handled.Add(grp.Key);
            ignored.Add((ExternalSeriesProvider)grp.Key);
        }

        // Secondary: enabled providers with NO row at all never matched → materialize ManualIgnore
        // (same semantics as the per-provider manual ignore). Providers that already have a
        // (non-Unmatched) row are left untouched.
        foreach (var p in providers)
        {
            if (handled.Contains((int)p.ProviderType)) continue;
            if (existing.Any(m => m.ProviderId == (int)p.ProviderType)) continue;

            _contributorDb.Metadata.Add(new ContributionMetadataEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = mappingId,
                ProviderId = (int)p.ProviderType,
                ProviderKey = string.Empty,
                MappingStatus = SeriesMappingStatus.ManualIgnore,
                LinkedDate = now,
                Version = VersionAddOrUpdate
            });
            ignored.Add(p.ProviderType);
        }

        if (ignored.Count > 0)
        {
            await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
        }
        return ignored;
    }

    /// <summary>Removes a contribution metadata row (tombstoned for replication).</summary>
    public async Task UnlinkAsync(Guid mappingId, ExternalSeriesProvider provider, CancellationToken token = default)
    {
        // Retire EVERY live non-Blocked row for this (series, provider): the decision row AND any
        // active keyed link. Blocked rows are exempt and left in place. Cloud-backed rows are
        // tombstoned; never-exported rows are deleted locally.
        var rows = await _contributorDb.Metadata
            .Where(m => m.SeriesId == mappingId && m.ProviderId == (int)provider
                && m.MappingStatus != SeriesMappingStatus.Blocked
                && m.Version != VersionLogicalDelete && m.Version != VersionUploadedDelete)
            .ToListAsync(token).ConfigureAwait(false);
        foreach (var row in rows)
        {
            ContributionRowLifecycle.Retire(_contributorDb, row);
        }
        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the contribution mapping ids that own any of the supplied titles.
    /// Used by the seed sweep to scope matching to just the series it collected,
    /// instead of rescanning the whole contribution graph.
    /// </summary>
    public async Task<List<Guid>> FindMappingIdsByTitlesAsync(IEnumerable<string> titles, CancellationToken token = default)
    {
        var wanted = titles
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count == 0)
            return [];

        return await _contributorDb.SeriesTitles
            .Include(mt => mt.Title)
            .Where(mt => mt.Title != null && wanted.Contains(mt.Title.Title!))
            .Select(mt => mt.SeriesId)
            .Distinct()
            .ToListAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the subset of <paramref name="mappingIds"/> that do NOT have a live metadata row for
    /// every <paramref name="expectedProviders"/> entry — i.e. mappings with a "missing mapping"
    /// (at least one expected provider has no live row at all, including mappings with no rows).
    ///
    /// Used by the seed sweep's gated (non-<c>--force</c>) mode: a series already covered by a live
    /// row for every expected provider is NOT eligible for another metadata match. Any live row
    /// counts as coverage regardless of its <see cref="SeriesMappingStatus"/> (an Unmatched
    /// placeholder is still a mapping). Tombstoned rows (PendingDelete / UploadedDelete) never count.
    /// When <paramref name="expectedProviders"/> is empty, nothing is "missing" and the result is empty.
    /// </summary>
    public async Task<HashSet<Guid>> FindMappingIdsMissingProviderCoverageAsync(
        IReadOnlyCollection<Guid> mappingIds,
        IReadOnlyCollection<ExternalSeriesProvider> expectedProviders,
        CancellationToken token = default)
    {
        if (mappingIds.Count == 0 || expectedProviders.Count == 0)
            return [];

        var ids = mappingIds.Distinct().ToList();
        var expected = expectedProviders.Distinct().Select(p => (int)p).ToHashSet();

        var rows = await _contributorDb.Metadata
            .Where(m => ids.Contains(m.SeriesId)
                && m.Version != VersionLogicalDelete
                && m.Version != VersionUploadedDelete)
            .Select(m => new { m.SeriesId, m.ProviderId })
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        // Providers present per mapping (an Unmatched placeholder still counts as a row).
        var coveredByMapping = new Dictionary<Guid, HashSet<int>>();
        foreach (var row in rows)
        {
            if (!coveredByMapping.TryGetValue(row.SeriesId, out var set))
                coveredByMapping[row.SeriesId] = set = [];
            set.Add(row.ProviderId);
        }

        var missing = new HashSet<Guid>();
        foreach (var id in ids)
        {
            // A mapping with NO rows at all is missing coverage; one is covered only when every
            // expected provider owns at least one live row.
            if (!coveredByMapping.TryGetValue(id, out var present) || !expected.IsSubsetOf(present))
                missing.Add(id);
        }

        return missing;
    }

    /// <summary>
    /// Enumerates the series that still need a metadata pass: every live series entry whose DERIVED
    /// <see cref="ContributionSeriesEntity.Metadata"/> flag is <c>false</c>, grouped by series id and
    /// paired with the distinct source PACKAGES its entries belong to (resolved through
    /// <c>SeriesEntries → Sources</c>).
    ///
    /// Used by the two-phase sweep's metadata pass: the packages let the caller resolve which
    /// metadata providers are allowed for each candidate series (per-package content-type gate) and
    /// EXCLUDE series whose packages allow none, so the pass always converges. The candidate set is
    /// recomputed after every pass because a join moves entries onto the survivor series and the
    /// flag recompute then settles each group.
    /// </summary>
    public async Task<Dictionary<Guid, HashSet<string>>> FindSeriesPendingMetadataAsync(
        CancellationToken token = default)
    {
        var rows = await _contributorDb.SeriesEntries
            .Where(e => !e.Metadata
                && e.Version != VersionLogicalDelete
                && e.Version != VersionUploadedDelete)
            .SelectMany(
                e => _contributorDb.SourceSeries
                    .Where(ss => ss.SeriesEntityId == e.Id
                        && ss.Version != VersionLogicalDelete
                        && ss.Version != VersionUploadedDelete),
                (e, ss) => new { e.SeriesId, ss.Source!.Package })
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        var result = new Dictionary<Guid, HashSet<string>>();
        foreach (var row in rows)
        {
            if (!result.TryGetValue(row.SeriesId, out var packages))
                result[row.SeriesId] = packages = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(row.Package))
                packages.Add(row.Package);
        }
        return result;
    }

    /// <summary>
    /// Scoped variant of <see cref="ScanAllMappingsAsync"/>: matches only the supplied
    /// mapping ids. Progress is still published under the same aggregate job id so the
    /// UI shows a coherent run.
    /// </summary>
    public async Task ScanMappingsAsync(
        IReadOnlyCollection<Guid> mappingIds, List<IExternalSeriesProvider> providers, CancellationToken token = default)
    {
        var total = mappingIds.Count;
        if (total == 0)
            return;

        if (_hubReport != null)
        {
            await _hubReport.ReportProgressAsync(new ProgressState
            {
                Id = LinkAllJobId,
                JobType = JobType.MetadataLink,
                ProgressStatus = ProgressStatus.Started,
                Percentage = 0,
                Message = $"Linking 0/{total} mappings"
            });
        }

        // BATCH PERFORMANCE: same rationale as ScanAllMappingsAsync — suppress the per-call
        // whole-tracker DetectChanges for the loop; SaveChanges forces one explicit pass.
        using var autoDetect = _contributorDb.SuppressAutoDetectChanges();

        var processed = 0;
        foreach (var id in mappingIds)
        {
            if (token.IsCancellationRequested) break;

            await ScanMappingAsync(id, providers, token).ConfigureAwait(false);
            processed++;

            // Bound the identity map per mapping (each scan persists its own writes first).
            _contributorDb.ChangeTracker.Clear();

            if (_hubReport != null)
            {
                var pct = processed * 100 / total;
                await _hubReport.ReportProgressAsync(new ProgressState
                {
                    Id = LinkAllJobId,
                    JobType = JobType.MetadataLink,
                    ProgressStatus = ProgressStatus.InProgress,
                    Percentage = pct,
                    Message = $"Linking {processed}/{total} mappings"
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
                Message = $"Linked {processed}/{total} mappings"
            });
        }

    }

    /// <summary>Background scan across all mappings, publishing per-mapping + aggregate SignalR progress.</summary>
    public async Task ScanAllMappingsAsync(List<IExternalSeriesProvider> providers, CancellationToken token = default)
    {
        // Only series that OWN live content are (re)scanned. A series with NO live source entry
        // (e.g. a read-only cloud fixture whose records were never imported locally) has nothing
        // the scan can vouch for: its titles were materialized by an earlier cloud apply, so
        // re-scanning it only re-acquires associations that no entry/Auto-User row backs — exactly
        // the unbacked links the title reconcile then retires on the next startup. Excluding them
        // removes that churn at the source.
        var mappingIds = await _contributorDb.SeriesTitles
            .Where(mt => mt.Version != VersionLogicalDelete
                && mt.Version != VersionUploadedDelete)
            .Select(mt => mt.SeriesId)
            .Distinct()
            .Where(id => _contributorDb.SeriesEntries.Any(s => s.SeriesId == id
                && s.Version != VersionLogicalDelete
                && s.Version != VersionUploadedDelete))
            .ToListAsync(token).ConfigureAwait(false);
        var total = mappingIds.Count;

        if (_hubReport != null)
        {
            await _hubReport.ReportProgressAsync(new ProgressState
            {
                Id = LinkAllJobId,
                JobType = JobType.MetadataLink,
                ProgressStatus = ProgressStatus.Started,
                Percentage = 0,
                Message = $"Linking 0/{total} mappings"
            });
        }

        // BATCH PERFORMANCE: with ONE scoped context across the whole scan, automatic change
        // detection (default on) re-scans every tracked entity before each of the many queries and
        // writes ScanMappingAsync performs, so per-mapping cost grows with mappings already done.
        // Suppress it for the loop; SaveChanges forces a single DetectChanges and every write path
        // sets IsModified explicitly (see ContributionDbContext.SuppressAutoDetectChanges).
        using var autoDetect = _contributorDb.SuppressAutoDetectChanges();

        var processed = 0;
        foreach (var id in mappingIds)
        {
            if (token.IsCancellationRequested) break;
            if (_hubReport != null)
            {
                await _hubReport.ReportProgressAsync(new ProgressState
                {
                    Id = $"contribution-link-{id}",
                    JobType = JobType.MetadataLink,
                    ProgressStatus = ProgressStatus.Started,
                    Percentage = 0,
                    Message = "Scanning contribution mapping…"
                });
            }

            var match = await ScanMappingAsync(id, providers, token).ConfigureAwait(false);
            processed++;

            if (_hubReport != null)
            {
                var pct = total > 0 ? (processed * 100 / total) : 100;
                await _hubReport.ReportProgressAsync(new ProgressState
                {
                    Id = $"contribution-link-{id}",
                    JobType = JobType.MetadataLink,
                    ProgressStatus = ProgressStatus.Completed,
                    Percentage = 100,
                    Message = $"{match.Links.Count} linked, {match.Suggestions.Count} suggested"
                });
                await _hubReport.ReportProgressAsync(new ProgressState
                {
                    Id = LinkAllJobId,
                    JobType = JobType.MetadataLink,
                    ProgressStatus = ProgressStatus.InProgress,
                    Percentage = pct,
                    Message = $"Linking {processed}/{total} mappings"
                });
            }

            // BOUND THE CHANGE TRACKER (root-cause fix): ScanMappingAsync persists all of its own
            // writes before returning, so the rows it tracked are no longer needed. This method
            // shares ONE scoped ContributionDbContext across the entire scan; without detaching
            // here the identity map accumulates a tracked ContributionMetadataEntity / SeriesTitleEntity
            // per row across every mapping. A later pass that reads the same row via AsNoTracking
            // then holds a DIFFERENT instance for the same key, and any Attach/Remove throws
            // "another instance with the same key value for {'Id'} is already being tracked".
            // Clearing per mapping keeps the identity map bounded to one mapping's work.
            _contributorDb.ChangeTracker.Clear();
        }

        if (_hubReport != null)
        {
            await _hubReport.ReportProgressAsync(new ProgressState
            {
                Id = LinkAllJobId,
                JobType = JobType.MetadataLink,
                ProgressStatus = ProgressStatus.Completed,
                Percentage = 100,
                Message = $"Linked {processed}/{total} mappings"
            });
        }
        await ReconcileSeriesGroupingAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Joins two or more contribution mappings (SeriesId) into ONE. The first live
    /// <paramref name="memberIds"/> entry becomes the target (keeping its SeriesId); every other
    /// member is folded into it:
    ///   * its <see cref="ContributionSeriesEntity"/> rows are re-pointed (SeriesId = target);
    ///   * its <see cref="SeriesTitleEntity"/> rows are merged into the target (duplicates — the
    ///     SAME TitleId — are tombstoned, not deleted);
    ///   * its <see cref="ContributionMetadataEntity"/> rows are migrated by (ProviderId, ProviderKey),
    ///     deduplicated (duplicates tombstoned, the strongest status wins).
    /// The absorbed member <see cref="SeriesEntity"/> rows are left orphaned (inert — the local
    /// Series table is not uploaded) so their tombstoned dependents are not cascade-deleted.
    /// Every tombstone / re-parent stamps Version = 0 (pending) or -1 (tombstone) so the normal
    /// upload sweep replicates the change. No automatic re-matching is run — the merged metadata
    /// carries over verbatim.
    /// Returns the target (surviving) SeriesId.
    /// </summary>
    public async Task<Guid> JoinSeriesAsync(IEnumerable<Guid> memberIds, CancellationToken token = default)
    {
        var distinct = memberIds.Distinct().ToList();
        if (distinct.Count < 2)
            throw new InvalidOperationException("Select at least two contributions to join.");

        // Target = the first member that has a live series row (falls back to the first id).
        var liveSeriesIds = await _contributorDb.SeriesEntries
            .Where(s => distinct.Contains(s.SeriesId) && s.Version != VersionLogicalDelete && s.Version != VersionUploadedDelete)
            .Select(s => s.SeriesId)
            .Distinct()
            .ToListAsync(token).ConfigureAwait(false);
        var targetId = distinct.FirstOrDefault(liveSeriesIds.Contains);
        if (targetId == Guid.Empty) targetId = distinct[0];

        return await MergeSeriesAsync(targetId, distinct, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Folds <paramref name="sourceIds"/> (excluding the target) into <paramref name="targetId"/>:
    /// re-parents source entries, unions title associations (dedup), migrates metadata rows on
    /// (ProviderId, ProviderKey) keeping the strongest status, and tombstones duplicates. The
    /// absorbed SeriesEntity rows are intentionally left in place (the local Series table is not
    /// uploaded; removing them would FK-cascade-delete the tombstones the removal relies on).
    /// Shared by the manual Join and the provider-identity auto-merge. Returns the target id.
    /// </summary>
    private async Task<Guid> MergeSeriesAsync(
        Guid targetId, IReadOnlyCollection<Guid> sourceIds, CancellationToken token, bool deferSettle = false)
    {
        var sources = sourceIds.Where(id => id != targetId).Distinct().ToList();
        if (sources.Count == 0)
            return targetId;

        // ── 1. Ensure the target SeriesEntity row exists. ──
        var targetExists = await _contributorDb.Series.AnyAsync(s => s.Id == targetId, token).ConfigureAwait(false);
        if (!targetExists)
            _contributorDb.Series.Add(new Models.ContributionDatabase.SeriesEntity { Id = targetId });

        // ── 2. Re-parent series entries. ──
        var seriesRows = await _contributorDb.SeriesEntries
            .Where(s => sources.Contains(s.SeriesId))
            .ToListAsync(token).ConfigureAwait(false);
        foreach (var row in seriesRows)
        {
            row.SeriesId = targetId;
            // A join re-parents the entries into a different mapping, so the mapping's verified
            // snapshot no longer holds: clear the date LOCALLY without stamping the row pending
            // (Version left as-is) so the cloud's sticky verified date is preserved.
            row.VerifiedDateUTC = null;
            // Conservative: a re-parented entry's derived Metadata flag is unknown until the
            // post-merge recompute settles the survivor group, so reset it to false here.
            row.Metadata = false;
            row.Version = VersionAddOrUpdate;
            _contributorDb.Entry(row).Property(s => s.VerifiedDateUTC).IsModified = true;
            _contributorDb.Entry(row).Property(s => s.Metadata).IsModified = true;
        }

        // ── 3. Merge title associations, tombstoning duplicates. ──
        var targetTitles = await _contributorDb.SeriesTitles
            .Where(mt => mt.SeriesId == targetId)
            .ToListAsync(token).ConfigureAwait(false);
        var targetTitleIds = targetTitles
            .Where(mt => ContributionVersions.IsLive(mt.Version))
            .Select(mt => mt.TitleId)
            .ToHashSet();

        var memberTitles = await _contributorDb.SeriesTitles
            .Where(mt => sources.Contains(mt.SeriesId))
            .ToListAsync(token).ConfigureAwait(false);
        // SeriesId is part of the SeriesTitleEntity composite key, so it can NEVER be mutated:
        // tombstone the member-series association and ADD (or resurrect) a target-series one.
        var targetTitleRowsByTitle = targetTitles.ToDictionary(mt => mt.TitleId);
        foreach (var link in memberTitles)
        {
            if (!ContributionVersions.IsLive(link.Version)) continue;

            if (targetTitleIds.Add(link.TitleId))
            {
                if (targetTitleRowsByTitle.TryGetValue(link.TitleId, out var existingTargetRow))
                {
                    // Resurrect a tombstoned (target, title) association.
                    _contributorDb.SeriesTitles.Attach(existingTargetRow);
                    existingTargetRow.Version = VersionAddOrUpdate;
                    _contributorDb.Entry(existingTargetRow).Property(x => x.Version).IsModified = true;
                }
                else
                {
                    _contributorDb.SeriesTitles.Add(new SeriesTitleEntity
                    {
                        SeriesId = targetId,
                        TitleId = link.TitleId,
                        Version = VersionAddOrUpdate
                    });
                }
            }

            // Retire the member-series association (key untouched): a cloud-backed row (-2) is
            // tombstoned so the merge's delete propagates; a never-exported row (0) is deleted
            // locally (no cloud copy to inform).
            ContributionRowLifecycle.Retire(_contributorDb, link);
        }

        // ── 4. Migrate metadata links, deduplicating on (ProviderId, ProviderKey). ──
        var metadataRows = await _contributorDb.Metadata
            .Where(m => sources.Contains(m.SeriesId))
            .ToListAsync(token).ConfigureAwait(false);
        var targetMetadata = await _contributorDb.Metadata
            .Where(m => m.SeriesId == targetId)
            .ToListAsync(token).ConfigureAwait(false);
        var metadataByKey = new Dictionary<(int ProviderId, string Key), ContributionMetadataEntity>();
        foreach (var m in targetMetadata)
        {
            if (!ContributionVersions.IsLive(m.Version)) continue;
            metadataByKey.TryAdd((m.ProviderId, m.ProviderKey ?? string.Empty), m);
        }

        foreach (var row in metadataRows)
        {
            if (!ContributionVersions.IsLive(row.Version)) continue;
            var key = (row.ProviderId, row.ProviderKey ?? string.Empty);
            if (metadataByKey.TryGetValue(key, out var existing))
            {
                // Duplicate decision → keep the strongest intent, drop this row.
                if (StatusPriority(row.MappingStatus) > StatusPriority(existing.MappingStatus))
                {
                    existing.MappingStatus = row.MappingStatus;
                    existing.LinkedDate = row.LinkedDate;
                    existing.Version = VersionAddOrUpdate;
                }
                row.Version = VersionLogicalDelete;
            }
            else
            {
                row.SeriesId = targetId;
                row.Version = VersionAddOrUpdate;
                metadataByKey[key] = row;
            }
        }

        // ── 5. The orphaned member SeriesEntity rows are intentionally LEFT in place. ──
        // The local Series table is a purely local aggregate root: it carries no Version column
        // and is NOT part of the upload snapshot (the cloud derives mappings from mapping_titles).
        // Physically removing it would make EF CASCADE-delete the members' tombstoned SeriesTitle /
        // Metadata dependents (FK OnDelete.Cascade), which would erase the -1 tombstones needed for
        // the removal to replicate. Orphaned rows are inert (grouping/scanning key off SeriesTitles).

        if (deferSettle)
        {
            // Bulk grouping reconcile: the caller batches ONE SaveChanges and a graph-wide
            // consolidate / flag recompute for the whole pass. Merges within a pass target
            // DISJOINT components (each entry belongs to exactly one component), so leaving the
            // changes tracked and unsaved until the pass boundary is safe — and it removes the
            // per-merge SQLite commit (an fsync) that dominated a large reconcile (thousands of
            // merges → thousands of commits).
            return targetId;
        }

        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);

        // Unify the merged provider rows: exact-duplicate collapse + provider-level Unmatched
        // suppression + one-Unmatched-per-provider. Without this the absorbed mapping's Unmatched
        // ("") rows survive alongside the target's keyed Auto/User rows (different key → not
        // deduped), so the joined mapping shows the same provider twice.
        await ConsolidateProviderRowsForSeriesAsync(targetId, token).ConfigureAwait(false);

        // Settle the merged mapping's derived Metadata flag (true iff the survivor now owns a live
        // metadata row) so a join never leaves a stale flag behind.
        await _graphRepair.RecomputeSeriesMetadataFlagsAsync(new[] { targetId }, token).ConfigureAwait(false);

        _logger.LogInformation("Merged {Count} contribution mapping(s) into {TargetId}", sources.Count + 1, targetId);
        return targetId;
    }

    /// <summary>
    /// Splits a contribution mapping (SeriesId) that carries more than one source entry
    /// (<see cref="ContributionSeriesEntity"/>) into one mapping per source entry.
    ///   * Every existing <see cref="SeriesTitleEntity"/> row of the original series and every
    ///     <see cref="ContributionMetadataEntity"/> row is tombstoned (title/meta links cleared).
    ///   * One NEW <see cref="SeriesEntity"/> is minted per source entry and each
    ///     <see cref="ContributionSeriesEntity"/> is re-parented to its own new series id.
    ///   * A single <see cref="SeriesTitleEntity"/> link is created per new series, mapping the
    ///     entry's own <c>Data.TitleId</c> to the new SeriesId.
    ///   * NO automatic matching is performed — the user re-matches each new series manually.
    ///   * The original <see cref="SeriesEntity"/> row is left orphaned (inert — the local Series
    ///     table is not uploaded) so the tombstoned title/metadata rows are not cascade-deleted.
    /// Returns the newly created series ids.
    /// </summary>
    public async Task<List<Guid>> SplitSeriesAsync(Guid mappingId, CancellationToken token = default)
    {
        var entries = await _contributorDb.SeriesEntries
            .Where(s => s.SeriesId == mappingId
                && s.Version != VersionLogicalDelete
                && s.Version != VersionUploadedDelete)
            .ToListAsync(token).ConfigureAwait(false);
        if (entries.Count < 2)
            throw new InvalidOperationException("Only a contribution with more than one source can be split.");

        // One group per entry (manual split). The reconciliation passes component-derived groups.
        var groups = entries.Select(e => new List<Guid> { e.Id }).ToList();
        return await SplitSeriesByGroupsAsync(mappingId, groups, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Splits <paramref name="seriesId"/> into one new series per group of source entries: every
    /// existing title/metadata row of the original series is tombstoned, each group's entries are
    /// re-parented to a fresh SeriesEntity, and each entry's own-title link is re-created. The
    /// original SeriesEntity row is left orphaned (inert; the local Series table is not uploaded).
    /// Shared by the manual Split (one group per entry) and the provider-identity reconciliation.
    /// Returns the new series ids.
    /// </summary>
    private async Task<List<Guid>> SplitSeriesByGroupsAsync(
        Guid seriesId, IReadOnlyList<List<Guid>> entryGroups, CancellationToken token)
    {
        var entries = await _contributorDb.SeriesEntries
            .Where(s => s.SeriesId == seriesId
                && s.Version != VersionLogicalDelete
                && s.Version != VersionUploadedDelete)
            .ToListAsync(token).ConfigureAwait(false);
        var byId = entries.ToDictionary(e => e.Id);

        // ── 1. Retire every existing title association (all associated series titles cleared).
        //    A cloud-backed row (-2) is tombstoned so the split's deletes propagate; a
        //    never-exported row (0) is deleted locally. ──
        var titleLinks = await _contributorDb.SeriesTitles
            .Where(mt => mt.SeriesId == seriesId)
            .ToListAsync(token).ConfigureAwait(false);
        foreach (var link in titleLinks)
        {
            if (!ContributionVersions.IsLive(link.Version)) continue;
            ContributionRowLifecycle.Retire(_contributorDb, link);
        }

        // ── 2. Retire every existing metadata row (all associated metadata cleared). Cloud-backed
        //    rows (-2) are tombstoned; never-exported rows (0) are deleted locally. ──
        var metadataRows = await _contributorDb.Metadata
            .Where(m => m.SeriesId == seriesId)
            .ToListAsync(token).ConfigureAwait(false);
        foreach (var row in metadataRows)
        {
            if (!ContributionVersions.IsLive(row.Version)) continue;
            ContributionRowLifecycle.Retire(_contributorDb, row);
        }

        // ── 3. Mint one new series per group and re-parent that group's entries. ──
        var newSeriesIds = new List<Guid>(entryGroups.Count);
        foreach (var group in entryGroups)
        {
            if (group.Count == 0) continue;

            var newSeriesId = Guid.NewGuid();
            newSeriesIds.Add(newSeriesId);
            _contributorDb.Series.Add(new Models.ContributionDatabase.SeriesEntity { Id = newSeriesId });

            // (SeriesId, TitleId) is the SeriesTitleEntity composite key, so two entries in the SAME
            // group that share their own title id (the very reason they were unioned into one
            // component) would otherwise Add the identical key twice and make EF throw
            // "another instance with the same key value ... is already being tracked". Track the
            // ids already handled for this new series and skip duplicates.
            var linkedTitleIds = new HashSet<Guid>();
            foreach (var entryId in group)
            {
                if (!byId.TryGetValue(entryId, out var entry)) continue;
                entry.SeriesId = newSeriesId;
                // A split re-parents the entry into a NEW mapping, so the verified snapshot no
                // longer holds: clear the date LOCALLY without stamping it pending (Version left
                // as-is) so the cloud's sticky verified date is preserved.
                entry.VerifiedDateUTC = null;
                // Conservative: a split-off entry has NO metadata of its own (the original series'
                // rows were retired above), so reset the derived flag to false.
                entry.Metadata = false;
                entry.Version = VersionAddOrUpdate;
                _contributorDb.Entry(entry).Property(s => s.VerifiedDateUTC).IsModified = true;
                _contributorDb.Entry(entry).Property(s => s.Metadata).IsModified = true;

                // Re-create the title link for the new series (its own entry title).
                var titleId = entry.Data?.TitleId ?? Guid.Empty;
                if (titleId == Guid.Empty) continue;
                if (!linkedTitleIds.Add(titleId)) continue;

                // Defensive: if a row for this (new series, title) is somehow already tracked,
                // resurrect it instead of adding a duplicate that would violate the composite key.
                var tracked = _contributorDb.ChangeTracker.Entries<SeriesTitleEntity>()
                    .FirstOrDefault(e => e.Entity.SeriesId == newSeriesId && e.Entity.TitleId == titleId)
                    ?.Entity;
                if (tracked is not null)
                {
                    tracked.Version = VersionAddOrUpdate;
                    _contributorDb.Entry(tracked).Property(x => x.Version).IsModified = true;
                    continue;
                }

                _contributorDb.SeriesTitles.Add(new SeriesTitleEntity
                {
                    SeriesId = newSeriesId,
                    TitleId = titleId,
                    Version = VersionAddOrUpdate
                });
            }
        }

        // ── 4. The original SeriesEntity row is intentionally LEFT in place. ──
        // Removing it would make EF CASCADE-delete the tombstoned SeriesTitle / Metadata rows
        // (FK OnDelete.Cascade) above, erasing the -1 tombstones the removal relies on. The Series
        // table is local-only and not uploaded, so the orphan row is inert.

        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);

        // Each new series received its group's entries; unify their provider rows so a split never
        // leaves a provider showing both a link and a stale "Not matched" placeholder.
        foreach (var newSeriesId in newSeriesIds)
            await ConsolidateProviderRowsForSeriesAsync(newSeriesId, token).ConfigureAwait(false);

        // Settle the derived Metadata flag for the split-off series (false unless a consolidated
        // provider row survived) so a split leaves a consistent flag.
        if (newSeriesIds.Count > 0)
            await _graphRepair.RecomputeSeriesMetadataFlagsAsync(newSeriesIds, token).ConfigureAwait(false);

        _logger.LogInformation("Split contribution series {SeriesId} into {Count} group(s)", seriesId, newSeriesIds.Count);
        return newSeriesIds;
    }

    /// <summary>
    /// Marks EVERY live source entry of a contribution mapping as Verified (<c>VerifiedDateUTC</c> =
    /// now UTC) and stamps it pending (<c>Version = 0</c>) so the flag is replicated to the cloud.
    /// Returns the number of entries stamped.
    /// </summary>
    public async Task<int> VerifyMappingAsync(Guid mappingId, CancellationToken token = default)
    {
        var now = DateTime.UtcNow;
        var entries = await _contributorDb.SeriesEntries
            .Where(s => s.SeriesId == mappingId
                && s.Version != VersionLogicalDelete
                && s.Version != VersionUploadedDelete)
            .ToListAsync(token).ConfigureAwait(false);

        foreach (var entry in entries)
        {
            entry.VerifiedDateUTC = now;
            entry.Version = VersionAddOrUpdate;
            _contributorDb.Entry(entry).Property(s => s.VerifiedDateUTC).IsModified = true;
            _contributorDb.Entry(entry).Property(s => s.Version).IsModified = true;
        }

        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
        _logger.LogInformation("Verified contribution mapping {MappingId} ({Count} entries)", mappingId, entries.Count);
        return entries.Count;
    }

    /// <summary>
    /// Clears <c>VerifiedDateUTC</c> on every live source entry of a contribution mapping
    /// (Un-Verify). This is a LOCAL-only clear: the entry's <c>Version</c> is left UNCHANGED so the
    /// row is not marked pending and the cleared date is never re-uploaded — the cloud value is
    /// sticky and only moves on a non-null verify or a genuine data change. Returns the number of
    /// entries cleared.
    /// </summary>
    public async Task<int> UnverifyMappingAsync(Guid mappingId, CancellationToken token = default)
    {
        var entries = await _contributorDb.SeriesEntries
            .Where(s => s.SeriesId == mappingId
                && s.Version != VersionLogicalDelete
                && s.Version != VersionUploadedDelete)
            .ToListAsync(token).ConfigureAwait(false);

        foreach (var entry in entries)
        {
            entry.VerifiedDateUTC = null;
            _contributorDb.Entry(entry).Property(s => s.VerifiedDateUTC).IsModified = true;
        }

        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
        _logger.LogInformation("Un-verified contribution mapping {MappingId} ({Count} entries)", mappingId, entries.Count);
        return entries.Count;
    }

    /// <summary>
    /// Strength ranking used when two joined metadata rows collide on (provider, key):
    /// UserConfirmed > Blocked > AutoMatched > ForeverIgnored > ManualIgnore > Unmatched.
    /// Mirrors the worker's merge priority so a manual decision is never clobbered by a weaker one.
    /// </summary>
    private static int StatusPriority(SeriesMappingStatus status) => status switch
    {
        SeriesMappingStatus.UserConfirmed => 6,
        SeriesMappingStatus.Blocked => 5,
        SeriesMappingStatus.AutoMatched => 4,
        SeriesMappingStatus.ForeverIgnored => 3,
        SeriesMappingStatus.ManualIgnore => 2,
        _ => 1,
    };

    // ── Internals ──

    private async Task<ContributionMetadataEntity?> GetRowAsync(
        Guid mappingId, ExternalSeriesProvider provider, string? providerKey, CancellationToken token)
    {
        // providerKey == " targets the DECISION row (Unmatched / Ignore / Block). providerKey == null
 // targets the ACTIVE link (non-empty key), falling back to any non-Blocked live row.
 var rows = await _contributorDb.Metadata
 .Where(m => m.SeriesId == mappingId && m.ProviderId == (int)provider
 && m.Version != VersionLogicalDelete && m.Version != VersionUploadedDelete)
 .ToListAsync(token).ConfigureAwait(false);

 if (providerKey != null)
 return rows.FirstOrDefault(m => string.Equals(m.ProviderKey ?? string.Empty, providerKey, StringComparison.Ordinal));

 return rows.Where(m => m.MappingStatus != SeriesMappingStatus.Blocked)
 .OrderByDescending(m => !string.IsNullOrEmpty(m.ProviderKey))
 .ThenByDescending(m => StatusPriority(m.MappingStatus))
 .FirstOrDefault();
 }

    /// <summary>
    /// Resolves the genre list + category of the local (Rensaio) series that best matches this
    /// contribution mapping's titles, so <see cref="CanSearchSeries"/> can be evaluated. The
    /// contribution schema stores neither genre nor type — the local series is the only source.
    /// Returns ([], null) when no local series matches.
    /// </summary>
    private async Task<(List<string> Genres, string? Category)> ResolveSeriesGenreAsync(
        List<string> titleCandidates, CancellationToken token)
    {
        try
        {
            var candidates = titleCandidates.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (candidates.Count == 0) return ([], null);

            var matching = await _db.Series
                .Include(s => s.Sources)
                .AsNoTracking()
                .Where(s => candidates.Contains(s.Title) || s.Sources.Any(src => candidates.Contains(src.Title)))
                .ToListAsync(token).ConfigureAwait(false);
            if (matching.Count == 0) return ([], null);

            // Prefer the series whose PRIMARY title matches, then any partial source-title match.
            var best = matching
                .OrderByDescending(s => candidates.Contains(s.Title))
                .First();
            return (best.Genre ?? [], best.Type);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve genre/category for contribution mapping scan");
            return ([], null);
        }
    }

    /// <summary>
    /// Resolves the cover used as the LOCAL side of image (PDQ) matching for a contribution
    /// mapping. The contribution schema DOES carry a per-entry source cover
    /// (<see cref="ContributionRecordV2.ThumbnailUrl"/>, the "Source Image" shown on the page), so
    /// that is preferred. When no entry has one, it falls back to the local library cover the same
    /// way <see cref="ResolveSeriesGenreAsync"/> relates titles back to a <c>SeriesEntity</c>.
    /// Returns the cover URL and, when known, the owning Mihon provider id (fallback path only).
    /// </summary>
    private async Task<(string? CoverUrl, string? MihonProviderId)> ResolveLocalCoverAsync(
        Guid mappingId, List<string> titleCandidates, CancellationToken token)
    {
        // 1. Contribution DB first: the source image carried by the mapping's live entries.
        try
        {
            var entries = await _contributorDb.SeriesEntries
                .Include(se => se.Sources)
                    .ThenInclude(ss => ss.Source)
                .Where(se => se.SeriesId == mappingId
                    && se.Version != VersionLogicalDelete
                    && se.Version != VersionUploadedDelete)
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);

            var coverEntry = entries
                .FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Data?.ThumbnailUrl));
            if (coverEntry != null)
            {
                var sourceCover = coverEntry.Data!.ThumbnailUrl;
                // The Mihon provider id (package|sourceId) of the source that OWNS this cover, so
                // the image cache records it and the URL image provider can use the source fallback
                // for referer/header-protected hosts. Without it the cache row is created empty and
                // the source fallback is never attempted. Prefer a live association.
                var assoc = coverEntry.Sources
                    .Where(ss => ContributionVersions.IsLive(ss.Version))
                    .OrderBy(ss => ss.SourceId)
                    .FirstOrDefault();
                var providerId = assoc?.Source is null
                    ? null
                    : RensaioBackend.Extensions.PackageExtensions.BuildMihonProviderId(
                        assoc.Source.Package, assoc.Source.SourceId);
                _logger.LogInformation(
                    "Source cover resolved for image matching from the contribution DB ({Url}); mihon provider {Provider}",
                    sourceCover, providerId ?? "(none)");
                return (sourceCover, providerId);
            }
            _logger.LogInformation(
                "Mapping {MappingId} has no source cover in the contribution DB; trying the local library",
                mappingId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the contribution source cover for mapping {MappingId}", mappingId);
        }

        // 2. Fallback: a local library series sharing one of the mapping's titles.
        try
        {
            var candidates = titleCandidates.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (candidates.Count == 0) return (null, null);

            var matching = await _db.Series
                .Include(s => s.Sources)
                .AsNoTracking()
                .Where(s => candidates.Contains(s.Title) || s.Sources.Any(src => candidates.Contains(src.Title)))
                .ToListAsync(token).ConfigureAwait(false);
            if (matching.Count == 0) return (null, null);

            // Prefer a primary-title match that actually HAS a cover.
            var best = matching
                .OrderByDescending(s => candidates.Contains(s.Title))
                .ThenByDescending(s => !string.IsNullOrWhiteSpace(s.ThumbnailUrl))
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.ThumbnailUrl));
            if (best == null) return (null, null);

            var mihonProviderId = best.Sources?
                .FirstOrDefault(src => !string.IsNullOrWhiteSpace(src.MihonProviderId))?.MihonProviderId;
            _logger.LogInformation(
                "Local library cover resolved for image matching ({Url}); mihon provider {Provider}",
                best.ThumbnailUrl, mihonProviderId ?? "(none)");
            return (best.ThumbnailUrl, mihonProviderId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve local library cover for contribution mapping scan");
            return (null, null);
        }
    }

    /// <summary>
    /// True when the mapping already holds a settled provider-level decision for
    /// <paramref name="provider"/> (an active link, ForeverIgnored or ManualIgnore). A Blocked row
    /// is id-scoped and NOT a provider decision, so it does not count. The content-type gate uses
    /// this to avoid downgrading an existing user/auto decision.
    /// </summary>
    private static bool IsDecidedProvider(
        IReadOnlyList<ContributionMetadataEntity> rows, ExternalSeriesProvider provider)
        => rows.Any(m => m.ProviderId == (int)provider
            && m.MappingStatus is SeriesMappingStatus.AutoMatched
                or SeriesMappingStatus.UserConfirmed
                or SeriesMappingStatus.ForeverIgnored
                or SeriesMappingStatus.ManualIgnore);

    /// <summary>
    /// Settles a (mapping, provider) relation as ForeverIgnored because the provider declared it
    /// cannot search this series (CanSearchSeries == false). Never downgrades a stronger decision
    /// (UserConfirmed / Blocked / ForeverIgnored).
    /// </summary>
    private async Task EnsureProviderIgnoredAsync(Guid mappingId, ExternalSeriesProvider provider,
        DateTime now, CancellationToken token)
    {
        var row = await GetRowAsync(mappingId, provider, string.Empty, token).ConfigureAwait(false);
        if (row != null)
        {
            if (row.MappingStatus is SeriesMappingStatus.UserConfirmed
                or SeriesMappingStatus.Blocked
                or SeriesMappingStatus.ForeverIgnored)
                return; // stronger decision stands
            _contributorDb.Metadata.Attach(row);
            row.ProviderKey = string.Empty;
            row.MappingStatus = SeriesMappingStatus.ForeverIgnored;
            row.LinkedDate = now;
            row.Version = VersionAddOrUpdate;
            _contributorDb.Entry(row).Property(x => x.ProviderKey).IsModified = true;
            _contributorDb.Entry(row).Property(x => x.MappingStatus).IsModified = true;
            _contributorDb.Entry(row).Property(x => x.LinkedDate).IsModified = true;
            _contributorDb.Entry(row).Property(x => x.Version).IsModified = true;
        }
        else
        {
            _contributorDb.Metadata.Add(new ContributionMetadataEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = mappingId,
                ProviderId = (int)provider,
                ProviderKey = string.Empty,
                MappingStatus = SeriesMappingStatus.ForeverIgnored,
                LinkedDate = now,
                Version = VersionAddOrUpdate
            });
        }
        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the source DTO for one series entry, resolving its stored (usually source-relative)
    /// url into the absolute page url via the owning extension's <c>getMangaUrl</c>. The manga is
    /// reconstructed from the entry's record — url, the per-source title (Data.TitleId) and the memo
    /// (some sources stash a slug there) — so extension overrides behave as in the sweep path.
    /// </summary>
    /// <summary>
    /// Projects a series entry to one <see cref="ContributionMappingSourceDto"/> per LIVE source
    /// association. A consolidated entry contributed by several sources of the same package yields
    /// one DTO per source (the page lists them as separate member sources).
    /// </summary>
    private async Task<IReadOnlyList<ContributionMappingSourceDto>> ToSourceDtosAsync(
        ContributionSeriesEntity? s, IReadOnlyDictionary<Guid, string> titleById, CancellationToken token)
    {
        if (s == null) return [];

        var data = s.Data;
        string? title = data != null
            && titleById.TryGetValue(data.TitleId, out string? resolved)
                ? resolved
                : null;

        var liveSources = s.Sources
            .Where(ss => ContributionVersions.IsLive(ss.Version) && ss.Source != null)
            .OrderBy(ss => ss.SourceId)
            .ToList();

        var result = new List<ContributionMappingSourceDto>(liveSources.Count);
        foreach (var assoc in liveSources)
        {
            var source = assoc.Source!;
            string? sourceUrl = null;
            if (!string.IsNullOrWhiteSpace(data?.Url))
            {
                string sourceKey = $"{source.Package}:{source.SourceId}";
                // Resolve to the absolute page url; fall back to the stored url when the extension
                // cannot resolve it (unknown source, failure, or an already-absolute url).
                sourceUrl = await _urlResolver
                    .ResolveAsync(sourceKey, data!.Url, title, data.Memo, token)
                    .ConfigureAwait(false)
                    ?? data.Url;
            }

            result.Add(new ContributionMappingSourceDto
            {
                SourceId = source.Id,
                SourceKey = $"{source.Package}:{source.SourceId}",
                Package = source.Package,
                SourceName = source.SourceName,
                SourceLanguage = source.SourceLanguage,
                Title = title,
                ThumbnailUrl = data?.ThumbnailUrl,
                SourceUrl = sourceUrl
            });
        }

        return result;
    }

    /// <summary>Indexes the mapping's title rows by <see cref="TitleEntity.Id"/> for record lookups.</summary>
    private static Dictionary<Guid, string> BuildTitleIndex(IEnumerable<SeriesTitleEntity> seriesTitles)
        => seriesTitles
            .Where(mt => mt.Title != null && !string.IsNullOrWhiteSpace(mt.Title.Title))
            .GroupBy(mt => mt.Title!.Id)
            .ToDictionary(g => g.Key, g => g.First().Title!.Title!);

    private static bool IsActiveStatus(SeriesMappingStatus status)
        => status is SeriesMappingStatus.Unmatched
            or SeriesMappingStatus.AutoMatched
            or SeriesMappingStatus.UserConfirmed
            or SeriesMappingStatus.Blocked
            or SeriesMappingStatus.ManualIgnore
            or SeriesMappingStatus.ForeverIgnored;
}
