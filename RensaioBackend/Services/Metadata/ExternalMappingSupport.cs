using Microsoft.EntityFrameworkCore;
using RensaioBackend.Data;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Scrobbling;
using RensaioBackend.Services.Scrobbling.Abstractions;
using Microsoft.Extensions.Logging;

namespace RensaioBackend.Services.Metadata;

/// <summary>
/// Shared provider-resolution helpers for the External Mappings and Contribution Mappings pages.
/// Centralizes:
///  - which metadata providers are "enabled" (public no-auth + authenticated) for a user,
///  - background/scan enabled-provider set,
///  - OAuth token pre-authentication before any provider API call,
///  - per-provider presentation meta (icon + series URL template),
///  - mapping-row → DTO projection and alt-title harvesting,
///  - provider enum parsing.
/// Both controllers delegate here so the two pages cannot drift apart.
/// </summary>
public sealed class ExternalMappingSupport
{
    /// <summary>
    /// Hard ceiling for a single provider's OAuth/token step. Without this a hung token/refresh
    /// endpoint (or a wedged SignalR/HTTP dependency) would block the whole sequential scan
    /// forever with no log — exactly the 4-hour silent stall this guards against.
    /// </summary>
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(30);

    private readonly AppDbContext _db;
    private readonly ExternalSeriesProviderFactory _factory;
    private readonly ILogger<ExternalMappingSupport> _logger;

    public ExternalMappingSupport(
        AppDbContext db,
        ExternalSeriesProviderFactory factory,
        ILogger<ExternalMappingSupport> logger)
    {
        _db = db;
        _factory = factory;
        _logger = logger;
    }

