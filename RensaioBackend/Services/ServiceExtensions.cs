using Rensaio.Resilience;
using RensaioBackend.Extensions;
using RensaioBackend.Migration;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Auth;
using RensaioBackend.Services.Background;
using RensaioBackend.Services.Bridge;
using RensaioBackend.Services.Daily;
using RensaioBackend.Services.Downloads;
using RensaioBackend.Services.Helpers;
using RensaioBackend.Services.Images;
using RensaioBackend.Services.Images.Providers;
using RensaioBackend.Services.Import;
using RensaioBackend.Services.Jobs;
using RensaioBackend.Services.Jobs.Settings;
using RensaioBackend.Services.Opds;
using RensaioBackend.Services.Providers;
using RensaioBackend.Services.ReadState;
using RensaioBackend.Services.Scrobbling;
using RensaioBackend.Services.Scrobbling.Abstractions;
using RensaioBackend.Services.Search;
using RensaioBackend.Services.Series;
using RensaioBackend.Services.Settings;
using RensaioBackend.Services.Users;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net.Http.Headers;

namespace RensaioBackend.Services
{
    public static class ServiceExtensions
    {
        public static IServiceCollection AddImportService(this IServiceCollection services)
        {
            services.TryAddScoped<SeriesScanner>();
            services.TryAddScoped<SeriesComparer>();
            services.TryAddScoped<ImportQueryService>();
            services.TryAddScoped<ImportCommandService>();
            services.TryAddScoped<UserImportService>();
            return services;
        }

        public static IServiceCollection AddRensaioJsonService(this IServiceCollection services)
        {
            // Singleton: shared per-file lock state across all services
            services.TryAddSingleton<RensaioJsonService>();
            return services;
        }

        public static IServiceCollection AddSeriesServices(this IServiceCollection services)
        {
            // Specialized series services
            services.TryAddScoped<SeriesQueryService>();
            services.TryAddScoped<SeriesCommandService>();
            services.TryAddScoped<SeriesProviderService>();
            services.TryAddScoped<SeriesArchiveService>();
            services.TryAddScoped<CadenceCalculationService>();
            
            // Series state sync service - central authority for rensaio.json sync
            services.TryAddScoped<SeriesStateService>();
            
            return services;
        }

        public static IServiceCollection AddJobServices(this IServiceCollection services)
        {
            // Core job services
            services.TryAddScoped<JobManagementService>();
            services.TryAddScoped<JobBusinessService>();
            services.TryAddScoped<JobExecutionService>();
            
            // Configuration and supporting services
            services.TryAddSingleton<JobsSettings>();
            services.TryAddScoped<JobHubReportService>();
            
            return services;
        }
        public static IServiceCollection AddHelperServices(this IServiceCollection services)
        {
            services.TryAddScoped<SettingsService>();

            services.AddScoped<IImageProvider, UrlImageProvider>();
            services.AddScoped<IImageProvider, ExtensionsImageProvider>();
            services.AddScoped<IImageProvider, StorageImageProvider>();
            // Corrects cover URLs whose host was abandoned by an extension (e.g. a contribution-DB
            // cover still on the old domain) onto the source's current homeUrl/mirror domains, and
            // remembers the correction in memory so later covers skip the dead host. Scoped: it uses
            // the scoped MihonBridgeService for the (cached) extension-metadata lookup.
            services.TryAddScoped<CoverUrlDomainResolver>();
            // Scoped: the bounded URL/ETag caches are static, so they are already shared
            // across all scoped consumers regardless of lifetime. A singleton must NOT
            // capture the scoped AppDbContext (and scoped IImageProviders) in its
            // constructor — that shares one DbContext across concurrent requests and
            // throws "A second operation was started on this context instance.
            services.TryAddScoped<ThumbCacheService>();
            // Image/ETag cache client: the shared resilience handler retries transient transport /
            // 5xx / 429 failures (and circuit-breaks per image host). Mid-stream body failures that
            // surface while copying the response are additionally retried inside UrlImageProvider
            // (see UpdateCacheWithRemoteAsync), because they occur after SendAsync returns.
            services.AddHttpClient(nameof(ThumbCacheService), SetHttpClientHeaders).AddProviderResilience();
            services.TryAddScoped<IImageFactory, NetVipsImageFactory>();
            services.TryAddScoped<ArchiveHelperService>();
            services.TryAddScoped<DailyService>();
            services.TryAddScoped<Status.StatusEvaluationService>();
            services.TryAddScoped<MihonBridgeService>();
            services.TryAddScoped<MigrationService>();
            services.TryAddScoped<NouisanceFixer20ExtraLarge>();
            return services;
        }
        public static IServiceCollection AddScrobblingServices(this IServiceCollection services, IConfiguration configuration)
        {
            // Shared provider resilience tuning (exponential retry + per-host circuit breaker).
            services.AddProviderResilience();

            services.TryAddScoped<Contributions.ContributionPropagationService>();
            services.TryAddScoped<Contributions.ContributionMappingService>();
            services.TryAddScoped<Contributions.RensaioMappingCascadeService>();
            // Reconciles a contribution series' title associations against Auto/User links,
            // tombstoning association rows that exist only because of a Blocked provider.
            services.TryAddScoped<Contributions.ContributionTitleHeuristicsService>();
            // Repairs the contribution graph structure (orphaned Series + their SeriesTitles).
            services.TryAddScoped<Contributions.ContributionGraphRepairService>();
            // One-shot: reclaims historical local-only tombstone bloat (deletes -1/-3 rows, orphans
            // and VACUUM), guarded by PRAGMA user_version so it runs at most once.
            services.TryAddScoped<Contributions.ContributionTombstoneCleanupService>();
            // Low-level contributor.db graph writer shared by propagation and the seed sweep.
            services.TryAddScoped<Contributions.ContributionGraphWriter>();
            // Resolves a contribution source entry's relative url to the absolute page url via the
            // owning extension's getMangaUrl. Singleton so its bounded LRU (Original Url → Full Url)
            // persists across requests.
            services.TryAddSingleton<Contributions.MangaUrlResolver>();
            // Re-usable extension scanning / matching / writing unit (records series into
            // contributor.db incrementally). Drivable from any entry point, not just --seed.
            services.TryAddScoped<Background.ExtensionSweepService>();
            // One-shot seeding sweep worker (started only when --seed is supplied): sweeps
            // via ExtensionSweepService then exports the contribution database once.
            services.TryAddScoped<Background.SeedService>();

            return AddScrobblingServicesCore(services, configuration);
        }

