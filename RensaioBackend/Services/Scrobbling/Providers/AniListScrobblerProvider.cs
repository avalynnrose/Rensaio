using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Scrobbling.Abstractions;
using RensaioBackend.Utils;
using Microsoft.Extensions.Logging;

namespace RensaioBackend.Services.Scrobbling.Providers;

/// <summary>
/// AniList scrobbler provider using OAuth2 (via proxy) and GraphQL API.
/// OAuth authorization is handled by ProxyScrobblerProvider base class;
/// search, read-state, and upload operations call the AniList GraphQL API directly.
/// https://docs.anilist.co
///
/// DUAL-FLOW OAuth support (mirrors the OAuth proxy's flow selection):
///   - Authorization Code Grant (UseImplicitFlow = false, default when a client
///     secret is configured on the proxy): the proxy exchanges the code
///     server-side, receives a refresh token, and silent renewal works.
///   - Implicit Grant (UseImplicitFlow = true, default when NO client secret is
///     configured): the access token is delivered in the redirect URL fragment
///     and captured by the proxy's capture page. NO refresh token is issued →
///     tokens expire and the user must re-authorize (~1 year TTL).
///
/// The flow the proxy actually runs is determined by its own configuration
/// (PROXY_ANILIST_FLOW, or auto-detection from the presence of a client secret).
/// This constant keeps the backend provider consistent with that selection so
/// it never attempts a refresh the proxy cannot perform.
/// </summary>
public class AniListScrobblerProvider : ProxyScrobblerProvider
{
    private const string GraphQlEndpoint = "https://graphql.anilist.co";

    /// <summary>
    /// Selects which OAuth2 flow AniList runs through the proxy.
    ///
    ///   false (default) → Authorization Code Grant — refresh tokens ARE issued
    ///                      by the proxy; silent renewal is supported.
    ///   true             → Implicit Grant — NO refresh token; token expires and
    ///                      the user must re-authorize.
    ///
    /// IMPORTANT: Keep this in sync with the proxy's flow selection:
    ///   - Proxy auto-detects 'code' when PROXY_ANILIST_CLIENT_SECRET is set,
    ///     'implicit' otherwise; PROXY_ANILIST_FLOW overrides either way.
    /// </summary>
    private static readonly bool UseImplicitFlow = true;

    /// <summary>
    /// Implicit flow issues NO refresh token; code flow does. Mirror that in the
    /// shared token-lifecycle logic so EnsureAuthenticatedAsync only attempts a
    /// silent refresh when the flow actually supports it.
    /// </summary>
    public override bool SupportsTokenRefresh => !UseImplicitFlow;

