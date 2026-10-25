using System.Net.Http.Json;
using RensaioBackend.Data;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Scrobbling.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace RensaioBackend.Services.Scrobbling.Providers;

/// <summary>
/// Abstract base class for scrobbler providers that use the central OAuth proxy
/// for authorization (token exchange/refresh) but call provider APIs directly
/// for search, read-state, and tracking operations.
///
/// OAuth flow nuance:
///   - 'code' grant providers (MyAnimeList, and AniList when a client secret is
///     configured) receive a refresh token and use RefreshTokenAsync.
///   - 'implicit' grant providers (AniList public client, no client secret) do
///     NOT receive a refresh token. Override <see cref="SupportsTokenRefresh"/>
///     to return false so EnsureAuthenticatedAsync skips the refresh branch —
///     the user simply re-authorizes when the access token expires.
/// </summary>
public abstract class ProxyScrobblerProvider : IScrobblerProvider
{
    protected readonly HttpClient _proxyHttpClient;
    protected HttpClient _apiHttpClient = null!; // Set by subclass constructors
    protected readonly ILogger _logger;
    protected readonly string _instanceKey;
    protected readonly string _proxyBaseUrl;
    protected readonly string _providerName;
    protected string? _accessToken;
    protected Guid? _userId;
    protected readonly ITokenStorageService _tokenStorage;
    protected readonly ScrobblerTokenProtector _tokenProtector;

    public ExternalSeriesProvider ProviderType { get; }
    public virtual ProviderFeatures Features => ProviderFeatures.Scrobbling | ProviderFeatures.Metadata;
    public string DisplayName { get; }
    public string? Icon { get; }
    public string? Link => null;
    public string? LinkDescription => null;
    public virtual string? SeriesUrlTemplate => null;
    public virtual string? ImageTemplateUrl => null;

    public virtual string[] SupportedTypes => new[] { "Manga", "Manhwa", "Manhua" };


    public bool RequiresOAuth => true;
    public bool SupportsDirectAuth => false;

    /// <summary>
    /// Whether this provider's OAuth flow issues a refresh token that can be
    /// used to silently renew an expired access token.
    ///
    /// Default: true (Authorization Code Grant — MyAnimeList, Kitsu, MangaDex,
    /// and AniList with a configured client secret).
    ///
    /// Implicit-grant providers (AniList public client) have NO refresh token.
    /// Override to false; EnsureAuthenticatedAsync will then leave the stored
    /// access token untouched when it expires instead of attempting a refresh.
    /// </summary>
    public virtual bool SupportsTokenRefresh => true;

    public Task<ScrobblerTokenResult> AuthenticateDirectAsync(DirectAuthRequest request)
        => throw new NotSupportedException("Proxy providers do not support direct authentication.");

    public void SetAccessToken(string accessToken, Guid useruid)
    {
        _accessToken = accessToken;
        _userId = useruid;
    }