    /// <summary>
    /// Returns the ordered metadata providers "enabled" for the current user: providers that need
    /// no authentication (RequiresOAuth == false, e.g. public APIs) OR providers whose
    /// UserScrobblerConfig for the user holds a stored access token. Providers that require OAuth
    /// but aren't authenticated are excluded. A <c>null</c> user keeps only public providers.
    /// </summary>
    public async Task<List<IExternalSeriesProvider>> GetEnabledProvidersAsync(UserEntity? user, CancellationToken token)
    {
        var all = GetMetadataProvidersInOrder();

        if (user == null)
        {
            // Unauthenticated in the HTTP context: keep only public (no-auth) providers.
            return all.Where(p => !p.RequiresOAuth).ToList();
        }

        var configs = await _db.UserScrobblerConfigs
            .Where(c => c.UserId == user.Id)
            .ToListAsync(token);

        var authenticated = configs
            .Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.AccessToken))
            .Select(c => c.Provider)
            .ToList();

        return all.Where(p => !p.RequiresOAuth || authenticated.Contains(p.ProviderType)).ToList();
    }

    /// <summary>
    /// Providers that unattended scanning may query: public providers (no auth) plus OAuth-gated
    /// providers that have at least one enabled config carrying an access token. The background
    /// worker has no request user, so "enabled" = any authenticated config.
    /// </summary>
    public async Task<HashSet<ExternalSeriesProvider>> GetEnabledProviderSetAsync(CancellationToken token = default)
    {
        var configs = await _db.UserScrobblerConfigs
            .Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.AccessToken))
            .Select(c => c.Provider)
            .ToListAsync(token).ConfigureAwait(false);
        return GetMetadataProvidersInOrder()
            .Where(p => !p.RequiresOAuth || configs.Contains(p.ProviderType))
            .Select(p => p.ProviderType)
            .ToHashSet();
    }

    /// <summary>
    /// Ensures OAuth-gated providers carry a valid access token before any API call. Uses the first
    /// enabled UserScrobblerConfig for the provider (any enabled user's token is usable for reading
    /// provider metadata). Safe to call from both the External and Contribution flows.
    /// </summary>
    public async Task EnsureProvidersAuthenticatedAsync(
        IReadOnlyList<IExternalSeriesProvider> providers,
        CancellationToken token)
    {
        foreach (var provider in providers)
        {
            if (!provider.RequiresOAuth) continue;

            var config = await _db.UserScrobblerConfigs
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Provider == provider.ProviderType && c.IsEnabled, token)
                .ConfigureAwait(false);
            if (config == null) continue; // no enabled user → cannot authenticate (skip)

            try
            {
                // Bounded: never let a single provider's token refresh stall the whole scan.
                await provider.EnsureAuthenticatedAsync(config.UserId, token)
                    .WaitAsync(AuthTimeout, token)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Metadata: authentication for provider {Provider} exceeded {Seconds}s; continuing without it.",
                    provider.ProviderType, AuthTimeout.TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Metadata: failed to authenticate provider {Provider}", provider.ProviderType);
            }
        }
    }

    /// <summary>Ordered metadata providers (hub first), instantiated + metadata-capable only.</summary>
    public List<IExternalSeriesProvider> GetMetadataProvidersInOrder()
    {
        var order = new List<ExternalSeriesProvider>
        {
            ExternalSeriesProvider.MangaBaka,
            ExternalSeriesProvider.AniList,
            ExternalSeriesProvider.MangaUpdates,
            ExternalSeriesProvider.Bangumi,
            ExternalSeriesProvider.MangaDex,
            ExternalSeriesProvider.MyAnimeList,
            ExternalSeriesProvider.Kitsu,
            ExternalSeriesProvider.ComicVine,
            ExternalSeriesProvider.Metron,          // deferred — factory returns null → skipped
            ExternalSeriesProvider.GrandComicsDatabase // deferred — factory returns null → skipped
        };
        return order
            .Select(p => _factory.GetProvider(p))
            .Where(p => p != null && SeriesMetadataResolver.IsMetadataProvider(p))
            .ToList()!;
    }

    /// <summary>Builds the root-level per-provider presentation map (icon + series URL template) keyed by provider name.</summary>
    public static Dictionary<string, ExternalProviderMetaDto> BuildProviderMeta(IReadOnlyList<IExternalSeriesProvider> providers)
    {
        var result = new Dictionary<string, ExternalProviderMetaDto>();
        foreach (var p in providers)
        {
            result[p.ProviderType.ToString()] = new ExternalProviderMetaDto
            {
                Icon = p.Icon,
                SeriesUrlTemplate = p.SeriesUrlTemplate
            };
        }
        return result;
    }

    /// <summary>Builds a provider row from an existing global mapping, reusing its cover / titles / links.</summary>
    public static ExternalMappingsSeriesProviderDto MappingToProviderDto(SeriesMappingEntity m)
    {
        return new ExternalMappingsSeriesProviderDto
        {
            ProviderCoverUrl = m.SeriesCoverUrl, // provider cover from the mapping row only
            Provider = m.Provider,
            ExternalSeriesId = m.ExternalSeriesId,
            ExternalSeriesTitle = m.ExternalSeriesTitle,
            MappingStatus = m.MappingStatus,
            LinkedDate = m.LinkedDate,
            LinkedSitesIds = m.LinkedSitesIds,
            AlternativeTitles = m.AlternativeTitles // verbatim: empty in DB -> empty in response
        };
    }

    /// <summary>
    /// Local alternative titles for an unmatched series, harvested from the channel/provider sources.
    /// The main title is included first (visible in the UI even when the provider has no match).
    /// </summary>
    public static List<string> BuildAlternativeTitles(SeriesEntity series)
    {
        var titles = new List<string>();
        titles.Add(series.Title);
        foreach (var s in series.Sources)
        {
            if (string.IsNullOrWhiteSpace(s.Title)) continue;
            if (titles.Any(t => string.Equals(t, s.Title, StringComparison.OrdinalIgnoreCase))) continue;
            titles.Add(s.Title);
        }
        return titles;
    }

    public static ExternalSeriesProvider? ParseProvider(string provider)
    {
        return Enum.TryParse<ExternalSeriesProvider>(provider, true, out var parsed)
            ? parsed
            : null;
    }
}