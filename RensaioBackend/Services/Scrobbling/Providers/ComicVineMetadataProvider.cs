using System.Net.Http.Json;
using RensaioBackend.Data;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Scrobbling.Abstractions;
using RensaioBackend.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace RensaioBackend.Services.Scrobbling.Providers;

/// <summary>
/// ComicVine metadata provider using API Key authentication.
/// The API key is stored in the database settings table (SettingEntity with name "Scrobbler_ComicVine_ApiKey")
/// and can be configured by the user via the frontend settings panel.
/// https://comicvine.gamespot.com/api
/// </summary>
public class ComicVineMetadataProvider : IExternalSeriesProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ComicVineMetadataProvider> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    private const string ApiBase = "https://comicvine.gamespot.com/api";

    public ExternalSeriesProvider ProviderType => ExternalSeriesProvider.ComicVine;
    public ProviderFeatures Features => ProviderFeatures.Metadata;
    public string DisplayName => "ComicVine";
    public string? Icon => ProviderIcons.ComicVine;
    public string? Link => "https://comicvine.gamespot.com/api/";
    public string? LinkDescription => "Get API Key";
    public string? SeriesUrlTemplate => "https://comicvine.gamespot.com/issue/4050-{0}/";
    public string? ImageTemplateUrl => null;
    public bool RequiresOAuth => false;
    public bool SupportsDirectAuth => false;

    public Task<ScrobblerTokenResult> AuthenticateDirectAsync(DirectAuthRequest request)
        => throw new NotSupportedException("ComicVine does not support direct authentication.");

    public void SetAccessToken(string accessToken, Guid uid) { /* No-op: ComicVine uses API key, not bearer tokens */ }

    public ComicVineMetadataProvider(
        IHttpClientFactory httpClientFactory,
        ILogger<ComicVineMetadataProvider> logger,
        IServiceScopeFactory scopeFactory)
    {
        _httpClient = httpClientFactory.CreateClient("Scrobbler_ComicVine");
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    private async Task<string?> GetApiKeyAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var setting = await db.Settings
            .FirstOrDefaultAsync(s => s.Name == "Scrobbler_ComicVine_ApiKey");
        return setting?.Value;
    }

    public Task<ScrobblerAuthUrlResult> GetAuthorizationUrlAsync(string redirectUri, string state)
        => throw new NotSupportedException("ComicVine does not support OAuth.");

    public Task<ScrobblerTokenResult> ExchangeCodeAsync(string code, string redirectUri)
        => throw new NotSupportedException("ComicVine does not support OAuth.");

    public Task<ScrobblerTokenResult> RefreshTokenAsync(string refreshToken)
        => throw new NotSupportedException("ComicVine does not support OAuth.");

    public Task<bool> ValidateApiKeyAsync(string apiKey)
        => Task.FromResult(!string.IsNullOrWhiteSpace(apiKey));

    public Task EnsureAuthenticatedAsync(Guid userId, CancellationToken token = default)
        => Task.CompletedTask;

    public async Task<List<ScrobblerSearchResult>> SearchSeriesAsync(string query, CancellationToken token = default)
    {
        _logger.LogInformation("ComicVine: searching for '{Query}'", query);
        var apiKey = await GetApiKeyAsync();
        if (string.IsNullOrEmpty(apiKey))
            return [];
        HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/search/?query={Uri.EscapeDataString(query)}&format=json&api_key={apiKey}&resources=volume");
        msg.Headers.UserAgent.ParseAdd("Rensaio 1.0");
        var response = await _httpClient.SendAsync(msg, token);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ComicVineResponse>(cancellationToken: token);
        var results = new List<ScrobblerSearchResult>();

        if (result?.Results == null) return results;

        foreach (var volume in result.Results)
        {
            var title = volume.Name ?? query;
            var titleCandidates = new List<string?> { volume.Name };
            if (!string.IsNullOrEmpty(volume.Aliases))
                titleCandidates.AddRange(
                    volume.Aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            results.Add(new ScrobblerSearchResult
            {
                ExternalId = volume.Id?.ToString() ?? string.Empty,
                Title = title,
                AlternateTitles = TitleListBuilder.BuildAlternates(title, titleCandidates),
                CoverUrl = volume.Image?.OriginalUrl ?? volume.Image?.MediumUrl,
                Type = "comic",
                ChapterCount = volume.CountOfIssues,
                Year = volume.StartYear
            });
        }

        return results;
    }

    public async Task<SeriesMetadataResult?> FetchSeriesMetadataAsync(string externalSeriesId, CancellationToken token = default)
    {
        _logger.LogInformation("ComicVine: fetching metadata for id '{Id}'", externalSeriesId);
        if (string.IsNullOrWhiteSpace(externalSeriesId)) return null;
        var apiKey = await GetApiKeyAsync();
        if (string.IsNullOrEmpty(apiKey)) return null;

        try
        {
            HttpRequestMessage msg = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/volume/4050-{Uri.EscapeDataString(externalSeriesId)}/?format=json&api_key={apiKey}&field_list=id,name,aliases,image,count_of_issues,description,start_year");
            msg.Headers.UserAgent.ParseAdd("Rensaio 1.0");
            var response = await _httpClient.SendAsync(msg, token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ComicVine detail failed for id '{Id}': HTTP {Status}", externalSeriesId, (int)response.StatusCode);
                return null;
            }
            
            var volume = await response.Content.ReadFromJsonAsync<ComicVineVolumeContainer>(cancellationToken: token);
            // The /volume/{id}/ detail endpoint returns the volume as a single object under
            // "results" (not an array, and no "volume" field).
            var v = volume?.Results;
            if (v == null) return null;

            var title = v.Name ?? externalSeriesId;
            var titleCandidates = new List<string?> { v.Name };
            if (!string.IsNullOrWhiteSpace(v.Aliases))
                titleCandidates.AddRange(
                    v.Aliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            return new SeriesMetadataResult
            {
                ExternalId = externalSeriesId,
                Title = title,
                MetaData = System.Text.Json.JsonSerializer.Serialize(v),
                LinkedSitesIds = [ $"comicvine:{externalSeriesId}" ],
                AlternativeTitles = TitleListBuilder.BuildAlternates(title, titleCandidates),
                CoverUrl = v.Image?.OriginalUrl ?? v.Image?.MediumUrl
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ComicVine detail failed for id '{Id}'", externalSeriesId);
            return null;
        }
    }

    public async Task<bool> ValidateTokenAsync(CancellationToken token = default)
    {
        var apiKey = await GetApiKeyAsync();
        return !string.IsNullOrEmpty(apiKey);
    }

    public List<string> FilterLookupTitles(IEnumerable<string> titles)
    {
        return titles.Where(a => !BangumiMetadataProvider.ContainsCJK(a)).ToList();
    }
    private static string[] NoCategories = ["manhwa", "manga", "manhua"];
    public bool CanSearchSeries(List<string> genres, string? category = null)
    {
        if (category != null && NoCategories.Contains(category.ToLowerInvariant())) return false;
        if (genres != null && genres.Any(g => NoCategories.Contains(g.ToLowerInvariant()))) return false;
        return true;
    }

    // ── JSON Models ──

    private class ComicVineResponse
    {
        public List<ComicVineVolume>? Results { get; set; }
    }

    // The /volume/{id}/ detail endpoint returns { "results": { ... } } — a single object,
    // not an array, with no "volume" field.
    private class ComicVineVolumeContainer
    {
        public ComicVineVolume? Results { get; set; }
    }

    private class ComicVineVolume
    {
        public int? Id { get; set; }
        public string? Name { get; set; }
        public string? Aliases { get; set; }
        public ComicVineImage? Image { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("count_of_issues")]
        public int? CountOfIssues { get; set; }
        public string? Description { get; set; }
        // ComicVine returns start_year as a string (e.g. "2022"), not an int.
        [System.Text.Json.Serialization.JsonPropertyName("start_year")]
        public string? StartYear { get; set; }
    }

    private class ComicVineImage
    {
        [System.Text.Json.Serialization.JsonPropertyName("original_url")]
        public string? OriginalUrl { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("medium_url")]
        public string? MediumUrl { get; set; }
    }
}