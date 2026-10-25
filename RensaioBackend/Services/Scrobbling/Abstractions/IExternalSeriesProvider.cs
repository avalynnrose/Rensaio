using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;

namespace RensaioBackend.Services.Scrobbling.Abstractions;

/// <summary>
/// Umbrella interface implemented by every external series provider (scrobbling sites like
/// AniList/MyAnimeList/Kitsu/MangaDex and metadata sites like MangaBaka/Bangumi/MangaUpdates/ComicVine).
/// Distinguishes these from the internal Mihon extension providers.
/// The read-state family sub-interface <see cref="IScrobblerProvider"/> extends this surface;
/// metadata-only providers implement this interface directly. Features are described by
/// <see cref="ProviderFeatures"/> (Scrobbling / Metadata).
/// </summary>
public interface IExternalSeriesProvider
{
    /// <summary>Identifies which external provider this is.</summary>
    ExternalSeriesProvider ProviderType { get; }

    /// <summary>
    /// Flag enum describing which features this provider supports (Scrobbling, Metadata, or both).
    /// Used to filter providers for the metadata link engine and drive UI grouping.
    /// </summary>
    ProviderFeatures Features { get; }

    /// <summary>Human-readable name (e.g. "MyAnimeList", "MangaBaka").</summary>
    string DisplayName { get; }

    /// <summary>Icon identifier for UI display.</summary>
    string? Icon { get; }

    /// <summary>Optional URL to the provider's website or settings page.</summary>
    string? Link { get; }

    /// <summary>Optional short description for the link.</summary>
    string? LinkDescription { get; }

    /// <summary>URL template for a series detail page; {0} = external series ID.</summary>
    string? SeriesUrlTemplate { get; }

    /// <summary>URL template for cover images; {0} = image identifier.</summary>
    string? ImageTemplateUrl { get; }

    /// <summary>Whether this provider requires OAuth (true) or an API key (false).</summary>
    bool RequiresOAuth { get; }

    /// <summary>Whether this provider supports direct password-based authentication.</summary>
    bool SupportsDirectAuth { get; }

    string[] SupportedTypes { get; }

    // ── Auth: OAuth ──
    Task<ScrobblerAuthUrlResult> GetAuthorizationUrlAsync(string redirectUri, string state);
    Task<ScrobblerTokenResult> ExchangeCodeAsync(string code, string redirectUri);
    Task<ScrobblerTokenResult> RefreshTokenAsync(string refreshToken, CancellationToken token = default);

    // ── Auth: Direct (password grant) ──
    Task<ScrobblerTokenResult> AuthenticateDirectAsync(DirectAuthRequest request);

    /// <summary>Sets the bearer access token on the underlying HTTP client.</summary>
    void SetAccessToken(string accessToken, Guid userid);

    // ── Auth: API Key ──
    Task<bool> ValidateApiKeyAsync(string apiKey);

    // ── Series Search & Matching ──
    Task<List<ScrobblerSearchResult>> SearchSeriesAsync(string query, CancellationToken token = default);

    // ── Metadata ──
    /// <summary>
    /// Fetches full metadata for a series by external ID. MetaData preserves provider-native JSON.
    /// Providers without a detail endpoint return null via this default implementation.
    /// </summary>
    Task<SeriesMetadataResult?> FetchSeriesMetadataAsync(string externalSeriesId, CancellationToken token = default)
        => Task.FromResult<SeriesMetadataResult?>(null);

    // ── Token Lifecycle ──
    Task EnsureAuthenticatedAsync(Guid userId, CancellationToken token = default);

    // ── Health ──
    Task<bool> ValidateTokenAsync(CancellationToken token = default);

    List<string> FilterLookupTitles(IEnumerable<string> titles);

    bool CanSearchSeries(List<string> genres, string? category = null);
}

/// <summary>
/// Family interface for providers that track read-state (scrobbling): AniList, MyAnimeList,
/// Kitsu, MangaDex. Adds read-state download/upload operations to the shared surface.
/// </summary>
public interface IScrobblerProvider : IExternalSeriesProvider
{
    /// <summary>
    /// Gets all chapters read for a series on the external service
    /// (chapterNumber -> lastPageRead).
    /// </summary>
    Task<Dictionary<decimal, float>> GetReadChaptersAsync(string externalSeriesId, CancellationToken token = default);

    /// <summary>
    /// Sets the read state for all chapters of a series on the external service.
    /// </summary>
    Task<bool> SetReadChaptersAsync(string externalSeriesId, Dictionary<decimal, float> chapterState, CancellationToken token = default);
}