    public AniListScrobblerProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<AniListScrobblerProvider> logger,
        IConfiguration configuration,
        ITokenStorageService tokenStorage,
        ScrobblerTokenProtector tokenProtector)
        : base(httpClientFactory, configuration, ExternalSeriesProvider.AniList, logger, tokenStorage, tokenProtector)
    {
        _apiHttpClient = httpClientFactory.CreateClient("Scrobbler_AniList");
    }
    private static readonly BoundedStringDecimalMap _dedupState = new();
    public override string? SeriesUrlTemplate => "https://anilist.co/manga/{0}";

    /// <summary>
    /// Flow-aware refresh handling:
    ///   - Code flow: delegate to the base proxy client (uses the proxy's
    ///     /refresh endpoint with the stored refresh token).
    ///   - Implicit flow: fail gracefully — the proxy has no refresh token for
    ///     this session and would reject the request.
    /// </summary>
    public override async Task<ScrobblerTokenResult> RefreshTokenAsync(string refreshToken, CancellationToken token = default)
    {
        if (UseImplicitFlow)
        {
            return new ScrobblerTokenResult
            {
                Success = false,
                ErrorMessage = "AniList implicit flow does not issue refresh tokens — please re-authorize when the access token expires."
            };
        }

        return await base.RefreshTokenAsync(refreshToken, token);
    }

    public override async Task<List<ScrobblerSearchResult>> SearchSeriesAsync(string query, CancellationToken token = default)
    {
        _logger.LogInformation("{provider}: searching for '{Query}'", ExternalSeriesProvider.AniList, query);
        var graphQlQuery = @"
            query ($search: String) {
                Page(page: 1, perPage: 25) {
                    media(search: $search, type: MANGA) {
                        id
                        idMal
                        title { romaji english native }
                        synonyms
                        coverImage { large }
                        format
                        status
                        chapters
                        volumes
                        description
                        averageScore
                        startDate { year }
                    }
                }
            }";

        var requestBody = new { query = graphQlQuery, variables = new { search = query } };
        _apiHttpClient.ApplyBearerToken(_accessToken);
        await ProviderRateLimits.EnforceAniListAsync();
        var response = await _apiHttpClient.PostAsJsonAsync(GraphQlEndpoint, requestBody, token);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AniListGraphQlResponse>(cancellationToken: token);
        var results = new List<ScrobblerSearchResult>();

        if (result?.Data?.Page?.Media == null) return results;

        foreach (var media in result.Data.Page.Media)
        {
            var title = media.Title?.Romaji ?? media.Title?.English ?? query;
            var titleCandidates = new List<string?> { media.Title?.Romaji, media.Title?.English, media.Title?.Native };
            if (media.Synonyms != null)
                titleCandidates.AddRange(media.Synonyms);

            var linkedSites = new List<string> { $"anilist:{media.Id}" };
            // AniList reports idMal=0 for manga with no MyAnimeList entry; that is a
            // placeholder, not a real id, so it must not become a declared edge.
            if (media.IdMal is int malId && malId > 0)
                linkedSites.Add($"myanimelist:{malId}");

            results.Add(new ScrobblerSearchResult
            {
                ExternalId = media.Id.ToString(),
                Title = title,
                AlternateTitles = TitleListBuilder.BuildAlternates(title, titleCandidates),
                LinkedSitesIds = linkedSites,
                CoverUrl = media.CoverImage?.Large,
                Type = media.Format,
                ChapterCount = media.Chapters,
                Status = media.Status,
                Synopsis = media.Description,
                Score = media.AverageScore,
                Year = media.StartDate?.Year?.ToString()
            });
        }

        return results;
    }

    /// <summary>
    /// Fetches full AniList metadata for a media ID (GraphQL Media query), preserving the
    /// provider-native payload in <see cref="SeriesMetadataResult.MetaData"/>.
    /// </summary>
    public override async Task<SeriesMetadataResult?> FetchSeriesMetadataAsync(string externalSeriesId, CancellationToken token = default)
    {
        base._logger.LogInformation("{provider}: fetching metadata for id '{Id}'", ExternalSeriesProvider.AniList, externalSeriesId);
        if (string.IsNullOrWhiteSpace(externalSeriesId)) return null;
        try
        {
            var query = @"
                query ($id: Int) {
                    Media(id: $id, type: MANGA) {
                        id
                        idMal
                        title { romaji english native }
                        synonyms
                        coverImage { large extraLarge }
                        format
                        status
                        chapters
                        volumes
                        description
                        averageScore
                        startDate { year month day }
                        endDate { year month day }
                        genres
                        tags { name }
                        externalLinks { site url id }
                    }
                }";

            var requestBody = new
            {
                query,
                variables = new { id = int.Parse(externalSeriesId) }
            };

            _apiHttpClient.ApplyBearerToken(_accessToken);
            await ProviderRateLimits.EnforceAniListAsync();
            var response = await _apiHttpClient.PostAsJsonAsync(GraphQlEndpoint, requestBody, token);
            if (!response.IsSuccessStatusCode) return null;

            var result = await response.Content.ReadFromJsonAsync<AniListMediaDetailResponse>(cancellationToken: token);
            var media = result?.Data?.Media;
            if (media == null) return null;

            var title = media.Title?.Romaji ?? media.Title?.English ?? externalSeriesId;
            var titleCandidates = new List<string?> { media.Title?.Romaji, media.Title?.English, media.Title?.Native };
            if (media.Synonyms != null) titleCandidates.AddRange(media.Synonyms);

            var linkedSites = new List<string> { $"anilist:{media.Id}" };
            // idMal=0 is AniList's "no MyAnimeList entry" placeholder — never a real edge.
            if (media.IdMal is int malId && malId > 0)
                linkedSites.Add($"myanimelist:{malId}");

            return new SeriesMetadataResult
            {
                ExternalId = media.Id.ToString(),
                Title = title,
                MetaData = System.Text.Json.JsonSerializer.Serialize(media),
                LinkedSitesIds = linkedSites,
                AlternativeTitles = TitleListBuilder.BuildAlternates(title, titleCandidates),
                CoverUrl = media.CoverImage?.ExtraLarge ?? media.CoverImage?.Large
            };
        }
        catch
        {
            return null;
        }
    }

    public override async Task<Dictionary<decimal, float>> GetReadChaptersAsync(string externalSeriesId, CancellationToken token = default)
    {
        var userId = await GetUserIdAsync(token);
        if (userId == null) return [];

        var query = @"
            query ($id: Int) {
                MediaList(userId: $userId, mediaId: $id, type: MANGA) {
                    progress
                    progressVolumes
                }
            }";

        var requestBody = new
        {
            query,
            variables = new { id = int.Parse(externalSeriesId) }
        };

        _apiHttpClient.ApplyBearerToken(_accessToken);
        await ProviderRateLimits.EnforceAniListAsync();
        var response = await _apiHttpClient.PostAsJsonAsync(GraphQlEndpoint, requestBody, token);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AniListMediaListResponse>(cancellationToken: token);
        var chapters = new Dictionary<decimal, float>();

        if (result?.Data?.MediaList?.Progress.HasValue == true)
        {
            var progress = result.Data.MediaList.Progress.Value;
            for (decimal i = 1; i <= progress; i++)
                chapters[i] = 1.0f;
        }

        return chapters;
    }
    private bool DeDup(string externalSeriesId, decimal chapterNumber)
    {
        string key = GetUserExternalKey(externalSeriesId);
        decimal? result = _dedupState.TryGet(key);
        return result != null && result == chapterNumber;
    }
    private void UpdateDeDup(string externalSeriesId, decimal chapterNumber)
    {
        string key = GetUserExternalKey(externalSeriesId);
        _dedupState.Set(key, chapterNumber);
    }
    public override async Task<bool> SetReadChaptersAsync(string externalSeriesId, Dictionary<decimal, float> chapterState, CancellationToken token = default)
    {

        decimal chapterNumber = chapterState.Where(a => a.Value == 1.0f).Select(a => a.Key).DefaultIfEmpty(0).Max();
        if (DeDup(externalSeriesId, chapterNumber))
            return true;
        var currentTotal = await GetChaptersReadTotalAsync(externalSeriesId, token);
        if (chapterNumber < currentTotal || chapterNumber <=0)
            return true;  // No update needed
        var mutation = @"
                mutation ($id: Int, $progress: Int) {
                    SaveMediaListEntry(mediaId: $id, progress: $progress) {
                        id
                        progress
                    }
                }";

        var requestBody = new
        {
            query = mutation,
            variables = new { id = int.Parse(externalSeriesId), progress = (int)chapterNumber }
        };
        _apiHttpClient.ApplyBearerToken(_accessToken);
        await ProviderRateLimits.EnforceAniListAsync();
        var response = await _apiHttpClient.PostAsJsonAsync(GraphQlEndpoint, requestBody, token);
        if (response.IsSuccessStatusCode)
        {
            UpdateDeDup(externalSeriesId, chapterNumber);
        }
        return response.IsSuccessStatusCode;
    }


    // ── Private Helpers ──

    private async Task<int?> GetUserIdAsync(CancellationToken token)
    {
        try
        {
            _apiHttpClient.ApplyBearerToken(_accessToken);
            var query = @"{ Viewer { id } }";
            await ProviderRateLimits.EnforceAniListAsync();
            var response = await _apiHttpClient.PostAsJsonAsync(GraphQlEndpoint, new { query }, token);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<AniListViewerResponse>(cancellationToken: token);
            return result?.Data?.Viewer?.Id;
        }
        catch
        {
            return null;
        }
    }
    public override bool CanSearchSeries(List<string> genres, string? category = null)
    {
        if (category != null && (category.Equals("comics", StringComparison.InvariantCultureIgnoreCase) 
            || category.Equals("comic", StringComparison.InvariantCultureIgnoreCase) 
            || (category.Contains("comic", StringComparison.InvariantCultureIgnoreCase) && category.Contains("western", StringComparison.InvariantCultureIgnoreCase))))
            return false;
        return true;
    }
    private async Task<int> GetChaptersReadTotalAsync(string externalSeriesId, CancellationToken token)
    {
        try
        {
            _apiHttpClient.ApplyBearerToken(_accessToken);
            var query = @"
                query ($id: Int) {
                    MediaList(mediaId: $id, type: MANGA) {
                        progress
                    }
                }";

            var requestBody = new { query, variables = new { id = int.Parse(externalSeriesId) } };
            await ProviderRateLimits.EnforceAniListAsync();
            var response = await _apiHttpClient.PostAsJsonAsync(GraphQlEndpoint, requestBody, token);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<AniListMediaListResponse>(cancellationToken: token);
            return result?.Data?.MediaList?.Progress ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    // ── JSON Models ──

    private class AniListGraphQlResponse
    {
        public AniListData? Data { get; set; }
    }

    private class AniListData
    {
        public AniListPage? Page { get; set; }
    }

    private class AniListPage
    {
        public List<AniListMedia>? Media { get; set; }
    }

    private class AniListMedia
    {
        public int Id { get; set; }
        public int? IdMal { get; set; }
        public AniListTitle? Title { get; set; }
        public List<string>? Synonyms { get; set; }
        public AniListCoverImage? CoverImage { get; set; }
        public string? Format { get; set; }
        public string? Status { get; set; }
        public int? Chapters { get; set; }
        public int? Volumes { get; set; }
        public string? Description { get; set; }
        public int? AverageScore { get; set; }
        public AniListStartDate? StartDate { get; set; }
        public AniListStartDate? EndDate { get; set; }
        public List<string>? Genres { get; set; }
        public List<AniListTag>? Tags { get; set; }
        public List<AniListExternalLink>? ExternalLinks { get; set; }
    }

    private class AniListTag
    {
        public string? Name { get; set; }
    }

    private class AniListExternalLink
    {
        public string? Site { get; set; }
        public string? Url { get; set; }
        public int? Id { get; set; }
    }

    private class AniListMediaDetailResponse
    {
        public AniListMediaDetailData? Data { get; set; }
    }

    private class AniListMediaDetailData
    {
        public AniListMedia? Media { get; set; }
    }

    private class AniListTitle
    {
        public string? Romaji { get; set; }
        public string? English { get; set; }
        public string? Native { get; set; }
    }

    private class AniListCoverImage
    {
        public string? Large { get; set; }
        public string? ExtraLarge { get; set; }
    }

    private class AniListStartDate
    {
        public int? Year { get; set; }
    }

    private class AniListMediaListResponse
    {
        public AniListMediaListData? Data { get; set; }
    }

    private class AniListMediaListData
    {
        public AniListMediaListEntry? MediaList { get; set; }
    }

    private class AniListMediaListEntry
    {
        public int? Progress { get; set; }
        public int? ProgressVolumes { get; set; }
    }

    private class AniListViewerResponse
    {
        public AniListViewerData? Data { get; set; }
    }

    private class AniListViewerData
    {
        public AniListViewer? Viewer { get; set; }
    }

    private class AniListViewer
    {
        public int Id { get; set; }
    }
}