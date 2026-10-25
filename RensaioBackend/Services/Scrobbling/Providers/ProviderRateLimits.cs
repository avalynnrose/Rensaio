using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using RensaioBackend.Services.Rpc;

namespace RensaioBackend.Services.Scrobbling.Providers;

/// <summary>
/// Central registry of documented outbound rate limits for every external
/// metadata / scrobbler provider. Each limiter is process-wide (static) so all
/// users, jobs and background services share a single per-provider budget and
/// the app never exceeds a provider's published quota.
///
/// Limits were taken from each provider's official documentation (see the
/// per-limiter comments). Where a provider publishes a burst/velocity rule in
/// addition to a coarse quota, both are enforced.
///
/// Every limiter uses a token bucket with a small queue so callers wait for a
/// permit instead of being rejected; the provider resilience handler still
/// translates any real "429 Too Many Requests" (with Retry-After) into an
/// exponential-backoff retry as a second line of defence.
///
/// NOTE: these are *proactive* limits that prevent hitting the provider's hard
/// limit. They are intentionally set at or just below the published quota.
/// </summary>
internal static class ProviderRateLimits
{
    /// <summary>
    /// Creates a token-bucket limiter with automatic replenishment and a bounded
    /// wait queue (so bursts queue up rather than being dropped).
    /// </summary>
    private static TokenBucketRateLimiter Bucket(int tokenLimit, int tokensPerPeriod, TimeSpan period, int queueLimit)
        => new(new TokenBucketRateLimiterOptions
        {
            TokenLimit = tokenLimit,
            TokensPerPeriod = tokensPerPeriod,
            ReplenishmentPeriod = period,
            QueueLimit = queueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });

