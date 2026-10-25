using RensaioBackend.Utils;

namespace RensaioBackend.Services.Images;

/// <summary>
/// Bounded, TTL-guarded, in-memory cache of cover-URL host corrections.
///
/// Two mappings are kept:
///  * a learned HOST REWRITE — a dead cover host (e.g. <c>kagane.to</c>) → the live host the
///    extraction actually resolved (<c>kagane.si</c>), so subsequent covers for the same domain
///    skip the dead host entirely instead of failing (and resolving the source) again; and
///  * the resolved SOURCE HOST LIST per extension package, so the (comparatively expensive)
///    extension listing that yields <c>homeUrl</c>/<c>mirrorUrls</c> is not re-run for every cover.
///
/// Static because the image providers are scoped while this decision is process-wide, mirroring
/// the existing static caches in <see cref="ThumbCacheService"/>. Nothing is persisted: a restart
/// simply re-learns the correction on the next fetch.
/// </summary>
public static class CoverHostRewriteCache
{
    /// <summary>Dead-host → live-host mappings expire so a re-introduced domain is re-probed.</summary>
    private static readonly TimeSpan RewriteTtl = TimeSpan.FromHours(1);

    /// <summary>Positive source host lists (homeUrl + mirrors) refresh hourly.</summary>
    private static readonly TimeSpan HostListTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// Negative source resolution (package not installed / unresolvable) is cached briefly so an
    /// unknown package does not trigger an extension listing on every single cover.
    /// </summary>
    private static readonly TimeSpan UnresolvedTtl = TimeSpan.FromMinutes(10);

    private sealed record Rewrite(string LiveHost, DateTimeOffset ExpiresAtUtc);

    /// <summary><c>Hosts</c> is empty when the package's source could not be resolved.</summary>
    private sealed record HostList(IReadOnlyList<string> Hosts, DateTimeOffset ExpiresAtUtc);

    private static readonly LruCache<string, Rewrite> _rewrites = new(4000);
    private static readonly LruCache<string, HostList> _hostLists = new(2000);

    private static string HostKey(string package, string host) =>
        package.ToLowerInvariant() + "|" + host.ToLowerInvariant();

    /// <summary>
    /// Returns the learned live host for a now-dead <paramref name="host"/> of
    /// <paramref name="package"/>, or <c>false</c> when no (unexpired) correction is known.
    /// </summary>
    public static bool TryGetLiveHost(string package, string host, out string liveHost)
    {
        if (_rewrites.TryGet(HostKey(package, host), out var entry)
            && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            liveHost = entry.LiveHost;
            return true;
        }

        liveHost = string.Empty;
        return false;
    }

    /// <summary>Records that covers on <paramref name="failedHost"/> are served by <paramref name="liveHost"/>.</summary>
    public static void SetLiveHost(string package, string failedHost, string liveHost)
        => _rewrites.Set(HostKey(package, failedHost), new Rewrite(liveHost, DateTimeOffset.UtcNow + RewriteTtl));

    /// <summary>
    /// Returns the cached source host list for <paramref name="package"/>. <c>true</c> with an empty
    /// list means "resolution was attempted and found nothing" (a negative hit).
    /// </summary>
    public static bool TryGetSourceHosts(string package, out IReadOnlyList<string> hosts)
    {
        if (_hostLists.TryGet(package.ToLowerInvariant(), out var entry)
            && entry.ExpiresAtUtc > DateTimeOffset.UtcNow)
        {
            hosts = entry.Hosts;
            return true;
        }

        hosts = [];
        return false;
    }

    /// <summary>Caches the resolved source host list (<paramref name="hosts"/> may be empty = unresolved).</summary>
    public static void SetSourceHosts(string package, IReadOnlyList<string> hosts)
    {
        TimeSpan ttl = hosts.Count == 0 ? UnresolvedTtl : HostListTtl;
        _hostLists.Set(package.ToLowerInvariant(), new HostList(hosts, DateTimeOffset.UtcNow + ttl));
    }
}
