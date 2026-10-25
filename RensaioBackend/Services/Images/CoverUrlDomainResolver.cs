using RensaioBackend.Services.Bridge;

namespace RensaioBackend.Services.Images;

/// <summary>
/// Decides whether a cover URL's host still belongs to its owning source, and — only when the
/// host is proven DEAD (a permanent DNS failure, not a transient blip) — rewrites it onto the
/// source's current domains: the extension's <c>homeUrl</c> host first, then each <c>mirrorUrls</c>
/// host in turn.
///
/// The domain set is read from the extension metadata (<c>TachiyomiSourceV2.homeUrl</c> /
/// <c>mirrorUrls</c>) through <see cref="MihonBridgeService.TryGetSourceHostsAsync"/>, so a cover
/// that still points at a domain the extension has since abandoned (e.g. <c>kagane.to</c> →
/// <c>kagane.si</c>) is redirected to the domain the package uses today. Successful corrections
/// are remembered in-process by <see cref="CoverHostRewriteCache"/> so later covers skip the dead
/// host entirely; nothing is persisted.
/// </summary>
public sealed class CoverUrlDomainResolver
{
    private readonly MihonBridgeService _mihon;
    private readonly ILogger<CoverUrlDomainResolver> _logger;

    public CoverUrlDomainResolver(MihonBridgeService mihon, ILogger<CoverUrlDomainResolver> logger)
    {
        _mihon = mihon;
        _logger = logger;
    }

    /// <summary>Returns the host of an absolute <paramref name="url"/>, or null when it has none.</summary>
    public static string? HostOf(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : null;

    /// <summary>Extracts the package part of a <c>package|sourceId</c> provider id, or null when malformed.</summary>
    public static string? PackageOf(string? mihonProviderId)
    {
        if (string.IsNullOrWhiteSpace(mihonProviderId))
            return null;
        string[] split = mihonProviderId.Split('|');
        return split.Length >= 2 && !string.IsNullOrWhiteSpace(split[0]) ? split[0] : null;
    }

    /// <summary>
    /// Applies a previously learned correction (dead host → live host) to <paramref name="url"/>
    /// so a cover whose domain was already found dead skips the dead host on the FIRST attempt,
    /// with no failed request and no log. Returns <paramref name="url"/> unchanged when nothing
    /// is known.
    /// </summary>
    public string ApplyKnownRewrite(string url, string? mihonProviderId)
    {
        string? package = PackageOf(mihonProviderId);
        string? host = HostOf(url);
        if (package is null || host is null)
            return url;

        if (CoverHostRewriteCache.TryGetLiveHost(package, host, out string liveHost)
            && !string.Equals(liveHost, host, StringComparison.OrdinalIgnoreCase))
        {
            return RewriteHost(url, liveHost);
        }

        return url;
    }

    /// <summary>
    /// Builds the ordered fallback URLs to try AFTER <paramref name="url"/> fails permanently: one
    /// URL per source host (homeUrl first, then mirrors), each the original URL with its host
    /// replaced. The URL's current host is excluded (it is the one that just failed). Returns an
    /// empty list when the provider is unknown, the source cannot be resolved, or no alternative
    /// host exists — in which case the caller logs and gives up.
    /// </summary>
    public async Task<IReadOnlyList<string>> BuildFallbackUrlsAsync(
        string url, string? mihonProviderId, CancellationToken token)
    {
        string? package = PackageOf(mihonProviderId);
        string? host = HostOf(url);
        if (package is null || host is null || mihonProviderId is null)
            return [];

        IReadOnlyList<string> sourceHosts = await GetSourceHostsAsync(package, mihonProviderId, token)
            .ConfigureAwait(false);
        if (sourceHosts.Count == 0)
            return [];

        var fallbacks = new List<string>(sourceHosts.Count);
        foreach (string sourceHost in sourceHosts)
        {
            if (string.Equals(sourceHost, host, StringComparison.OrdinalIgnoreCase))
                continue; // Same host that just failed — never retry it.
            string candidate = RewriteHost(url, sourceHost);
            if (!fallbacks.Contains(candidate, StringComparer.Ordinal))
                fallbacks.Add(candidate);
        }

        return fallbacks;
    }

    /// <summary>Records that covers on <paramref name="failedHost"/> are served by <paramref name="liveHost"/>.</summary>
    public void RememberLiveHost(string? mihonProviderId, string failedHost, string liveHost)
    {
        string? package = PackageOf(mihonProviderId);
        if (package is null
            || string.IsNullOrWhiteSpace(failedHost)
            || string.IsNullOrWhiteSpace(liveHost)
            || string.Equals(failedHost, liveHost, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CoverHostRewriteCache.SetLiveHost(package, failedHost, liveHost);
        _logger.LogInformation(
            "Cover domain for {Package} corrected: {DeadHost} → {LiveHost} (remembered in memory).",
            package, failedHost, liveHost);
    }

    private async Task<IReadOnlyList<string>> GetSourceHostsAsync(
        string package, string mihonProviderId, CancellationToken token)
    {
        if (CoverHostRewriteCache.TryGetSourceHosts(package, out IReadOnlyList<string> cached))
            return cached;

        IReadOnlyList<string> hosts = await _mihon.TryGetSourceHostsAsync(mihonProviderId, token)
            .ConfigureAwait(false) ?? [];

        // Cached even when empty: a negative result is remembered briefly so an unresolvable
        // package does not trigger an extension listing on every cover.
        CoverHostRewriteCache.SetSourceHosts(package, hosts);
        return hosts;
    }

    /// <summary>
    /// Returns <paramref name="url"/> with its host replaced by <paramref name="host"/>, preserving
    /// scheme, path, query and fragment (the port resets to the scheme default, which is correct
    /// for the source's public domains). Returns the URL unchanged when it already uses the host.
    /// </summary>
    private static string RewriteHost(string url, string host)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return url;
        if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
            return url;

        var builder = new UriBuilder(uri)
        {
            Host = host,
            Port = -1 // default port for the scheme (443/https)
        };
        return builder.Uri.ToString();
    }
}
