using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mihon.ExtensionsBridge.Models.Extensions;
using RensaioBackend.Extensions;
using RensaioBackend.Services.Rpc;
using RensaioBackend.Utils;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// Resolves the absolute source page url for a contribution source entry.
///
/// The url persisted in the contribution record (<c>ContributionRecordV2.Url</c>) is usually the
/// source-relative path the extension returned from its catalogue (e.g. <c>/series/1234</c>). The
/// real, clickable page url is produced by the extension's
/// <c>HttpSource.getMangaUrl(SManga)</c>, which applies the source base url (and any per-source
/// override). This service bridges that call through the extension runtime and caches the result.
/// </summary>
public sealed class MangaUrlResolver
{
    /// <summary>Fixed capacity of the resolution cache (least-recently-used eviction).</summary>
    private const int CacheCapacity = 5000;

    /// <summary>
    /// Explicit JSON null. An unset <see cref="Manga.Memo"/> is an Undefined <see cref="JsonElement"/>,
    /// which System.Text.Json refuses to serialize — so a real null must be substituted before the
    /// manga crosses the RPC pipe (same pattern as the discovery worker).
    /// </summary>
    private static readonly JsonElement NullMemo = JsonDocument.Parse("null").RootElement.Clone();

    private readonly IExtensionRpcBridge _rpc;
    private readonly ILogger<MangaUrlResolver> _logger;

    /// <summary>
    /// Bounded LRU of Original Url → Full Url. Kept on the (singleton) resolver so the mapping
    /// survives across requests; capped so it can never grow unbounded.
    /// </summary>
    private readonly LruCache<string, string> _urlCache = new(CacheCapacity);

    public MangaUrlResolver(IExtensionRpcBridge rpc, ILogger<MangaUrlResolver> logger)
    {
        _rpc = rpc;
        _logger = logger;
    }

    /// <summary>
    /// Resolves <paramref name="relativeUrl"/> to an absolute page url using the extension that
    /// owns <paramref name="sourceKey"/> (format <c>package:sourceId</c>). Returns null when the
    /// url cannot be resolved (unknown source, non-HTTP source, extension error). Absolute urls are
    /// returned unchanged; the extension is only consulted for relative paths.
    /// </summary>
    /// <param name="sourceKey">Canonical <c>package:sourceId</c> identity of the owning source.</param>
    /// <param name="relativeUrl">The stored (usually source-relative) series url.</param>
    /// <param name="title">The series title from the entry's record — populated on the manga so
    /// extension overrides that read the title can build the url.</param>
    /// <param name="memo">The entry's raw memo JSON string. Some sources stash a slug/extra identity
    /// in <c>SManga.memo</c>; it is rehydrated onto the manga before <c>getMangaUrl</c> runs.</param>
    public async Task<string?> ResolveAsync(string? sourceKey, string? relativeUrl, string? title,
        string? memo = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(relativeUrl))
            return null;

        // Already absolute — nothing to resolve.
        if (Uri.TryCreate(relativeUrl, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return relativeUrl;
        }

        if (string.IsNullOrWhiteSpace(sourceKey))
            return null;

        // Cache keyed by the Original Url → Full Url.
        if (_urlCache.TryGet(relativeUrl, out string cached))
            return cached;

        string? resolved = await ResolveViaExtensionAsync(sourceKey, relativeUrl, title, memo, token).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(resolved))
            _urlCache.Set(relativeUrl, resolved);

        return resolved;
    }

    private async Task<string?> ResolveViaExtensionAsync(string sourceKey, string relativeUrl, string? title,
        string? memo, CancellationToken token)
    {
        // sourceKey is the canonical "package:sourceId" form (see ContributionMappingSourceDto),
        // e.g. "eu.kanade.tachiyomi.extension.en.mangakatana:3170561626848540385" — NOT the
        // pipe-separated "package|sourceId" Mihon provider id.
        int separator = sourceKey.LastIndexOf(':');
        if (separator <= 0 || separator >= sourceKey.Length - 1)
            return null;

        string package = sourceKey[..separator];
        string sourceIdText = sourceKey[(separator + 1)..];

        if (!long.TryParse(sourceIdText, out long sourceId))
            return null;

        try
        {
            var interop = await _rpc.GetSourceInteropAsync(package, sourceId, token).ConfigureAwait(false);
            if (interop == null || !interop.IsHttpSource)
                return null;

            // Reconstruct the manga exactly as the entry's record described it — url, title and the
            // memo (which may carry a source-specific slug) — so extension overrides that build the
            // page url from those fields behave identically to the sweep/catalogue path.
            var manga = new Manga
            {
                Url = relativeUrl,
                Title = title ?? string.Empty,
                Memo = ParseMemo(memo)
            };

            string url = await interop.GetMangaUrlAsync(manga, token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
        catch (Exception ex)
        {
            // A missing/unloaded extension or a failing override must not break the page: the
            // caller falls back to the stored (relative) url.
            _logger.LogDebug(ex, "MangaUrlResolver: failed to resolve url for source {SourceKey} ({Url}).", sourceKey, relativeUrl);
            return null;
        }
    }

    /// <summary>
    /// Parses a memo JSON string into a detached <see cref="JsonElement"/>. Always returns a
    /// serializable element: an explicit JSON null when the memo is empty/invalid (never Undefined).
    /// </summary>
    private static JsonElement ParseMemo(string? memo)
    {
        if (string.IsNullOrWhiteSpace(memo)) return NullMemo;
        try
        {
            using var doc = JsonDocument.Parse(memo);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return NullMemo;
        }
    }
}
