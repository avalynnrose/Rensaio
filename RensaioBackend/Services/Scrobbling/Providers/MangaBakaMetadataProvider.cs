using System.Net.Http.Json;
using System.Text.Json;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Metadata;
using RensaioBackend.Services.Scrobbling.Abstractions;
using RensaioBackend.Utils;
using Microsoft.Extensions.Logging;

namespace RensaioBackend.Services.Scrobbling.Providers;

/// <summary>
/// MangaBaka metadata provider (https://mangabaka.org — note: mangabaka.com is parked/for-sale;
/// the live open manga/light-novel database is at mangabaka.org, API root api.mangabaka.org).
/// API: FastAPI-style v2 REST.
///   Search: GET /v2/series/search?q={q}&limit={n}&page={p}
///   Detail: GET /v2/series/{id}
/// The search + detail payloads expose <c>source</c> — a direct cross-site link hub containing
/// anilist, my_anime_list, manga_updates, and kitsu IDs — plus a structured <c>titles</c> array.
/// Metadata-only: does not implement scrobbling read-state operations.
/// </summary>
public class MangaBakaMetadataProvider : IExternalSeriesProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MangaBakaMetadataProvider> _logger;
    private readonly SeriesMetadataResolver _resolver;
    public string[] SupportedTypes => new[] { "Manga", "Manhwa", "Manhua" };

    private const string ApiBase = "https://api.mangabaka.org/v2";

    public MangaBakaMetadataProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<MangaBakaMetadataProvider> logger,
        SeriesMetadataResolver resolver)
    {
        _httpClient = httpClientFactory.CreateClient("Scrobbler_MangaBaka");
        _logger = logger;
        _resolver = resolver;
    }

    public ExternalSeriesProvider ProviderType => ExternalSeriesProvider.MangaBaka;
    public ProviderFeatures Features => ProviderFeatures.Metadata;
    public string DisplayName => "MangaBaka";
    public string? Icon => ProviderIcons.MangaBaka;
    public string? Link => "https://mangabaka.org";
    public string? LinkDescription => null;
    public string? SeriesUrlTemplate => "https://mangabaka.org/manga/{0}";
    public string? ImageTemplateUrl => null;
    public bool RequiresOAuth => false;
    public bool SupportsDirectAuth => false;

    public Task<ScrobblerAuthUrlResult> GetAuthorizationUrlAsync(string redirectUri, string state)
        => throw new NotSupportedException("MangaBaka does not support OAuth.");

    public Task<ScrobblerTokenResult> ExchangeCodeAsync(string code, string redirectUri)
        => throw new NotSupportedException("MangaBaka does not support OAuth.");

    public Task<ScrobblerTokenResult> RefreshTokenAsync(string refreshToken, CancellationToken token = default)
        => throw new NotSupportedException("MangaBaka does not support OAuth.");

    public Task<ScrobblerTokenResult> AuthenticateDirectAsync(DirectAuthRequest request)
        => throw new NotSupportedException("MangaBaka does not support direct authentication.");

    public void SetAccessToken(string accessToken, Guid userid) { /* No-op: public API */ }

    public Task<bool> ValidateApiKeyAsync(string apiKey)
        => Task.FromResult(!string.IsNullOrWhiteSpace(apiKey));

    public Task EnsureAuthenticatedAsync(Guid userId, CancellationToken token = default)
        => Task.CompletedTask;

    public async Task<List<ScrobblerSearchResult>> SearchSeriesAsync(string query, CancellationToken token = default)
    {
        _logger.LogInformation("{provider}: searching for '{Query}'", ExternalSeriesProvider.MangaBaka, query);
        var results = new List<ScrobblerSearchResult>();
        if (string.IsNullOrWhiteSpace(query))
            return results;

        try
        {
            var url = $"{ApiBase}/series/search?q={Uri.EscapeDataString(query)}&limit=20&page=1";
            await ProviderRateLimits.EnforceMangaBakaSearchAsync();
            var response = await _httpClient.GetAsync(url, token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{provider} search failed for '{Query}': HTTP {Status}", ExternalSeriesProvider.MangaBaka, query, (int)response.StatusCode);
                return results;
            }

            var payload = await response.Content.ReadFromJsonAsync<MangaBakaSearchResponse>(cancellationToken: token);
            if (payload?.Data == null) return results;

            foreach (var series in payload.Data)
            {
                var titles = ExtractTitles(series);
                var title = titles.Count > 0 ? titles[0] : query;
                results.Add(new ScrobblerSearchResult
                {
                    ExternalId = series.Id?.ToString(CultureInvariant) ?? string.Empty,
                    Title = title,
                    AlternateTitles = TitleListBuilder.BuildAlternates(title, titles),
                    LinkedSitesIds = ExtractLinkedSites(series),
                    CoverUrl = series.Cover?.Raw,
                    Type = series.Type,
                    ChapterCount = series.TotalChapters,
                    Status = NormalizeStatus(series.Status),
                    Synopsis = series.Description,
                    Year = series.Published?.StartDate?.Substring(0, 4)
                });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "{provider} search failed for '{Query}'", ExternalSeriesProvider.MangaBaka, query);
        }

        return results;
    }

    public async Task<SeriesMetadataResult?> FetchSeriesMetadataAsync(string externalSeriesId, CancellationToken token = default)
    {
        _logger.LogInformation("{provider}: fetching metadata for id '{Id}'", ExternalSeriesProvider.MangaBaka, externalSeriesId);
        if (string.IsNullOrWhiteSpace(externalSeriesId))
            return null;

        try
        {
            var url = $"{ApiBase}/series/{Uri.EscapeDataString(externalSeriesId)}";
            await ProviderRateLimits.EnforceMangaBakaDefaultAsync();
            var response = await _httpClient.GetAsync(url, token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{provider} detail failed for id '{Id}': HTTP {Status}", ExternalSeriesProvider.MangaBaka, externalSeriesId, (int)response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<MangaBakaDetailResponse>(cancellationToken: token);
            var series = payload?.Data;
            if (series == null) return null;

            var titles = ExtractTitles(series);
            var title = titles.Count > 0 ? titles[0] : externalSeriesId;
            return new SeriesMetadataResult
            {
                ExternalId = series.Id?.ToString(CultureInvariant) ?? externalSeriesId,
                Title = title,
                MetaData = JsonSerializer.Serialize(series),
                LinkedSitesIds = ExtractLinkedSites(series),
                AlternativeTitles = TitleListBuilder.BuildAlternates(title, titles),
                CoverUrl = series.Cover?.Raw
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "{provider} detail failed for id '{Id}'", ExternalSeriesProvider.MangaBaka, externalSeriesId);
            return null;
        }
    }

    public Task<bool> ValidateTokenAsync(CancellationToken token = default)
        => Task.FromResult(true);

    private static System.Globalization.CultureInfo CultureInvariant => System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>
    /// all titles from titles[], deduped case-insensitively, main/primary title first.
    /// </summary>
    private static List<string> ExtractTitles(MangaBakaSeries series)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var primary = series.Titles?.FirstOrDefault(t => t.IsPrimary == true)?.Title;

        void Add(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return;
            if (seen.Add(title)) result.Add(title);
        }

        Add(primary);
        if (series.Titles != null)
        {
            foreach (var t in series.Titles)
                Add(t.Title);
        }
        return result;
    }

    /// <summary>
    /// source.{anilist,my_anime_list,manga_updates,kitsu} -> canonical "site:id".
    /// </summary>
    private List<string> ExtractLinkedSites(MangaBakaSeries series)
    {
        var links = new List<string>();
        if (series.Source == null) return links;

        void Add(string? siteKey, string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            var canonical = _resolver.NormalizeSiteSlug(siteKey, ProviderType);
            if (canonical == null) return;
            // "0" is MangaBaka's placeholder for a missing cross-site link (e.g. no MAL entry).
            if (long.TryParse(id.Trim(), out long numeric) && numeric <= 0) return;
            // Normalize against the CANONICAL slug (the legacy MangaUpdates id remap only
            // applies to mangaupdates ids; using it for every site was incorrect).
            string nid = _resolver.NormalizeId(canonical, id) ?? id;
            links.Add($"{canonical}:{nid}");
        }
        if (series.Source.Anilist?.Id != null) Add("anilist", series.Source.Anilist.Id);
        if (series.Source.MyAnimeList?.Id != null) Add("my_anime_list", series.Source.MyAnimeList.Id);
        if (series.Source.MangaUpdates?.Id != null) Add("manga_updates", series.Source.MangaUpdates.Id);
        if (series.Source.Kitsu?.Id != null) Add("kitsu", series.Source.Kitsu.Id);

        return links.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? NormalizeStatus(string? status)
        => status?.ToLowerInvariant() switch
        {
            "releasing" => "Ongoing",
            "completed" => "Completed",
            "hiatus" => "Hiatus",
            "cancelled" => "Cancelled",
            _ => status
        };

    public List<string> FilterLookupTitles(IEnumerable<string> titles)
    {
        return titles.ToList();
    }

    public bool CanSearchSeries(List<string> genres, string? category = null)
    {
        // MangaBaka is a manga/light-novel hub — western comics are not indexed.
        if (category != null && (category.Equals("comics", StringComparison.InvariantCultureIgnoreCase)
            || category.Equals("comic", StringComparison.InvariantCultureIgnoreCase)
            || (category.Contains("comic", StringComparison.InvariantCultureIgnoreCase) && category.Contains("western", StringComparison.InvariantCultureIgnoreCase))))
            return false;
        return true;
    }

    // ── JSON models (provider-native) ──

    private class MangaBakaSearchResponse
    {
        public int? Status { get; set; }
        public List<MangaBakaSeries>? Data { get; set; }
    }

    private class MangaBakaDetailResponse
    {
        public int? Status { get; set; }
        public MangaBakaSeries? Data { get; set; }
    }

    private class MangaBakaSeries
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")] public int? Id { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("state")] public string? State { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("canonical_url")] public string? CanonicalUrl { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("authors")] public List<string>? Authors { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("artists")] public List<string>? Artists { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("description")] public string? Description { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("published")] public MangaBakaPublished? Published { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("status")] public string? Status { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("is_licensed")] public bool? IsLicensed { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("content_rating")] public string? ContentRating { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("type")] public string? Type { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("rating")] public double? Rating { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("publishers")] public List<MangaBakaPublisher>? Publishers { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("source")] public MangaBakaSource? Source { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("final_volume")] public int? FinalVolume { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("total_chapters")] public int? TotalChapters { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("titles")] public List<MangaBakaTitle>? Titles { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("cover")] public MangaBakaCover? Cover { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("anime")] public MangaBakaAnime? Anime { get; set; }
    }

    private class MangaBakaPublished
    {
        [System.Text.Json.Serialization.JsonPropertyName("start_date")] public string? StartDate { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("end_date")] public string? EndDate { get; set; }
    }

    private class MangaBakaPublisher
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("type")] public string? Type { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("note")] public string? Note { get; set; }
    }

    private class MangaBakaSource
    {
        [System.Text.Json.Serialization.JsonPropertyName("anilist")] public MangaBakaSourceEntry? Anilist { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("my_anime_list")] public MangaBakaSourceEntry? MyAnimeList { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("manga_updates")] public MangaBakaSourceEntry? MangaUpdates { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("kitsu")] public MangaBakaSourceEntry? Kitsu { get; set; }
    }

    private class MangaBakaSourceEntry
    {
        // MangaUpdates uses a string slug (e.g. "njeqwry"); all other sources use int IDs.
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleIdConverter))]
        public string? Id { get; set; }

        // Bangumi/MangaBaka return rating as number (e.g. 9.2) OR null; tolerate both.
        [System.Text.Json.Serialization.JsonPropertyName("rating")] public object? Rating { get; set; }
    }

    /// <summary>
    /// Accepts either a JSON number (AniList/MAL/Kitsu) or a JSON string (MangaUpdates slug).
    /// </summary>
    private sealed class FlexibleIdConverter : System.Text.Json.Serialization.JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                var num = reader.GetInt64();
                return num.ToString(CultureInvariant);
            }
            if (reader.TokenType == JsonTokenType.String)
                return reader.GetString();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, string? value, System.Text.Json.JsonSerializerOptions options)
            => writer.WriteStringValue(value);

        private static System.Globalization.CultureInfo CultureInvariant => System.Globalization.CultureInfo.InvariantCulture;
    }

    private class MangaBakaTitle
    {
        [System.Text.Json.Serialization.JsonPropertyName("language")] public string? Language { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("traits")] public List<string>? Traits { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("title")] public string? Title { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("is_primary")] public bool? IsPrimary { get; set; }
    }

    private class MangaBakaCover
    {
        [System.Text.Json.Serialization.JsonPropertyName("raw")] public string? Raw { get; set; }
    }

    private class MangaBakaAnime
    {
        [System.Text.Json.Serialization.JsonPropertyName("exists")] public bool? Exists { get; set; }
    }
}