        private static IServiceCollection AddScrobblingServicesCore(this IServiceCollection services, IConfiguration configuration)
        {
            services.TryAddScoped<ScrobblerTokenProtector>();
            services.TryAddScoped<ITokenStorageService, TokenStorageService>();
            services.TryAddScoped<ExternalSeriesProviderFactory>();
            services.TryAddScoped<TitleMatcher>();
            services.TryAddScoped<ScrobblerSyncService>();
            services.TryAddScoped<SeriesMatchingService>();

            // Metadata ecosystem services
            services.TryAddScoped<Metadata.SeriesMetadataResolver>();
            // Database-agnostic matcher + provider helpers shared by the External Mappings and
            // Contribution Mappings flows (registered before the engine which depends on them).
            services.TryAddScoped<Metadata.MetadataMatchCore>();
            services.TryAddScoped<Metadata.ExternalMappingSupport>();
            // Image (PDQ) matching: URL-keyed hash cache + orchestration + matcher.
            services.TryAddScoped<Metadata.ImageHashCacheService>();
            services.TryAddScoped<Metadata.ImageMatchCore>();
            services.TryAddScoped<Metadata.ImageMatcher>();
            // Content-type gate: metadata providers only run when they support at least one of the
            // content types derived from the root "TypeMappings" list for the source package.
            services.AddSingleton<Metadata.MetadataTypeGate>();
            services.TryAddScoped<Metadata.MetadataLinkEngine>();
            // Wrong-linkage detection + auto-block repair (depends on the engine; registered after).
            services.TryAddScoped<Metadata.MappingConflictRepairService>();


            // Register all external provider implementations under the UMBRELLA interface.
            // NOTE: Must use AddScoped (not TryAddScoped) so each provider is registered,
            // and the key MUST be IExternalSeriesProvider: ExternalSeriesProviderFactory
            // injects IEnumerable<IExternalSeriesProvider>, and the container auto-collects
            // beans bound to that exact key. Registering under the read-state sub-interface
            // IScrobblerProvider would yield an empty collection here.
            // Direct auth: Kitsu and MangaDex use password-based auth directly
            services.AddScoped<Scrobbling.Abstractions.IExternalSeriesProvider, Scrobbling.Providers.KitsuScrobblerProvider>();
            services.AddScoped<Scrobbling.Abstractions.IExternalSeriesProvider, Scrobbling.Providers.MangaDexScrobblerProvider>();

            // AniList and MyAnimeList use OAuth2 via the central OAuth proxy for authorization,
            // but call their respective APIs directly for search/tracking operations.
            services.AddScoped<Scrobbling.Abstractions.IExternalSeriesProvider, Scrobbling.Providers.AniListScrobblerProvider>();
            services.AddScoped<Scrobbling.Abstractions.IExternalSeriesProvider, Scrobbling.Providers.MyAnimeListScrobblerProvider>();

            // Metadata-only providers (implement IExternalSeriesProvider directly)
            services.AddScoped<Scrobbling.Abstractions.IExternalSeriesProvider, Scrobbling.Providers.MangaBakaMetadataProvider>();
            services.AddScoped<Scrobbling.Abstractions.IExternalSeriesProvider, Scrobbling.Providers.BangumiMetadataProvider>();
            services.AddScoped<Scrobbling.Abstractions.IExternalSeriesProvider, Scrobbling.Providers.MangaUpdatesMetadataProvider>();
            services.AddScoped<Scrobbling.Abstractions.IExternalSeriesProvider, Scrobbling.Providers.ComicVineMetadataProvider>();
            // Global metadata repository (in-memory; PR #76 will back it with the snapshot index).
            services.TryAddScoped<Contributions.Abstractions.IGlobalMetadataRepository, Contributions.InMemoryGlobalMetadataRepository>();
            services.TryAddScoped<Contributions.InMemoryGlobalMetadataRepository>();

            // Register HTTP clients.
            // Every scrobbler / metadata provider client gets the shared resilience
            // handler so provider rate limits and outages are retried with exponential
            // backoff and then short-circuited per host by a 1-minute circuit breaker.
            services.AddHttpClient("Scrobbler_AniList", SetHttpClientHeaders).AddProviderResilience();
            services.AddHttpClient("Scrobbler_MAL", SetHttpClientHeaders).AddProviderResilience();
            services.AddHttpClient("Scrobbler_Kitsu", SetHttpClientHeaders).AddProviderResilience();
            services.AddHttpClient("Scrobbler_MangaDex", SetHttpClientHeaders).AddProviderResilience();
            services.AddHttpClient("Scrobbler_MangaBaka", SetHttpClientHeaders).AddProviderResilience();
            services.AddHttpClient("Scrobbler_Bangumi", SetHttpClientHeaders).AddProviderResilience();
            services.AddHttpClient("Scrobbler_MangaUpdates", SetHttpClientHeaders).AddProviderResilience();
            // ComicVine (API-key metadata) and the OAuth proxy client are created by
            // their providers but were previously never registered; without a
            // registration CreateClient returns a default client with no resilience.
            services.AddHttpClient("Scrobbler_ComicVine", SetHttpClientHeaders).AddProviderResilience();
            services.AddHttpClient("Scrobbler_Proxy", SetHttpClientHeaders).AddProviderResilience();

            return services;
        }
        internal static void SetHttpClientHeaders(System.Net.Http.HttpClient client)
        {
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Rensaio", "1.0"));
        }
        public static IServiceCollection AddBackgroundServices(this IServiceCollection services)
        {
            services.TryAddSingleton<JobQueueHostedService>();
            services.TryAddSingleton<JobScheduledHostedService>();
            services.AddHostedService<DebouncedScrobblerSyncHostedService>();
            services.TryAddSingleton<MetadataBackgroundScanService>();
            services.TryAddSingleton<ContributionUploadBackgroundService>();
            services.TryAddSingleton<ContributionImportBackgroundService>();
            // Single startup orchestrator for the contribution/mapping repair pipeline
            // (conflict repair → title reconciliation → cover backfill). Must be registered
            // or StartupHostedService.StartWorker cannot resolve it and the whole pass is skipped.
            services.TryAddSingleton<ContributionRepairBackgroundService>();
            return services;
        }