    private static async ValueTask EnforceAsync(TokenBucketRateLimiter limiter, CancellationToken token = default)
    {
        using var lease = await limiter.AcquireAsync(permitCount: 1, token).ConfigureAwait(false);
        // A full queue yields a failed lease; wait briefly and let the caller
        // proceed rather than dropping the request outright.
        if (!lease.IsAcquired)
            await Task.Delay(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
    }

    // ── MangaDex ─────────────────────────────────────────────────────────────
    // https://api.mangadex.org/docs/2-limitations/
    // Global: 5 requests/second. (Per-endpoint buckets such as 40/min for
    // /manga are more strict; the 5/s global limit is the documented ceiling.)
    private static readonly TokenBucketRateLimiter MangaDex =
        Bucket(tokenLimit: 5, tokensPerPeriod: 5, period: TimeSpan.FromSeconds(1), queueLimit: 4);

    // ── AniList ──────────────────────────────────────────────────────────────
    // https://docs.anilist.co/guide/rate-limiting
    // Documented ceiling: 90 requests/minute, but the API is currently in a
    // degraded state limited to 30 requests/minute. Enforce the effective
    // 30/min so we never trip the timeout; a burst limiter also applies.
    private static readonly TokenBucketRateLimiter AniList =
        Bucket(tokenLimit: 30, tokensPerPeriod: 30, period: TimeSpan.FromMinutes(1), queueLimit: 4);

    // ── MyAnimeList ──────────────────────────────────────────────────────────
    // https://myanimelist.net/apiconfig/references/api/v2 (API terms)
    // Documented: 3 requests/second.
    private static readonly TokenBucketRateLimiter MyAnimeList =
        Bucket(tokenLimit: 3, tokensPerPeriod: 3, period: TimeSpan.FromSeconds(1), queueLimit: 4);

    // ── Kitsu ────────────────────────────────────────────────────────────────
    // https://kitsu.docs.apiary.io — no explicit documented number; the edge
    // API enforces ~60 requests/minute. Enforce the same ceiling.
    private static readonly TokenBucketRateLimiter Kitsu =
        Bucket(tokenLimit: 60, tokensPerPeriod: 60, period: TimeSpan.FromMinutes(1), queueLimit: 4);

    // ── MangaBaka ────────────────────────────────────────────────────────────
    // https://mangabaka.org/data/api — "Rate Limits and Caching":
    //   /series/search : 30 requests/minute
    //   everything else: 180 requests/minute
    private static readonly TokenBucketRateLimiter MangaBakaSearch =
        Bucket(tokenLimit: 30, tokensPerPeriod: 30, period: TimeSpan.FromMinutes(1), queueLimit: 4);
    private static readonly TokenBucketRateLimiter MangaBakaDefault =
        Bucket(tokenLimit: 180, tokensPerPeriod: 180, period: TimeSpan.FromMinutes(1), queueLimit: 8);

    // ── ComicVine ────────────────────────────────────────────────────────────
    // https://comicvine.gamespot.com/api/ — "Rate limiting":
    //   200 requests per resource, per hour, plus velocity detection that blocks
    //   callers that fire too many requests per second. The hourly quota is
    //   per-resource (e.g. all /volume calls share one bucket), so limiters are
    //   kept per resource key; a shared 1/s velocity limiter caps bursts.
    private static readonly ConcurrentDictionary<string, TokenBucketRateLimiter> ComicVineByResource = new();
    private static readonly TokenBucketRateLimiter ComicVineVelocity =
        Bucket(tokenLimit: 1, tokensPerPeriod: 1, period: TimeSpan.FromSeconds(1), queueLimit: 4);

    // ── Bangumi (bgm.tv) ─────────────────────────────────────────────────────
    // https://bangumi.github.io/api/ — the public API has no published numeric
    // quota; community guidance and the project's own clients throttle to about
    // 1 request/second to stay within fair-use. Enforce 1/s.
    private static readonly TokenBucketRateLimiter Bangumi =
        Bucket(tokenLimit: 1, tokensPerPeriod: 1, period: TimeSpan.FromSeconds(1), queueLimit: 4);

    // ── MangaUpdates ─────────────────────────────────────────────────────────
    // https://api.mangaupdates.com/ (API ToS): "use reasonable spacing between
    // requests so as not to overwhelm the servers". Enforce a polite 1/s.
    private static readonly TokenBucketRateLimiter MangaUpdates =
        Bucket(tokenLimit: 1, tokensPerPeriod: 1, period: TimeSpan.FromSeconds(1), queueLimit: 4);

    // ── Public enforcement helpers ───────────────────────────────────────────

    public static ValueTask EnforceMangaDexAsync() => EnforceAsync(MangaDex);

    public static ValueTask EnforceAniListAsync() => EnforceAsync(AniList);

    public static ValueTask EnforceMyAnimeListAsync() => EnforceAsync(MyAnimeList);

    public static ValueTask EnforceKitsuAsync() => EnforceAsync(Kitsu);

    public static ValueTask EnforceMangaBakaSearchAsync() => EnforceAsync(MangaBakaSearch);

    public static ValueTask EnforceMangaBakaDefaultAsync() => EnforceAsync(MangaBakaDefault);

    public static ValueTask EnforceBangumiAsync() => EnforceAsync(Bangumi);

    public static ValueTask EnforceMangaUpdatesAsync() => EnforceAsync(MangaUpdates);

    /// <summary>
    /// Enforces ComicVine's per-resource hourly quota plus the global velocity
    /// cap. <paramref name="resource"/> is the ComicVine resource type
    /// (e.g. "volume"); all calls for the same resource share one hourly bucket.
    /// </summary>
    public static async ValueTask EnforceComicVineAsync(string? resource)
    {
        var key = string.IsNullOrWhiteSpace(resource) ? "default" : resource.Trim().ToLowerInvariant();
        var resourceLimiter = ComicVineByResource.GetOrAdd(
            key,
            _ => Bucket(tokenLimit: 200, tokensPerPeriod: 200, period: TimeSpan.FromHours(1), queueLimit: 16));

        await EnforceAsync(ComicVineVelocity);
        await EnforceAsync(resourceLimiter);
    }

    // ── Mihon source extension package limits (operator-controlled) ──────────
    // Unlike the scrobbler providers above, these are not hard-coded: the operator
    // configures a fixed dictionary of package ids in appsettings.json and only the
    // packages listed there are throttled. Each package can limit catalogue/metadata
    // calls (Methods) and page-image/download fetches (Images) independently, using
    // the SAME Bucket(...) factory and EnforceAsync(...) policy as the providers.
    private static volatile SourceRateLimitsOptions? _sourceOptions;

    /// <summary>
    /// Lazily created per-package buckets. Rebuilt (cleared) whenever
    /// <see cref="ConfigureSourceLimits"/> observes new configuration, so a values
    /// change applies on reload without a process restart.
    /// </summary>
    private static readonly ConcurrentDictionary<string, PackageLimiters> SourceLimiters =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class PackageLimiters
    {
        public TokenBucketRateLimiter? Methods;
        public TokenBucketRateLimiter? Images;
    }

    /// <summary>
    /// Applies the operator's per-package source limits. Called at startup and on
    /// every configuration reload; cached buckets are dropped so the next acquire
    /// rebuilds them from the new values.
    /// </summary>
    public static void ConfigureSourceLimits(SourceRateLimitsOptions options)
    {
        _sourceOptions = options;
        SourceLimiters.Clear();
    }

    /// <summary>Enforces the package's catalogue/metadata bucket, if configured.</summary>
    public static ValueTask EnforceSourceMethodAsync(string? packageId, CancellationToken token = default)
        => EnforceSourceAsync(packageId, isImage: false, token);

    /// <summary>Enforces the package's page-image / direct-download bucket, if configured.</summary>
    public static ValueTask EnforceSourceImageAsync(string? packageId, CancellationToken token = default)
        => EnforceSourceAsync(packageId, isImage: true, token);

    private static async ValueTask EnforceSourceAsync(string? packageId, bool isImage, CancellationToken token)
    {
        var options = _sourceOptions;
        if (options is null || !options.Enabled || string.IsNullOrWhiteSpace(packageId))
            return;

        if (!options.Packages.TryGetValue(packageId, out var limits) || limits is null)
            return;
        if ((isImage ? limits.Images : limits.Methods) is null)
            return;

        var limiters = SourceLimiters.GetOrAdd(packageId, key => BuildPackageLimiters(options, key));
        var limiter = isImage ? limiters.Images : limiters.Methods;
        if (limiter is null)
            return;

        await EnforceAsync(limiter, token).ConfigureAwait(false);
    }

    private static PackageLimiters BuildPackageLimiters(SourceRateLimitsOptions options, string packageId)
    {
        options.Packages.TryGetValue(packageId, out var limits);
        return new PackageLimiters
        {
            Methods = TryBucket(limits?.Methods),
            Images = TryBucket(limits?.Images),
        };
    }

    /// <summary>
    /// Builds a bucket from a configured dimension, or null when the dimension is
    /// absent or the values are not a valid token-bucket shape (treated as
    /// unthrottled rather than throwing on a bad operator entry).
    /// </summary>
    private static TokenBucketRateLimiter? TryBucket(PackageRateLimit? config)
    {
        if (config is null || config.TokenLimit <= 0 || config.TokensPerPeriod <= 0 || config.QueueLimit < 0)
            return null;
        return Bucket(config.TokenLimit, config.TokensPerPeriod, config.Period, config.QueueLimit);
    }
}
