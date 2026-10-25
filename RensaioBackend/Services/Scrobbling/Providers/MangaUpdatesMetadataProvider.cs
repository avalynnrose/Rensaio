using System.Net.Http.Json;
using System.Text.Json;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Scrobbling.Abstractions;
using RensaioBackend.Utils;
using Microsoft.Extensions.Logging;

namespace RensaioBackend.Services.Scrobbling.Providers;

/// <summary>
/// MangaUpdates metadata provider (api.mangaupdates.com, v1 API).
/// Verified live:
///   Search: POST https://api.mangaupdates.com/v1/series/search   body { "search": "...", "page": 1, "perPage": 25 }
///   Detail: GET  https://api.mangaupdates.com/v1/series/{series_id}
/// The detail payload exposes <c>title</c>, <c>associated</c> (related series titles),
/// <c>related_series</c> (with numeric IDs + URLs), <c>publications</c>, etc.
/// MangaUpdates does NOT expose AniList/MAL IDs — cross-site links arrive inbound
/// from MangaBaka (manga_updates.id = slug) and MangaDex (links.mu = slug).
/// Metadata-only provider.
/// </summary>
public class MangaUpdatesMetadataProvider : IExternalSeriesProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MangaUpdatesMetadataProvider> _logger;
    public string[] SupportedTypes => new[] { "Manga", "Manhwa", "Manhua" };

    private const string ApiBase = "https://api.mangaupdates.com/v1";

    public MangaUpdatesMetadataProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<MangaUpdatesMetadataProvider> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Scrobbler_MangaUpdates");
        _logger = logger;
    }

    public ExternalSeriesProvider ProviderType => ExternalSeriesProvider.MangaUpdates;
    public ProviderFeatures Features => ProviderFeatures.Metadata;
    public string DisplayName => "MangaUpdates";
    public string? Icon => ProviderIcons.MangaUpdates;
    public string? Link => "https://www.mangaupdates.com";
    public string? LinkDescription => null;
    public string? SeriesUrlTemplate => "https://www.mangaupdates.com/series/{0}";
    public string? ImageTemplateUrl => null;
    public bool RequiresOAuth => false;
    public bool SupportsDirectAuth => false;

    public Task<ScrobblerAuthUrlResult> GetAuthorizationUrlAsync(string redirectUri, string state)
        => throw new NotSupportedException("MangaUpdates does not support OAuth.");

    public Task<ScrobblerTokenResult> ExchangeCodeAsync(string code, string redirectUri)
        => throw new NotSupportedException("MangaUpdates does not support OAuth.");

    public Task<ScrobblerTokenResult> RefreshTokenAsync(string refreshToken, CancellationToken token = default)
        => throw new NotSupportedException("MangaUpdates does not support OAuth.");

    public Task<ScrobblerTokenResult> AuthenticateDirectAsync(DirectAuthRequest request)
        => throw new NotSupportedException("MangaUpdates does not support direct authentication.");

    public void SetAccessToken(string accessToken, Guid userid) { /* No-op: public API */ }

    public Task<bool> ValidateApiKeyAsync(string apiKey)
        => Task.FromResult(!string.IsNullOrWhiteSpace(apiKey));
    public bool CanSearchSeries(List<string> genres, string? category = null)
    {
        if (category != null && (category.Equals("comics", StringComparison.InvariantCultureIgnoreCase)
            || category.Equals("comic", StringComparison.InvariantCultureIgnoreCase)
            || (category.Contains("comic", StringComparison.InvariantCultureIgnoreCase) && category.Contains("western", StringComparison.InvariantCultureIgnoreCase))))
            return false;
        return true;
    }
    public Task EnsureAuthenticatedAsync(Guid userId, CancellationToken token = default)
        => Task.CompletedTask;

    public async Task<List<ScrobblerSearchResult>> SearchSeriesAsync(string query, CancellationToken token = default)
    {
        _logger.LogInformation("{provider}: searching for '{Query}'", ExternalSeriesProvider.MangaUpdates, query);
        var results = new List<ScrobblerSearchResult>();
        if (string.IsNullOrWhiteSpace(query))
            return results;

        try
        {
            var requestBody = new MangaUpdatesSearchRequest
            {
                Search = query,
                Page = 1,
                PerPage = 25
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/series/search")
            {
                Content = JsonContent.Create(requestBody)
            };
            await ProviderRateLimits.EnforceMangaUpdatesAsync();
            var response = await _httpClient.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{provider} search failed for '{Query}': HTTP {Status}", ExternalSeriesProvider.MangaUpdates, query, (int)response.StatusCode);
                return results;
            }

            var payload = await response.Content.ReadFromJsonAsync<MangaUpdatesSearchResponse>(cancellationToken: token);
            if (payload?.Results == null) return results;

            foreach (var entry in payload.Results)
            {
                var record = entry.Record;
                if (record == null) continue;

                // MangaUpdates stores/identifies by base36 slug (e.g. "njeqwry"); the API returns a
                // decimal series_id — translate to the canonical base36 ExternalId for the app.
                var externalId = record.SeriesId.HasValue
                    ? Base36Codec.Encode(record.SeriesId.Value)
                    : string.Empty;

                results.Add(new ScrobblerSearchResult
                {
                    ExternalId = externalId,
                    Title = record.Title ?? query,
                    AlternateTitles = [],
                    CoverUrl = record.Image?.Url?.Original ?? record.Image?.Url?.Thumb,
                    Type = record.Type,
                    ChapterCount = null,
                    Status = NormalizeStatus(record.Status),
                    Synopsis = record.Description,
                    Score = record.BayesianRating,
                    Year = record.Year
                });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "{provider} search failed for '{Query}'", ExternalSeriesProvider.MangaUpdates, query);
        }

        return results;
    }

    public async Task<SeriesMetadataResult?> FetchSeriesMetadataAsync(string externalSeriesId, CancellationToken token = default)
    {
        _logger.LogInformation("{provider}: fetching metadata for id '{Id}'", ExternalSeriesProvider.MangaUpdates, externalSeriesId);
        if (string.IsNullOrWhiteSpace(externalSeriesId))
            return null;

        // External id is stored as a base36 slug; the API expects the decimal long.
        var seriesIdLong = Base36Codec.Decode(externalSeriesId);
        if (seriesIdLong < 0)
        {
            _logger.LogWarning("{provider} detail invalid base36 id '{Id}'", ExternalSeriesProvider.MangaUpdates, externalSeriesId);
            return null;
        }

        try
        {
            var url = $"{ApiBase}/series/{seriesIdLong}";
            await ProviderRateLimits.EnforceMangaUpdatesAsync();
            var response = await _httpClient.GetAsync(url, token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{provider} detail failed for id '{Id}': HTTP {Status}", ExternalSeriesProvider.MangaUpdates, externalSeriesId, (int)response.StatusCode);
                return null;
            }

            var detail = await response.Content.ReadFromJsonAsync<MangaUpdatesSeriesDetail>(cancellationToken: token);
            if (detail == null || detail.SeriesId == null) return null;

            var titleCandidates = BuildTitleCandidates(detail);
            var title = TitleListBuilder.ResolvePrimary(titleCandidates, externalSeriesId)!;
            return new SeriesMetadataResult
            {
                ExternalId = Base36Codec.Encode((long)detail.SeriesId),
                Title = title,
                MetaData = JsonSerializer.Serialize(detail),
                LinkedSitesIds = [], // MangaUpdates exposes related series by title/ID only, no AniList/MAL IDs
                AlternativeTitles = TitleListBuilder.BuildAlternates(title, titleCandidates),
                CoverUrl = detail.Image?.Url?.Original ?? detail.Image?.Url?.Thumb
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "{provider} detail failed for id '{Id}'", ExternalSeriesProvider.MangaUpdates, externalSeriesId);
            return null;
        }
    }
    public List<string> FilterLookupTitles(IEnumerable<string> titles)
    {
        return titles.ToList();
    }
    public Task<bool> ValidateTokenAsync(CancellationToken token = default)
        => Task.FromResult(true);

    private static List<string?> BuildTitleCandidates(MangaUpdatesSeriesDetail detail)
    {
        var result = new List<string?> { detail.Title };
        if (detail.Associated != null)
        {
            foreach (var assoc in detail.Associated)
                result.Add(assoc.Title);
        }
        return result;
    }

    private static string? NormalizeStatus(string? status)
        => status;

    // ── JSON models (provider-native) ──
    // The MangaUpdates v1 API uses snake_case; every property is bound via [JsonPropertyName].

    private class MangaUpdatesSearchRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("search")] public string? Search { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("page")] public int Page { get; set; } = 1;
        [System.Text.Json.Serialization.JsonPropertyName("per_page")] public int PerPage { get; set; } = 25;
    }

    private class MangaUpdatesSearchResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("total_hits")] public int? TotalHits { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("page")] public int? Page { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("per_page")] public int? PerPage { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("results")] public List<MangaUpdatesSearchResult>? Results { get; set; }
    }

    private class MangaUpdatesSearchResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("record")] public MangaUpdatesRecord? Record { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("hit_title")] public string? HitTitle { get; set; }
    }

    private class MangaUpdatesRecord
    {
        [System.Text.Json.Serialization.JsonPropertyName("series_id")] public long? SeriesId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("title")] public string? Title { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("url")] public string? Url { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("description")] public string? Description { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("image")] public MangaUpdatesImage? Image { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("type")] public string? Type { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("year")] public string? Year { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("bayesian_rating")] public decimal? BayesianRating { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("rating_votes")] public int? RatingVotes { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("status")] public string? Status { get; set; }
    }

    private class MangaUpdatesImage
    {
        [System.Text.Json.Serialization.JsonPropertyName("url")] public MangaUpdatesImageUrl? Url { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("height")] public int? Height { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("width")] public int? Width { get; set; }
    }

    private class MangaUpdatesImageUrl
    {
        [System.Text.Json.Serialization.JsonPropertyName("original")] public string? Original { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("thumb")] public string? Thumb { get; set; }
    }

    private class MangaUpdatesSeriesDetail
    {
        [System.Text.Json.Serialization.JsonPropertyName("series_id")] public long? SeriesId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("title")] public string? Title { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("url")] public string? Url { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("description")] public string? Description { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("image")] public MangaUpdatesImage? Image { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("type")] public string? Type { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("year")] public string? Year { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("bayesian_rating")] public decimal? BayesianRating { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("status")] public string? Status { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("associated")] public List<MangaUpdatesAssociated>? Associated { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("related_series")] public List<MangaUpdatesRelatedSeries>? RelatedSeries { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("publications")] public List<MangaUpdatesPublication>? Publications { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("categories")] public List<MangaUpdatesCategory>? Categories { get; set; }
    }

    private class MangaUpdatesAssociated
    {
        [System.Text.Json.Serialization.JsonPropertyName("title")] public string? Title { get; set; }
    }

    private class MangaUpdatesRelatedSeries
    {
        [System.Text.Json.Serialization.JsonPropertyName("relation_id")] public long? RelationId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("relation_type")] public string? RelationType { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("related_series_id")] public long? RelatedSeriesId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("related_series_name")] public string? RelatedSeriesName { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("related_series_url")] public string? RelatedSeriesUrl { get; set; }
    }

    private class MangaUpdatesPublication
    {
        [System.Text.Json.Serialization.JsonPropertyName("publication_name")] public string? PublicationName { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("publisher_name")] public string? PublisherName { get; set; }
    }

    private class MangaUpdatesCategory
    {
        [System.Text.Json.Serialization.JsonPropertyName("series_id")] public long? SeriesId { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("category")] public string? Category { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("votes")] public int? Votes { get; set; }
    }
}