    protected ProxyScrobblerProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ExternalSeriesProvider providerType,
        ILogger logger,
        ITokenStorageService tokenStorage,
        ScrobblerTokenProtector tokenProtector)
    {
        _proxyHttpClient = httpClientFactory.CreateClient("Scrobbler_Proxy");
        _logger = logger;
        _tokenStorage = tokenStorage;
        _tokenProtector = tokenProtector;

        _proxyBaseUrl = configuration.GetValue<string>("Scrobbling:ProxyUrl")?.TrimEnd('/')
                        ?? "https://auth.rensaio.net";
        _instanceKey = configuration.GetValue<string>("Scrobbling:Proxy:InstanceKey")
                       ?? configuration.GetValue<string>("Scrobbling:InstanceKey")
                       ?? string.Empty;

        ProviderType = providerType;
        DisplayName = providerType switch
        {
            ExternalSeriesProvider.AniList => "AniList",
            ExternalSeriesProvider.MyAnimeList => "MyAnimeList",
            ExternalSeriesProvider.Kitsu => "Kitsu",
            ExternalSeriesProvider.MangaDex => "MangaDex",
            _ => providerType.ToString()
        };
        Icon = providerType switch
        {
            ExternalSeriesProvider.MyAnimeList => ProviderIcons.MyAnimeList,
            ExternalSeriesProvider.AniList => ProviderIcons.AniList,
            ExternalSeriesProvider.Kitsu => ProviderIcons.Kitsu,
            ExternalSeriesProvider.MangaDex => ProviderIcons.MangaDex,
            _ => ProviderIcons.Placeholder
        };
        _providerName = providerType switch
        {
            ExternalSeriesProvider.AniList => "anilist",
            ExternalSeriesProvider.MyAnimeList => "myanimelist",
            ExternalSeriesProvider.Kitsu => "kitsu",
            ExternalSeriesProvider.MangaDex => "mangadex",
            _ => providerType.ToString().ToLowerInvariant()
        };
    }


    // ── OAuth Proxy Methods (concrete) ──

    public async Task<ScrobblerAuthUrlResult> GetAuthorizationUrlAsync(string redirectUri, string state)
    {
        _proxyHttpClient.DefaultRequestHeaders.Remove("X-Instance-Key");
        _proxyHttpClient.DefaultRequestHeaders.Add("X-Instance-Key", _instanceKey);

        // Proxy generates its own state and redirectUri — no body needed
        var response = await _proxyHttpClient.PostAsync(
            $"{_proxyBaseUrl}/api/oauth/{_providerName}/url", null);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ProxyAuthUrlResponse>();

        return new ScrobblerAuthUrlResult
        {
            AuthUrl = result?.AuthUrl ?? string.Empty,
            State = result?.State ?? state
        };
    }

    public async Task<ScrobblerTokenResult> ExchangeCodeAsync(string code, string redirectUri)
    {
        // ExchangeCodeAsync is called by ProxyCallback which retrieves tokens from proxy
        // The proxy no longer requires instanceSecret for token retrieval (token store is state-based)
        var state = HttpContextHelper.CurrentState;

        var request = new { state, instanceKey = _instanceKey };

        var response = await _proxyHttpClient.PostAsJsonAsync(
            $"{_proxyBaseUrl}/api/oauth/{_providerName}/token", request);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ProxyErrorResponse>();
            return new ScrobblerTokenResult
            {
                Success = false,
                ErrorMessage = error?.Error ?? "Failed to retrieve tokens from proxy"
            };
        }

        var result = await response.Content.ReadFromJsonAsync<ProxyTokenResponse>();
        return new ScrobblerTokenResult
        {
            Success = true,
            AccessToken = result?.AccessToken,
            RefreshToken = result?.RefreshToken,
            ExpiresAt = result?.ExpiresAt
        };
    }

    public virtual async Task<ScrobblerTokenResult> RefreshTokenAsync(string refreshToken, CancellationToken token = default)
    {
        // Implicit-grant providers (no refresh token) override this to return a
        // graceful failure instead of calling the proxy's /refresh endpoint.
        var request = new { refreshToken };

        _proxyHttpClient.DefaultRequestHeaders.Remove("X-Instance-Key");
        _proxyHttpClient.DefaultRequestHeaders.Add("X-Instance-Key", _instanceKey);

        var response = await _proxyHttpClient.PostAsJsonAsync(
            $"{_proxyBaseUrl}/api/oauth/{_providerName}/refresh", request, token);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<ProxyErrorResponse>();
            return new ScrobblerTokenResult
            {
                Success = false,
                ErrorMessage = error?.Error ?? "Failed to refresh token via proxy"
            };
        }

        var result = await response.Content.ReadFromJsonAsync<ProxyTokenResponse>();
        return new ScrobblerTokenResult
        {
            Success = true,
            AccessToken = result?.AccessToken,
            RefreshToken = result?.RefreshToken,
            ExpiresAt = result?.ExpiresAt
        };
    }
    public string GetUserExternalKey(string externalSeriesId) => $"{_userId}:{externalSeriesId}";

    /// <summary>
    /// Metadata detail fetch. Subclasses that support <see cref="ProviderFeatures.Metadata"/>
    /// override this to return provider-native detail JSON.
    /// </summary>
    public virtual Task<SeriesMetadataResult?> FetchSeriesMetadataAsync(string externalSeriesId, CancellationToken token = default)
        => Task.FromResult<SeriesMetadataResult?>(null);

    public Task<bool> ValidateApiKeyAsync(string apiKey) => Task.FromResult(false);

    public async Task<bool> ValidateTokenAsync(CancellationToken token = default)
    {
        try
        {
            _proxyHttpClient.DefaultRequestHeaders.Remove("X-Instance-Key");
            _proxyHttpClient.DefaultRequestHeaders.Add("X-Instance-Key", _instanceKey);

            var response = await _proxyHttpClient.GetAsync(
                $"{_proxyBaseUrl}/api/oauth/{_providerName}/validate", token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ── Token Lifecycle ──

    public async Task EnsureAuthenticatedAsync(Guid userId, CancellationToken token = default)
    {
        var (accessToken, refreshToken, expiresAt) = await _tokenStorage.LoadTokensAsync(userId, ProviderType, token);
        if (accessToken == null) return;

        SetAccessToken(accessToken, userId);

        // Implicit-grant providers (e.g. AniList without a client secret) receive
        // NO refresh token from the OAuth flow. There is nothing to silently
        // refresh — the user re-authorizes when the access token expires.
        if (!SupportsTokenRefresh) return;

        if (expiresAt.HasValue && expiresAt.Value < DateTime.UtcNow.AddMinutes(5))
        {
            if (string.IsNullOrEmpty(refreshToken)) return;

            var refreshResult = await RefreshTokenAsync(refreshToken, token);
            if (refreshResult.Success && refreshResult.AccessToken != null)
            {
                var encryptedAccess = _tokenProtector.Encrypt(refreshResult.AccessToken);
                var encryptedRefresh = refreshResult.RefreshToken != null
                    ? _tokenProtector.Encrypt(refreshResult.RefreshToken)
                    : null;

                await _tokenStorage.PersistRefreshedTokensAsync(userId, ProviderType,
                    encryptedAccess, encryptedRefresh, refreshResult.ExpiresAt, token);
                SetAccessToken(refreshResult.AccessToken, userId);
            }
        }
    }

    // ── Abstract API Methods (implemented by subclasses) ──

    public abstract Task<List<ScrobblerSearchResult>> SearchSeriesAsync(string query, CancellationToken token = default);
    public abstract Task<Dictionary<decimal, float>> GetReadChaptersAsync(string externalSeriesId, CancellationToken token = default);
    public abstract Task<bool> SetReadChaptersAsync(string externalSeriesId, Dictionary<decimal, float> chapterState, CancellationToken token = default);

    public virtual List<string> FilterLookupTitles(IEnumerable<string> titles)
    {
        return titles.ToList();
    }
    
    public virtual bool CanSearchSeries(List<string> genres, string? category = null)
    {
        return true;
    }

    // ── JSON Models ──

    private class ProxyAuthUrlResponse
    {
        public string AuthUrl { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
    }

    private class ProxyTokenResponse
    {
        public string AccessToken { get; set; } = string.Empty;
        public string? RefreshToken { get; set; }
        public DateTime? ExpiresAt { get; set; }
    }

    private class ProxyErrorResponse
    {
        public string Error { get; set; } = string.Empty;
    }
}


/// <summary>
/// Helper to pass the OAuth state from the callback request to the provider.
/// </summary>
public static class HttpContextHelper
{
    [ThreadStatic]
    public static string? CurrentState;
}

/// <summary>
/// Shared configuration model for scrobbler providers that use ClientId/ClientSecret/ApiKey.
/// </summary>
internal class ScrobblerConfiguration
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? ApiKey { get; set; }
}