        public static IServiceCollection AddProviderServices(this IServiceCollection services)
        {

            
            // Provider Services (SRP-focused)
            services.TryAddScoped<ProviderManagerService>();
            services.TryAddScoped<ProviderPreferencesService>();
            
            // Provider Cache and Storage
            services.TryAddScoped<ProviderCacheService>();
            
            return services;
        }

        public static IServiceCollection AddSearchServices(this IServiceCollection services)
        {
            // CQRS Search Services
            services.TryAddScoped<SearchQueryService>();
            services.TryAddScoped<SearchCommandService>();
            
            return services;
        }

        public static IServiceCollection AddDownloadServices(this IServiceCollection services)
        {
            // Download CQRS Services
            services.TryAddScoped<DownloadQueryService>();
            services.TryAddScoped<DownloadCommandService>();
            
            return services;
        }
        
        public static IServiceCollection AddMcpServices(this IServiceCollection services)
        {
            services.TryAddScoped<Mcp.McpToolService>();
            services.TryAddScoped<Mcp.Abstractions.IMcpPermissionService, Mcp.McpPermissionService>();
            return services;
        }

        public static IServiceCollection AddAuthServices(this IServiceCollection services)
        {
            services.TryAddScoped<PasswordService>();
            services.TryAddScoped<OpdsPathGenerator>();
            services.TryAddScoped<JwtTokenService>();
            services.TryAddScoped<UserInviteService>();
            services.TryAddScoped<UserQueryService>();
            services.TryAddScoped<UserCommandService>();
            services.AddHttpClient(Auth.Oidc.OidcService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
            services.TryAddSingleton<Auth.Oidc.OidcDiscoveryCache>();
            services.TryAddScoped<Auth.Oidc.OidcService>();
            return services;
        }

        public static IServiceCollection AddReadStateServices(this IServiceCollection services)
        {
            services.TryAddSingleton<ReadStateCacheService>();
            services.TryAddSingleton<ReadStateChangeNotifier>();
            services.TryAddScoped<ReadStateService>();
            return services;
        }

        public static IServiceCollection AddOpdsServices(this IServiceCollection services)
        {
            services.TryAddSingleton<HashCacheService>();
            services.TryAddSingleton<OpdsExtractionCoordinator>();
            services.TryAddSingleton<ClientCapabilitiesHelper>();
            services.TryAddScoped<OpdsImageService>();
            services.TryAddScoped<OpdsFeedService>();
            return services;
        }

    }
}
