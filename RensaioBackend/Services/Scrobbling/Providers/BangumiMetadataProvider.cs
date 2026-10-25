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
/// Bangumi / bgm.tv metadata provider.
/// API (verified live):
///   Search: POST https://api.bgm.tv/v0/search/subjects  body { "keywords": "...", "filter": { "type": [1,2] }, "limit": 10, "offset": 0 }
///   Detail: GET  https://api.bgm.tv/v0/subjects/{id}
/// Bangumi subject types: 1=book (manga/novel), 2=anime, 3=music, 4=game, 6=real.
/// The payloads expose <c>name</c>/<c>name_cn</c>/<c>alias</c> and rich infobox metadata,
/// but no direct cross-site AniList/MAL IDs — Bangumi links are typically inbound-only.
/// Metadata-only provider.
/// </summary>
public class BangumiMetadataProvider : IExternalSeriesProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<BangumiMetadataProvider> _logger;
    private readonly SeriesMetadataResolver _resolver;
    public string[] SupportedTypes => new[] { "Manga", "Manhwa", "Manhua", "Comics" };
    private const string ApiBase = "https://api.bgm.tv/v0";

    public BangumiMetadataProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<BangumiMetadataProvider> logger,
        SeriesMetadataResolver resolver)
    {
        _httpClient = httpClientFactory.CreateClient("Scrobbler_Bangumi");
        _logger = logger;
        _resolver = resolver;
    }

    public ExternalSeriesProvider ProviderType => ExternalSeriesProvider.Bangumi;
    public ProviderFeatures Features => ProviderFeatures.Metadata;
    public string DisplayName => "Bangumi";
    public string? Icon => ProviderIcons.Bangumi;
    public string? Link => "https://bgm.tv";
    public string? LinkDescription => null;
    public string? SeriesUrlTemplate => "https://bgm.tv/subject/{0}";
    public string? ImageTemplateUrl => null;
    public bool RequiresOAuth => false;
    public bool SupportsDirectAuth => false;

    public Task<ScrobblerAuthUrlResult> GetAuthorizationUrlAsync(string redirectUri, string state)
        => throw new NotSupportedException("Bangumi does not support OAuth via this flow.");

    public Task<ScrobblerTokenResult> ExchangeCodeAsync(string code, string redirectUri)
        => throw new NotSupportedException("Bangumi does not support OAuth via this flow.");

    public Task<ScrobblerTokenResult> RefreshTokenAsync(string refreshToken, CancellationToken token = default)
        => throw new NotSupportedException("Bangumi does not support OAuth via this flow.");

    public Task<ScrobblerTokenResult> AuthenticateDirectAsync(DirectAuthRequest request)
        => throw new NotSupportedException("Bangumi does not support direct authentication.");

    public void SetAccessToken(string accessToken, Guid userid) { /* No-op: public API */ }

    public Task<bool> ValidateApiKeyAsync(string apiKey)
        => Task.FromResult(!string.IsNullOrWhiteSpace(apiKey));

    public Task EnsureAuthenticatedAsync(Guid userId, CancellationToken token = default)
        => Task.CompletedTask;

    public async Task<List<ScrobblerSearchResult>> SearchSeriesAsync(string query, CancellationToken token = default)
    {
        _logger.LogInformation("{provider}: searching for '{Query}'", ExternalSeriesProvider.Bangumi, query);
        var results = new List<ScrobblerSearchResult>();
        if (string.IsNullOrWhiteSpace(query))
            return results;

        try
        {
            // Subject types 1 (book) + 2 (anime): covers manga, light novels, and anime-origin series.
            var requestBody = new BangumiSearchRequest
            {
                Keyword = query,
                Filter = new BangumiSearchFilter { Type = new List<int> { 1 } },
                Sort = "match",
                Limit = 20,
                Offset = 0
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/search/subjects")
            {
                Content = JsonContent.Create(requestBody)
            };
            await ProviderRateLimits.EnforceBangumiAsync();
            var response = await _httpClient.SendAsync(request, token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{provider} search failed for '{Query}': HTTP {Status}", ExternalSeriesProvider.Bangumi, query, (int)response.StatusCode);
                return results;
            }

            var payload = await response.Content.ReadFromJsonAsync<BangumiSearchResponse>(cancellationToken: token);
            if (payload?.Data == null) return results;

            foreach (var subject in payload.Data)
            {
                var titleCandidates = new List<string?> { subject.Name, subject.NameCn };
                var title = TitleListBuilder.ResolvePrimary(titleCandidates, query)!;

                results.Add(new ScrobblerSearchResult
                {
                    ExternalId = subject.Id?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                    Title = title,
                    AlternateTitles = TitleListBuilder.BuildAlternates(title, titleCandidates),
                    CoverUrl = subject.Images?.Large ?? subject.Images?.Common,
                    Type = BangumiTypeToText(subject.Type),
                    Status = null,
                    Synopsis = subject.Summary,
                    Score = subject.Rating?.Score,
                    Year = subject.Date?.Length >= 4 ? subject.Date.Substring(0, 4) : null
                });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "{provider} search failed for '{Query}'", ExternalSeriesProvider.Bangumi, query);
        }

        return results;
    }

    public async Task<SeriesMetadataResult?> FetchSeriesMetadataAsync(string externalSeriesId, CancellationToken token = default)
    {
        _logger.LogInformation("{provider}: fetching metadata for id '{Id}'", ExternalSeriesProvider.Bangumi, externalSeriesId);
        if (string.IsNullOrWhiteSpace(externalSeriesId))
            return null;

        try
        {
            var url = $"{ApiBase}/subjects/{Uri.EscapeDataString(externalSeriesId)}";
            await ProviderRateLimits.EnforceBangumiAsync();
            var response = await _httpClient.GetAsync(url, token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{provider} detail failed for id '{Id}': HTTP {Status}", ExternalSeriesProvider.Bangumi, externalSeriesId, (int)response.StatusCode);
                return null;
            }

            var subject = await response.Content.ReadFromJsonAsync<BangumiSubject>(cancellationToken: token);
            if (subject == null) return null;

            var titleCandidates = new List<string?> { subject.Name, subject.NameCn };
            if (subject.Alias != null)
                titleCandidates.AddRange(subject.Alias);
            // The 别名 (alias) infobox entry carries an array of {k, v} pairs; the v values are aliases.
            if (subject.Infobox != null)
            {
                foreach (var box in subject.Infobox)
                {
                    if (!string.Equals(box.Key, "别名", StringComparison.OrdinalIgnoreCase) || box.Value == null)
                        continue;

                    if (box.Value is JsonElement aliasElement && aliasElement.ValueKind == JsonValueKind.Array)
                    {
                        var aliasPairs = JsonSerializer.Deserialize<List<BangumiInfoBoxValue>>(aliasElement.GetRawText());
                        if (aliasPairs != null)
                        {
                            foreach (var pair in aliasPairs)
                                titleCandidates.Add(pair.Value);
                        }
                    }
                }
            }
            var title = TitleListBuilder.ResolvePrimary(titleCandidates, externalSeriesId)!;

            return new SeriesMetadataResult
            {
                ExternalId = externalSeriesId,
                Title = title,
                MetaData = JsonSerializer.Serialize(subject),
                LinkedSitesIds = [], // Bangumi does not expose cross-site IDs in these payloads
                AlternativeTitles = TitleListBuilder.BuildAlternates(title, titleCandidates),
                CoverUrl = subject.Images?.Large ?? subject.Images?.Common ?? subject.Images?.Medium
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "{provider} detail failed for id '{Id}'", ExternalSeriesProvider.Bangumi, externalSeriesId);
            return null;
        }
    }
    /// <summary>
    /// Returns <c>true</c> when the text contains any East-Asian (Japanese, Korean
    /// or Chinese) script. Covers Hiragana/Katakana/Kanji for Japanese, Hangul for
    /// Korean, and Han ideographs (plus CJK extensions) for Chinese.
    /// </summary>
    public static bool ContainsCJK(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (var rune in text.EnumerateRunes())
        {
            int c = rune.Value;

            // ── Japanese ──
            // Hiragana
            if (c is >= 0x3040 and <= 0x309F)
                return true;
            // Katakana
            if (c is >= 0x30A0 and <= 0x30FF)
                return true;
            // Katakana Phonetic Extensions
            if (c is >= 0x31F0 and <= 0x31FF)
                return true;
            // Half-width Katakana
            if (c is >= 0xFF65 and <= 0xFF9F)
                return true;

            // ── Korean (Hangul) ──
            // Hangul Jamo
            if (c is >= 0x1100 and <= 0x11FF)
                return true;
            // Hangul Jamo Extended-A
            if (c is >= 0xA960 and <= 0xA97F)
                return true;
            // Hangul Compatibility Jamo
            if (c is >= 0x3130 and <= 0x318F)
                return true;
            // Hangul Syllables
            if (c is >= 0xAC00 and <= 0xD7A3)
                return true;
            // Hangul Jamo Extended-B
            if (c is >= 0xD7B0 and <= 0xD7FF)
                return true;
            // Half-width Hangul
            if (c is >= 0xFFA0 and <= 0xFFDC)
                return true;

            // ── Chinese (Han) ──
            // CJK Radicals Supplement
            if (c is >= 0x2E80 and <= 0x2EFF)
                return true;
            // Kangxi Radicals
            if (c is >= 0x2F00 and <= 0x2FDF)
                return true;
            // CJK Unified Ideographs Extension A
            if (c is >= 0x3400 and <= 0x4DBF)
                return true;
            // CJK Unified Ideographs
            if (c is >= 0x4E00 and <= 0x9FFF)
                return true;
            // CJK Compatibility Ideographs
            if (c is >= 0xF900 and <= 0xFAFF)
                return true;
            // CJK Extensions B–I
            if (c is >= 0x20000 and <= 0x323AF)
                return true;
            // CJK Compatibility Ideographs Supplement
            if (c is >= 0x2F800 and <= 0x2FA1F)
                return true;
            // Full-width punctuation / forms (common in CJK text)
            if (c is >= 0xFF00 and <= 0xFF60)
                return true;
        }

        return false;
    }

    public List<string> FilterLookupTitles(IEnumerable<string> titles)
    {
        return titles.Where(a => ContainsCJK(a)).ToList();
    }

    public Task<bool> ValidateTokenAsync(CancellationToken token = default)
        => Task.FromResult(true);

    /// <summary>The <see cref="SeriesMetadataResolver"/> is kept for future linked-site support.</summary>
    private static string? BangumiTypeToText(int? type)
        => type switch
        {
            1 => "manga",
            2 => "anime",
            3 => "music",
            4 => "game",
            6 => "real",
            _ => null
        };

    public bool CanSearchSeries(List<string> genres, string? category = null)
    {
        return true;
    }

    // ── JSON models (provider-native) ──

    private class BangumiSearchRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("keyword")] public string? Keyword { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("filter")] public BangumiSearchFilter? Filter { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("limit")] public int Limit { get; set; } = 20;
        [System.Text.Json.Serialization.JsonPropertyName("sort")] public string Sort = "match";
        [System.Text.Json.Serialization.JsonPropertyName("offset")] public int Offset { get; set; } = 0;
    }

    private class BangumiSearchFilter
    {
        [System.Text.Json.Serialization.JsonPropertyName("type")] public List<int>? Type { get; set; }
    }

    private class BangumiSearchResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("data")] public List<BangumiSubject>? Data { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("total")] public int? Total { get; set; }
    }

    private class BangumiSubject
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")] public int? Id { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("name_cn")] public string? NameCn { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("alias")] public List<string>? Alias { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("date")] public string? Date { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("platform")] public string? Platform { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("summary")] public string? Summary { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("type")] public int? Type { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("images")] public BangumiImages? Images { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("rating")] public BangumiRating? Rating { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("tags")] public List<BangumiTag>? Tags { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("infobox")] public List<BangumiInfoBox>? Infobox { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("volumes")] public int? Volumes { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("eps")] public int? Eps { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("nsfw")] public bool? Nsfw { get; set; }
    }

    private class BangumiImages
    {
        [System.Text.Json.Serialization.JsonPropertyName("large")] public string? Large { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("common")] public string? Common { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("medium")] public string? Medium { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("small")] public string? Small { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("grid")] public string? Grid { get; set; }
    }

    private class BangumiRating
    {
        [System.Text.Json.Serialization.JsonPropertyName("rank")] public int? Rank { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("total")] public int? Total { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("score")] public decimal? Score { get; set; }
    }

    private class BangumiTag
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")] public string? Name { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("count")] public int? Count { get; set; }
    }

    private class BangumiInfoBox
    {
        [System.Text.Json.Serialization.JsonPropertyName("key")] public string? Key { get; set; }
        // Value can be a string OR an array (e.g. multiple authors) → tolerate both.
        [System.Text.Json.Serialization.JsonPropertyName("value")] public object? Value { get; set; }
    }

    /// <summary>An entry in a Bangumi infobox array value: {k, v} key/value pair (e.g. 别名 aliases).</summary>
    private class BangumiInfoBoxValue
    {
        [System.Text.Json.Serialization.JsonPropertyName("k")] public string? K { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("v")] public string? Value { get; set; }
    }
}