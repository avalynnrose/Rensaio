using RensaioBackend.Data;
using RensaioBackend.Models;
using RensaioBackend.Services.Images;
using RensaioBackend.Services.Settings;
using RensaioBackend.Extensions;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Metadata;
using RensaioBackend.Services.Scrobbling.Abstractions;
using RensaioBackend.Services.Series;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace RensaioBackend.Services.Scrobbling;

/// <summary>
/// Automatic + manual series matching between local series and external providers.
/// Global-only: mapping state lives in <see cref="SeriesMappingEntity"/> (the per-user
/// <c>UserSeriesMappings</c> table has been dropped). userId is accepted for API back-compat
/// and only used to pick the provider token; mapping writes are app-wide.
/// Provides minimal logging on match decisions.
/// </summary>
public class SeriesMatchingService
{
    private readonly AppDbContext _db;
    private readonly ContributionDbContext _contributorDb;
    private readonly ExternalSeriesProviderFactory _providerFactory;
    private readonly TitleMatcher _titleMatcher;
    private readonly SeriesStateService _seriesStateService;
    private readonly SettingsService _settings;
    private readonly ThumbCacheService _thumbCache;
    private readonly ILogger<SeriesMatchingService> _logger;
    private readonly MetadataLinkEngine _linkEngine;
    private readonly Contributions.ContributionPropagationService _contributionSync;
    private const string ImagePrefix = "/api/image/";

    // Contribution-DB row version semantics (see ContributionUploadService): 0 = pending
    // add/update, -1 = pending delete, -2 = uploaded (LIVE), -3 = uploaded delete.
    private const int ContributionVersionLogicalDelete = -1;
    private const int ContributionVersionUploadedDelete = -3;

    public SeriesMatchingService(
        AppDbContext db,
        ContributionDbContext contributorDb,
        ExternalSeriesProviderFactory providerFactory,
        TitleMatcher titleMatcher,
        SeriesStateService seriesStateService,
        SettingsService settings,
        ThumbCacheService thumbCache,
        ILogger<SeriesMatchingService> logger,
        MetadataLinkEngine linkEngine,
        Contributions.ContributionPropagationService contributionSync)
    {
        _db = db;
        _contributorDb = contributorDb;
        _providerFactory = providerFactory;
        _titleMatcher = titleMatcher;
        _seriesStateService = seriesStateService;
        _settings = settings;
        _thumbCache = thumbCache;
        _logger = logger;
        _linkEngine = linkEngine;
        _contributionSync = contributionSync;
    }

    /// <summary>Best-effort contribution propagation after a mapping mutation.</summary>
    private async Task SyncContributionAsync(Guid seriesId, CancellationToken token)
    {
        try
        {
            await _contributionSync.SyncSeriesAsync(seriesId, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to propagate contribution state for series {SeriesId}", seriesId);
        }
    }

    private async ValueTask<string?> ResolveCoverUrlAsync(string? thumbnailUrl, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(thumbnailUrl))
            return null;
        var key = await _thumbCache.GetKeyAsync(thumbnailUrl, token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(key))
            return null;
        return ImagePrefix + key;
    }

    /// <summary>Auto-match a single series across the user's enabled providers (global writes).</summary>
    public async Task AutoMatchSeriesAsync(Guid userId, Guid seriesId, CancellationToken token = default)
    {
        var configs = await _db.UserScrobblerConfigs
            .Where(c => c.UserId == userId && c.IsEnabled)
            .ToListAsync(token);
        var series = await _db.Series
            .Include(s => s.Sources)
            .FirstOrDefaultAsync(s => s.Id == seriesId, token);
        if (series == null)
        {
            _logger.LogDebug("Auto-match skipped: series {SeriesId} not found", seriesId);
            return;
        }
        foreach (var config in configs)
        {
            await AutoMatchForProviderAsync(userId, series, config.Provider, token);
        }
        _logger.LogDebug("Auto-match finished for series {SeriesId} across {Count} providers", seriesId, configs.Count);
    }

    /// <summary>Auto-match all unmatched series for a user across a provider (global writes).</summary>
    public async Task<AutoMatchResultDto> AutoMatchAllAsync(Guid userId, ExternalSeriesProvider provider, CancellationToken token = default)
    {
        var result = new AutoMatchResultDto();
        var config = await _db.UserScrobblerConfigs
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Provider == provider, token);
        if (config == null || !config.IsEnabled)
            return result;

        var seriesList = await _db.Series
            .Include(s => s.Sources)
            .ToListAsync(token);
        result.TotalSeries = seriesList.Count;

        foreach (var series in seriesList)
        {
            var existingMapping = await _db.SeriesMappings
                .FirstOrDefaultAsync(m => m.SeriesId == series.Id && m.Provider == provider, token);
            if (existingMapping != null && existingMapping.MappingStatus != SeriesMappingStatus.Unmatched)
            {
                result.LeftUnmatched++;
                continue;
            }

            var matchResult = await TryAutoMatchAsync(userId, series, provider, token);
            if (matchResult != null)
            {
                result.AutoMatched++;
                if (matchResult.MatchScore.HasValue && matchResult.MatchScore.Value < 0.95)
                    result.SuggestedMatches.Add(matchResult);
            }
            else
            {
                result.LeftUnmatched++;
            }
        }
        _logger.LogDebug("Auto-match all complete for provider {Provider}: {AutoMatched} matched, {Left} unmatched",
            provider, result.AutoMatched, result.LeftUnmatched);
        return result;
    }

    /// <summary>Search an external provider for a series (uses the user's token).</summary>
    public async Task<List<ScrobblerSearchResult>> SearchExternalSeriesAsync(
        Guid userId, ExternalSeriesProvider provider, string query, CancellationToken token = default)
    {
        var scrobbler = _providerFactory.GetProvider(provider);
        if (scrobbler == null) return [];
        await scrobbler.EnsureAuthenticatedAsync(userId, token);
        var results = await scrobbler.SearchSeriesAsync(query, token);
        // Rewrite provider cover URLs through the image cache (Cloudflare bypass + etag + cache).
        await _thumbCache.PopulateThumbsAsync(results, token: token);
        return results;
    }

    /// <summary>Confirm a manual match — writes the global SeriesMapping (app-wide).</summary>
    public async Task ConfirmMatchAsync(Guid userId, Guid seriesId, ExternalSeriesProvider provider,
        string externalSeriesId, string? externalTitle = null, CancellationToken token = default)
    {
        var mapping = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == provider, token);
        var series = await _db.Series.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == seriesId, token);

        // Blocked = id-level rule: you can't match to the blocked id, but you CAN match another id.
        // EXCEPTION: a REPAIR-created (auto-sealed) id-block is subordinate to an explicit user
        // choice — the auto-repair guessed this id was wrong (see SeriesMappingEntity.IsAutoSealedBlock),
        // but the user confirming here knows better, so it is always overridable. A USER-created block
        // (real LinkedDate) is a deliberate refusal and still refuses the confirm — unless the block
        // holder is a legitimately-distinct SAME-TITLE / DIFFERENT-CATEGORY twin (categorized folders,
        // different first path segment), the same exception the conflict repair applies.
        if (mapping?.MappingStatus == SeriesMappingStatus.Blocked &&
            string.Equals(mapping.ExternalSeriesId, externalSeriesId, StringComparison.OrdinalIgnoreCase))
        {
            bool overridable = mapping.IsAutoSealedBlock
               || (series != null
                   && await IsSameTitleDifferentCategoryOwnerAsync(series, provider, externalSeriesId, token));
            if (!overridable)
            {
                throw new InvalidOperationException(
                    $"Cannot match series {seriesId} to the blocked id '{externalSeriesId}' on {provider}.");
            }
            // The user's explicit choice wins; lift the auto/user block and link.
            mapping.MappingStatus = SeriesMappingStatus.UserConfirmed;
            mapping.ExternalSeriesId = externalSeriesId;
            mapping.ExternalSeriesTitle = externalTitle;
            mapping.LinkedDate = DateTime.UtcNow;
            mapping.UpdateDate = DateTime.UtcNow;
            await _db.SaveChangesAsync(token);
            await _linkEngine.LinkSeriesAsync(seriesId, token).ConfigureAwait(false);
            await SyncContributionAsync(seriesId, token).ConfigureAwait(false);
            _logger.LogDebug("Confirmed match (blocked {BlockKind}) series {SeriesId} provider {Provider} -> {ExternalId}",
                mapping.IsAutoSealedBlock ? "auto" : "user",
                seriesId, provider, externalSeriesId);
            return;
        }
        var now = DateTime.UtcNow;
        if (mapping != null)
        {
            mapping.ExternalSeriesId = externalSeriesId;
            mapping.ExternalSeriesTitle = externalTitle;
            mapping.MappingStatus = SeriesMappingStatus.UserConfirmed;
            mapping.LinkedDate = now;
            mapping.UpdateDate = now;
        }
        else
        {
            _db.SeriesMappings.Add(new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                Provider = provider,
                ExternalSeriesId = externalSeriesId,
                ExternalSeriesTitle = externalTitle,
                UserUid = userId,
                UserRole = UserLevel.User,
                MappingStatus = SeriesMappingStatus.UserConfirmed,
                LinkedDate = now,
                UpdateDate = now
            });
        }
        await _db.SaveChangesAsync(token);

        // Enrich the confirmed mapping with the provider's Series Metadata (cover, alt titles,
        // linked site ids) and automatch the linked providers by re-running the metadata link
        // engine, which reseeds from the just-saved mapping and propagates through LinkedSitesIds.
        await _linkEngine.LinkSeriesAsync(seriesId, token).ConfigureAwait(false);

        // Propagate the newly UserConfirmed mapping into the local contribution
        // database (contributor.db) so it participates in cloud contribution sync —
        // same as auto-match, disable and remove paths.
        await SyncContributionAsync(seriesId, token).ConfigureAwait(false);

        _logger.LogDebug("Confirmed match series {SeriesId} provider {Provider} -> {ExternalId}", seriesId, provider, externalSeriesId);
    }

    /// <summary>
    /// True when a series with an active claim to <paramref name="externalSeriesId"/> on
    /// <paramref name="provider"/> is a legitimately-distinct twin of <paramref name="series"/>:
    /// same normalized title AND different first storage-path segment (different category folder).
    /// Mirrors MappingConflictRepairService's category-aware exception so a stale auto-block on a
    /// same-title/different-category twin never prevents a manual confirmation.
    /// </summary>
    private async Task<bool> IsSameTitleDifferentCategoryOwnerAsync(
        SeriesEntity series, ExternalSeriesProvider provider, string externalSeriesId, CancellationToken token)
    {
        // EF can't translate string.Equals(…, OrdinalIgnoreCase) in a LINQ filter — compare the
        // lowercased stored value against the lowercased id (SQL LOWER/==). Status guard is a plain
        // equality OR (AutoMatched / UserConfirmed are the "active claim" statuses).
        string lowerId = externalSeriesId.ToLowerInvariant();
        var ownerId = await _db.SeriesMappings
            .AsNoTracking()
            .Where(m => m.SeriesId != series.Id
                && m.Provider == provider
                && m.ExternalSeriesId != null
                && m.ExternalSeriesId.ToLower() == lowerId
                && (m.MappingStatus == SeriesMappingStatus.AutoMatched
                    || m.MappingStatus == SeriesMappingStatus.UserConfirmed))
            .Select(m => m.SeriesId)
            .FirstOrDefaultAsync(token);
        if (ownerId == null || ownerId == Guid.Empty) return false;

        var owner = await _db.Series.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == ownerId, token);
        if (owner == null) return false;

        string ta = NormalizeTitle(series.Title);
        string tb = NormalizeTitle(owner.Title);
        if (string.IsNullOrWhiteSpace(ta) || string.IsNullOrWhiteSpace(tb))
            return false;
        if (!string.Equals(ta, tb, StringComparison.OrdinalIgnoreCase))
            return false;

        string ca = FirstPathSegment(series.StoragePath);
        string cb = FirstPathSegment(owner.StoragePath);
        return !string.IsNullOrWhiteSpace(ca)
            && !string.IsNullOrWhiteSpace(cb)
            && !string.Equals(ca, cb, StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstPathSegment(string path)
    {
        string p = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        int idx = p.IndexOf('/');
        return idx > 0 ? p[..idx] : p;
    }

    private static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        return string.Join(' ', title.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }

    /// <summary>Get all series matching statuses (global mappings) for the user's providers.</summary>
    public async Task<List<SeriesMatchStatusDto>> GetUnmatchedSeriesAsync(Guid userId, CancellationToken token = default)
    {
        var configs = await _db.UserScrobblerConfigs
            .Where(c => c.UserId == userId && c.IsEnabled)
            .ToListAsync(token);
        List<SeriesMatchStatusDto> result = [];

        foreach (var config in configs)
        {
            var seriesList = await _db.Series
                .Include(s => s.Sources)
                .ToListAsync(token);
            foreach (var series in seriesList)
            {
                var mapping = await _db.SeriesMappings
                    .FirstOrDefaultAsync(m => m.SeriesId == series.Id && m.Provider == config.Provider, token);
                var altTitles = string.Join(", ", series.Sources?
                    .Where(s => !string.IsNullOrEmpty(s.Title) && !s.Title.Equals(series.Title, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Title)
                    .Distinct() ?? []);
                var coverUrl = await ResolveCoverUrlAsync(series.ThumbnailUrl, token);
                result.Add(new SeriesMatchStatusDto
                {
                    SeriesId = series.Id,
                    SeriesTitle = series.Title,
                    SeriesCoverUrl = coverUrl,
                    AlternativeTitles = altTitles,
                    Provider = config.Provider,
                    MappingStatus = mapping?.MappingStatus ?? SeriesMappingStatus.Unmatched,
                    ExternalSeriesId = mapping?.ExternalSeriesId,
                    ExternalSeriesTitle = mapping?.ExternalSeriesTitle,
                    MatchScore = null
                });
            }
        }
        return result;
    }

    /// <summary>Mark a series/provider as forever-ignored (global).</summary>
    public async Task DisableSeriesLinkAsync(Guid userId, Guid seriesId, ExternalSeriesProvider provider, CancellationToken token = default)
    {
        var mapping = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == provider, token);
        if (mapping != null)
        {
            mapping.MappingStatus = SeriesMappingStatus.ForeverIgnored;
            mapping.UpdateDate = DateTime.UtcNow;
        }
        else
        {
            _db.SeriesMappings.Add(new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                Provider = provider,
                ExternalSeriesId = string.Empty,
                UserUid = userId,
                UserRole = UserLevel.User,
                MappingStatus = SeriesMappingStatus.ForeverIgnored,
                UpdateDate = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync(token);
        await SyncContributionAsync(seriesId, token);
        _logger.LogDebug("Disabled link series {SeriesId} provider {Provider}", seriesId, provider);
    }

    /// <summary>Remove a mapping (reset to Unmatched).</summary>
    public async Task RemoveMappingAsync(Guid userId, Guid seriesId, ExternalSeriesProvider provider, CancellationToken token = default)
    {
        var mapping = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == provider, token);
        if (mapping != null)
        {
            _db.SeriesMappings.Remove(mapping);
            await _db.SaveChangesAsync(token);
            await SyncContributionAsync(seriesId, token);
        }
    }

    /// <summary>Get all match statuses for a user (global mappings across providers).</summary>
    public async Task<List<SeriesMatchStatusDto>> GetMatchStatusesAsync(Guid userId, CancellationToken token = default)
    {
        var allSeries = await _db.Series
            .Include(s => s.Sources)
            .ToListAsync(token);
        var allMappings = await _db.SeriesMappings.ToListAsync(token);
        List<SeriesMatchStatusDto> result = [];
        foreach (var provider in Enum.GetValues<ExternalSeriesProvider>())
        {
            foreach (var series in allSeries)
            {
                var mapping = allMappings.FirstOrDefault(m => m.SeriesId == series.Id && m.Provider == provider);
                var altTitles = string.Join(", ", series.Sources?
                    .Where(s => !string.IsNullOrEmpty(s.Title) && !s.Title.Equals(series.Title, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Title)
                    .Distinct() ?? []);
                var externalId = mapping?.ExternalSeriesId;
                string? externalUrl = null;
                if (!string.IsNullOrEmpty(externalId))
                {
                    var prov = _providerFactory.GetProvider(provider);
                    if (prov?.SeriesUrlTemplate != null)
                    {
                        try { externalUrl = string.Format(prov.SeriesUrlTemplate, externalId); }
                        catch { }
                    }
                }
                var coverUrl = await ResolveCoverUrlAsync(series.ThumbnailUrl, token);
                result.Add(new SeriesMatchStatusDto
                {
                    SeriesId = series.Id,
                    SeriesTitle = series.Title,
                    SeriesCoverUrl = coverUrl,
                    AlternativeTitles = altTitles,
                    Provider = provider,
                    MappingStatus = mapping?.MappingStatus ?? SeriesMappingStatus.Unmatched,
                    ExternalSeriesId = externalId,
                    ExternalSeriesTitle = mapping?.ExternalSeriesTitle,
                    ExternalSeriesUrl = externalUrl,
                    MatchScore = null
                });
            }
        }
        return result;
    }

    /// <summary>Get sync status per provider (global mappings).</summary>
    public async Task<List<SyncStatusDto>> GetSyncStatusAsync(Guid userId, CancellationToken token = default)
    {
        var configs = await _db.UserScrobblerConfigs
            .Where(c => c.UserId == userId)
            .ToListAsync(token);
        List<SyncStatusDto> result = [];
        foreach (var config in configs)
        {
            var seriesMatched = await _db.SeriesMappings
                .CountAsync(m => m.Provider == config.Provider
                    && (m.MappingStatus == SeriesMappingStatus.UserConfirmed
                        || m.MappingStatus == SeriesMappingStatus.AutoMatched), token);
            var seriesUnmatched = await _db.SeriesMappings
                .CountAsync(m => m.Provider == config.Provider
                    && m.MappingStatus == SeriesMappingStatus.Unmatched, token);
            var seriesIgnored = await _db.SeriesMappings
                .CountAsync(m => m.Provider == config.Provider
                    && (m.MappingStatus == SeriesMappingStatus.ForeverIgnored
                        || m.MappingStatus == SeriesMappingStatus.TemporaryIgnored), token);
            result.Add(new SyncStatusDto
            {
                Provider = config.Provider,
                LastSyncAt = config.LastSyncAt,
                LastUploadAt = config.LastUploadAt,
                LastDownloadAt = config.LastDownloadAt,
                SeriesMatched = seriesMatched,
                SeriesUnmatched = seriesUnmatched,
                SeriesIgnored = seriesIgnored
            });
        }
        return result;
    }

    // ── Private ──

    private async Task AutoMatchForProviderAsync(Guid userId, SeriesEntity series,
        ExternalSeriesProvider provider, CancellationToken token)
    {
        await TryAutoMatchAsync(userId, series, provider, token);
    }

    private async Task<SeriesMatchStatusDto?> TryAutoMatchAsync(Guid userId, SeriesEntity series,
        ExternalSeriesProvider provider, CancellationToken token)
    {
        var scrobbler = _providerFactory.GetProvider(provider);
        if (scrobbler == null)
        {
            _logger.LogDebug("Auto-match skipped series {SeriesId}: provider {Provider} unavailable", series.Id, provider);
            return null;
        }

        await scrobbler.EnsureAuthenticatedAsync(userId, token);

        // Bug fix: if this series is already linked for this provider (any active/decision
        // status except Unmatched), do NOT re-search — the mapping is already settled.
        var preExisting = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == series.Id && m.Provider == provider, token);
        if (preExisting != null && preExisting.MappingStatus != SeriesMappingStatus.Unmatched)
        {
            _logger.LogDebug("Auto-match skipped series {SeriesId} provider {Provider}: already linked (status {Status})",
                series.Id, provider, preExisting.MappingStatus);
            return null;
        }

        // CanSearchSeries gate — BEFORE searching this series on this provider, ask the provider
        // whether the series is searchable at all. Genres come from SeriesEntity.Genre and the
        // category comes from SeriesEntity.Type. When the provider cannot search this series, the
        // (series, provider) relation is settled as "ignoredAlways" (ForeverIgnored) so the
        // provider is never searched for this series again.
        if (!scrobbler.CanSearchSeries(series.Genre, series.Type))
        {
            _logger.LogDebug("Auto-match skipped series {SeriesId} provider {Provider}: CanSearchSeries=false → ignoredAlways",
                series.Id, provider);
            await EnsureRelationIgnoredAsync(userId, series.Id, provider, token).ConfigureAwait(false);
            return null;
        }

        // Contribution database FIRST match. When the community (contribution) database already
        // linked THIS series on THIS provider (AutoMatched / UserConfirmed with a provider key),
        // we USE that hit instead of searching the provider — HIT = no scrobbler/metadata search,
        // only a metadata fetch when the provider is available (it is, in this auto-match flow).
        var contributionId = await FindContributionLinkAsync(series, provider, token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(contributionId))
        {
            _logger.LogDebug("Auto-match series {SeriesId} provider {Provider}: contribution DB hit -> {ExternalId} (no search)",
                series.Id, provider, contributionId);
            return await ApplyContributionHitAsync(userId, series, provider, contributionId, token).ConfigureAwait(false);
        }

        var localCandidates = _titleMatcher.BuildTitleCandidates(series);
        var uniqueTitles = localCandidates
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (uniqueTitles.Length == 0) return null;

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allCandidates = new List<(string SearchTitle, string Id)>();
        var resultLookup = new Dictionary<string, ScrobblerSearchResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var title in uniqueTitles)
        {
            var searchResults = await scrobbler.SearchSeriesAsync(title, token);
            foreach (var result in searchResults)
            {
                if (string.IsNullOrWhiteSpace(result.ExternalId) || string.IsNullOrWhiteSpace(result.Title)) continue;
                if (seenIds.Add(result.ExternalId))
                {
                    allCandidates.Add((result.Title, result.ExternalId));
                    foreach (var alttitle in result.AlternateTitles)
                        allCandidates.Add((alttitle, result.ExternalId));
                    resultLookup[result.ExternalId] = result;
                }
            }
        }

        if (allCandidates.Count == 0) return null;

        var scored = TitleMatcher.MatchTitles(
            originalTitles: localCandidates,
            candidates: allCandidates,
            minimumScore: 0);
        if (scored.Length == 0) return null;

        var best = scored[0];
        var bestPercentage = best.Percentage;
        var bestExternalId = best.Id;
        var bestExternalTitle = best.SearchTitle;
        var bestResult = resultLookup.GetValueOrDefault(bestExternalId);
        // Rewrite the raw provider cover through the image cache before it is surfaced to the
        // frontend (either in the suggested-match row or after auto-match). Never persisted.
        if (bestResult != null)
            await _thumbCache.PopulateThumbsAsync(bestResult, token: token);

        const int autoMatchThreshold = 95;
        if (bestPercentage < autoMatchThreshold)
        {
            _logger.LogDebug("Auto-match suggested (score {Score}%) series {SeriesId} provider {Provider} -> {ExternalId}",
                bestPercentage, series.Id, provider, bestExternalId);
            var existingMapping = await _db.SeriesMappings
                .FirstOrDefaultAsync(m => m.SeriesId == series.Id && m.Provider == provider, token);
            if (existingMapping == null)
            {
                _db.SeriesMappings.Add(new SeriesMappingEntity
                {
                    Id = Guid.NewGuid(),
                    SeriesId = series.Id,
                    Provider = provider,
                    ExternalSeriesId = bestExternalId,
                    ExternalSeriesTitle = bestExternalTitle,
                    UserUid = userId,
                    UserRole = UserLevel.User,
                    MappingStatus = SeriesMappingStatus.Unmatched,
                    UpdateDate = DateTime.UtcNow
                });
                await _db.SaveChangesAsync(token);
            }
            return new SeriesMatchStatusDto
            {
                SeriesId = series.Id,
                SeriesTitle = series.Title,
                Provider = provider,
                MappingStatus = SeriesMappingStatus.Unmatched,
                ExternalSeriesId = bestExternalId,
                ExternalSeriesTitle = bestExternalTitle,
                ExternalCoverUrl = bestResult?.CoverUrl,
                MatchScore = bestPercentage / 100.0
            };
        }

        // CONFLICT GUARD: never auto-link this series to a (provider, externalId) already owned by
        // a DIFFERENT series (active claim + stronger or equal decision). Refuse and return — the
        // mapping-conflict repair pass settles deterministically who owns each id.
        var owner = await Metadata.MappingOwnershipGuard.FindOwnerAsync(_db, provider,
            bestExternalId, series.Id, token).ConfigureAwait(false);
        if (owner != null && Metadata.MappingOwnershipGuard.IsStrongerThanNewAutoMatch(owner))
        {
            var existingBlocked = await _db.SeriesMappings
                .FirstOrDefaultAsync(m => m.SeriesId == series.Id && m.Provider == provider, token);
            // Guard against the bogus-key bug: a "0"/empty bestExternalId means the search produced
            // no real id — creating a Blocked row with it would be a meaningless hard block that the
            // startup repair then has to downgrade again. Skip the block entirely when there's no id.
            if (existingBlocked == null && !string.IsNullOrWhiteSpace(bestExternalId) && bestExternalId != "0")
            {
                _db.SeriesMappings.Add(new SeriesMappingEntity
                {
                    Id = Guid.NewGuid(),
                    SeriesId = series.Id,
                    Provider = provider,
                    ExternalSeriesId = bestExternalId,
                    MappingStatus = SeriesMappingStatus.Blocked,
                    LinkedDate = SeriesMappingEntity.AutoSealedSentinel,
                    UserRole = UserLevel.User,
                    UpdateDate = DateTime.UtcNow
                });
                await _db.SaveChangesAsync(token);
                await SyncContributionAsync(series.Id, token);
            }
            _logger.LogDebug("Auto-match refused series {SeriesId} provider {Provider} -> {ExternalId}: "
                + "already owned by series {OwnerSeriesId}", series.Id, provider, bestExternalId, owner.SeriesId);
            return null;
        }

        var existing = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == series.Id && m.Provider == provider, token);
        var now = DateTime.UtcNow;
        if (existing != null)
        {
            // Never downgrade a settled decision: user-confirmed / blocked matches must keep
            // their status — auto-match only fills/updates the external id/title columns.
            if (existing.MappingStatus == SeriesMappingStatus.UserConfirmed ||
                existing.MappingStatus == SeriesMappingStatus.Blocked ||
                existing.MappingStatus == SeriesMappingStatus.ForeverIgnored ||
                existing.MappingStatus == SeriesMappingStatus.TemporaryIgnored)
            {
                _logger.LogDebug("Auto-match kept existing status {Status} for series {SeriesId} provider {Provider}",
                    existing.MappingStatus, series.Id, provider);
                await _seriesStateService.SyncToRensaioJsonAsync(series.Id, token).ConfigureAwait(false);
                return new SeriesMatchStatusDto
                {
                    SeriesId = series.Id,
                    SeriesTitle = series.Title,
                    Provider = provider,
                    MappingStatus = existing.MappingStatus,
                    ExternalSeriesId = existing.ExternalSeriesId,
                    ExternalSeriesTitle = existing.ExternalSeriesTitle,
                    MatchScore = bestPercentage / 100.0
                };
            }

            existing.ExternalSeriesId = bestExternalId;
            existing.ExternalSeriesTitle = bestExternalTitle;
            existing.MappingStatus = SeriesMappingStatus.AutoMatched;
            existing.LinkedDate = now;
            existing.UpdateDate = now;
        }
        else
        {
            _db.SeriesMappings.Add(new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = series.Id,
                Provider = provider,
                ExternalSeriesId = bestExternalId,
                ExternalSeriesTitle = bestExternalTitle,
                UserUid = userId,
                UserRole = UserLevel.User,
                MappingStatus = SeriesMappingStatus.AutoMatched,
                LinkedDate = now,
                UpdateDate = now
            });
        }
        await _db.SaveChangesAsync(token);

        // Upsert global metadata via the link engine cache fields from the search result.
        await UpsertSeriesMappingAsync(userId, series.Id, provider, bestExternalId, bestExternalTitle, UserLevel.User, token);
        _logger.LogDebug("Auto-matched series {SeriesId} provider {Provider} -> {ExternalId} (score {Score}%)",
            series.Id, provider, bestExternalId, bestPercentage);

        return new SeriesMatchStatusDto
        {
            SeriesId = series.Id,
            SeriesTitle = series.Title,
            Provider = provider,
            MappingStatus = SeriesMappingStatus.AutoMatched,
            ExternalSeriesId = bestExternalId,
            ExternalSeriesTitle = bestExternalTitle,
            ExternalCoverUrl = bestResult?.CoverUrl,
            MatchScore = bestPercentage / 100.0
        };
    }

    /// <summary>
    /// Contribution-DB first match for a single (series, provider) pair. Resolves the local
    /// series' titles to an existing contribution mapping and returns the provider key when the
    /// community already linked this series on this provider with an active status.
    /// A HIT means the provider must NOT be searched — the mapping is adopted directly.
    /// </summary>
    private async Task<string?> FindContributionLinkAsync(SeriesEntity series,
        ExternalSeriesProvider provider, CancellationToken token)
    {
        try
        {
            var localCandidates = _titleMatcher.BuildTitleCandidates(series)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (localCandidates.Count == 0) return null;

            var titleIds = localCandidates.Select(TitleEntity.DeriveId).Distinct().ToHashSet();

            var mappingTitles = await _contributorDb.MappingTitles
                .Where(mt => titleIds.Contains(mt.TitleId))
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);
            if (mappingTitles.Count == 0) return null;

            // Prefer the mapping that links the MAIN title, then the one with the most shared titles.
            var mainTitleId = TitleEntity.DeriveId(series.Title);
            var mappingId = mappingTitles
                .GroupBy(mt => mt.MappingId)
                .OrderByDescending(g => g.Any(mt => mt.TitleId == mainTitleId) ? 1 : 0)
                .ThenByDescending(g => g.Count())
                .First().Key;

            // Uploaded rows are -2 (NOT >= 0): use the tombstone test, otherwise a first-match
            // lookup returns nothing once the row has been exported to the cloud.
            var row = await _contributorDb.Metadata
                .Where(m => m.MappingId == mappingId
                    && m.ProviderId == (int)provider
                    && m.Version != ContributionVersionLogicalDelete
                    && m.Version != ContributionVersionUploadedDelete
                    && !string.IsNullOrWhiteSpace(m.ProviderKey)
                    && (m.MappingStatus == SeriesMappingStatus.AutoMatched
                        || m.MappingStatus == SeriesMappingStatus.UserConfirmed))
                .AsNoTracking()
                .FirstOrDefaultAsync(token).ConfigureAwait(false);

            return row?.ProviderKey;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Contribution DB first-match failed for series {SeriesId} provider {Provider}",
                series.Id, provider);
            return null;
        }
    }

    /// <summary>
    /// Adopts a contribution-DB hit: persists the (series, provider) mapping as AutoMatched using
    /// the community-established external id and enriches it with the provider's series metadata
    /// (cover, alt titles, linked site ids). The provider is available here (this auto-match flow
    /// only reaches enabled providers), so a metadata fetch is allowed — no search is performed.
    /// </summary>
    private async Task<SeriesMatchStatusDto?> ApplyContributionHitAsync(Guid userId, SeriesEntity series,
        ExternalSeriesProvider provider, string externalId, CancellationToken token)
    {
        var now = DateTime.UtcNow;
        var existing = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == series.Id && m.Provider == provider, token);

        // Blocked is id-level: the user blocked this exact id → the contribution hit cannot be used.
        if (existing?.MappingStatus == SeriesMappingStatus.Blocked &&
            string.Equals(existing.ExternalSeriesId, externalId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Contribution hit refused series {SeriesId} provider {Provider}: id {Id} is blocked",
                series.Id, provider, externalId);
            return null;
        }

        var scrobbler = _providerFactory.GetProvider(provider);
        SeriesMetadataResult? metadata = null;
        if (scrobbler != null)
        {
            try { metadata = await scrobbler.FetchSeriesMetadataAsync(externalId, token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch series metadata for contribution hit {Provider} {Id}", provider, externalId);
            }
        }
        var externalTitle = metadata?.Title ?? existing?.ExternalSeriesTitle;
        var coverUrl = metadata?.CoverUrl ?? existing?.SeriesCoverUrl;
        var linkedSites = metadata?.LinkedSitesIds ?? [];
        var altTitles = metadata?.AlternativeTitles ?? [];
        var metaData = metadata?.MetaData;

        if (existing != null)
        {
            existing.ExternalSeriesId = externalId;
            existing.ExternalSeriesTitle = externalTitle;
            existing.MappingStatus = SeriesMappingStatus.AutoMatched;
            existing.LinkedDate = now;
            existing.UpdateDate = now;
            if (string.IsNullOrWhiteSpace(existing.SeriesCoverUrl) && !string.IsNullOrWhiteSpace(coverUrl))
                existing.SeriesCoverUrl = coverUrl;
            if (!string.IsNullOrWhiteSpace(metaData))
                existing.MetaData = metaData;
            existing.LinkedSitesIds = MergeStrings(existing.LinkedSitesIds, linkedSites);
            existing.AlternativeTitles = MergeStrings(existing.AlternativeTitles, altTitles);
        }
        else
        {
            _db.SeriesMappings.Add(new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = series.Id,
                Provider = provider,
                ExternalSeriesId = externalId,
                ExternalSeriesTitle = externalTitle,
                SeriesCoverUrl = coverUrl,
                MetaData = metaData,
                LinkedSitesIds = linkedSites,
                AlternativeTitles = altTitles,
                UserUid = userId,
                UserRole = UserLevel.User,
                MappingStatus = SeriesMappingStatus.AutoMatched,
                LinkedDate = now,
                UpdateDate = now
            });
        }
        await _db.SaveChangesAsync(token);
        await _seriesStateService.SyncToRensaioJsonAsync(series.Id, token).ConfigureAwait(false);
        await SyncContributionAsync(series.Id, token);
        _logger.LogDebug("Auto-matched series {SeriesId} provider {Provider} -> {ExternalId} via contribution DB hit",
            series.Id, provider, externalId);

        return new SeriesMatchStatusDto
        {
            SeriesId = series.Id,
            SeriesTitle = series.Title,
            Provider = provider,
            MappingStatus = SeriesMappingStatus.AutoMatched,
            ExternalSeriesId = externalId,
            ExternalSeriesTitle = externalTitle,
            ExternalCoverUrl = coverUrl,
            MatchScore = 1.0
        };
    }

    /// <summary>
    /// Settles a (series, provider) relation as "ignoredAlways" (ForeverIgnored) because the
    /// provider declared it cannot search this series (CanSearchSeries == false). Never downgrades
    /// a stronger local decision (UserConfirmed / Blocked / ForeverIgnored).
    /// </summary>
    private async Task EnsureRelationIgnoredAsync(Guid userId, Guid seriesId,
        ExternalSeriesProvider provider, CancellationToken token)
    {
        var existing = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == provider, token);
        var now = DateTime.UtcNow;
        if (existing != null)
        {
            if (existing.MappingStatus == SeriesMappingStatus.UserConfirmed
                || existing.MappingStatus == SeriesMappingStatus.Blocked
                || existing.MappingStatus == SeriesMappingStatus.ForeverIgnored)
                return; // user/stronger decision stands
            existing.ExternalSeriesId = string.Empty;
            existing.MappingStatus = SeriesMappingStatus.ForeverIgnored;
            existing.LinkedDate = now;
            existing.UpdateDate = now;
        }
        else
        {
            _db.SeriesMappings.Add(new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                Provider = provider,
                ExternalSeriesId = string.Empty,
                ExternalSeriesTitle = null,
                UserUid = userId,
                UserRole = UserLevel.User,
                MappingStatus = SeriesMappingStatus.ForeverIgnored,
                LinkedDate = now,
                UpdateDate = now
            });
        }
        await _db.SaveChangesAsync(token);
        await _seriesStateService.SyncToRensaioJsonAsync(seriesId, token).ConfigureAwait(false);
        await SyncContributionAsync(seriesId, token);
        _logger.LogDebug("Relation series {SeriesId} provider {Provider} set to ignoredAlways (CanSearchSeries=false)",
            seriesId, provider);
    }

    private static List<string> MergeStrings(List<string> existing, List<string> incoming)
    {
        var set = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var s in incoming) set.Add(s);
        return set.ToList();
    }

    private async Task UpsertSeriesMappingAsync(Guid userId, Guid seriesId, ExternalSeriesProvider provider,
        string externalSeriesId, string? externalTitle, UserLevel userLevel, CancellationToken token)
    {
        var existing = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == provider, token);
        if (existing != null)
        {
            if (userLevel >= existing.UserRole)
            {
                existing.ExternalSeriesId = externalSeriesId;
                existing.ExternalSeriesTitle = externalTitle;
                existing.UserUid = userId;
                existing.UserRole = userLevel;
                existing.UpdateDate = DateTime.UtcNow;
            }
        }
        else
        {
            _db.SeriesMappings.Add(new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                Provider = provider,
                ExternalSeriesId = externalSeriesId,
                ExternalSeriesTitle = externalTitle,
                UserUid = userId,
                UserRole = userLevel,
                UpdateDate = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync(token);
        await _seriesStateService.SyncToRensaioJsonAsync(seriesId, token).ConfigureAwait(false);
        await SyncContributionAsync(seriesId, token);
    }
}