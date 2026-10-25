using RensaioBackend.Services.Bridge;
using RensaioBackend.Services.Images;
using RensaioBackend.Utils.ImageHashing;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace RensaioBackend.Services.Metadata;

/// <summary>
/// Orchestrates image (PDQ) matching for the metadata flows. Resolves the local series cover
/// hash and the candidate provider cover hashes, reusing cached hashes whenever possible, and
/// downloads provider covers in bounded parallel (max <see cref="MaxParallelDownloads"/>).
///
/// Acquisition strategy — both sides go through <see cref="ThumbCacheService"/> so images are
/// disk-cached and reused, content-typed, and downloaded with the Mihon bridge fallback that the
/// URL image provider already implements:
///   - Local cover: resolved through the ambient <see cref="ThumbCacheService"/>; falls back to
///     the Mihon bridge <c>ISourceInterop.DownloadUrlAsync</c> when a Mihon provider id is known
///     and the cache cannot serve the image.
///   - Provider cover: resolved through a per-download <see cref="ThumbCacheService"/> scope so
///     the bounded-parallel fetches never share an <c>AppDbContext</c> instance.
/// </summary>
public class ImageMatchCore
{
    /// <summary>Maximum number of provider cover downloads/hashes in flight at once.</summary>
    public const int MaxParallelDownloads = 4;

    private const string ApiImagePrefix = "/api/image/";

    private readonly ThumbCacheService _thumbs;
    private readonly ImageHashCacheService _hashCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MihonBridgeService _mihon;
    private readonly ILogger<ImageMatchCore> _logger;

    public ImageMatchCore(
        ThumbCacheService thumbs,
        ImageHashCacheService hashCache,
        IServiceScopeFactory scopeFactory,
        MihonBridgeService mihon,
        ILogger<ImageMatchCore> logger)
    {
        _thumbs = thumbs;
        _hashCache = hashCache;
        _scopeFactory = scopeFactory;
        _mihon = mihon;
        _logger = logger;
    }

    // ── Local side ──────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the PDQ hash of the local series cover. <paramref name="localThumbnailUrl"/> may
    /// be a raw provider/storage URL or an already-rewritten <c>/api/image/{key}</c> path.
    /// Returns null when the cover cannot be resolved or hashed.
    /// </summary>
    public async Task<PdqHash256?> GetLocalHashAsync(
        string? localThumbnailUrl, string? mihonProviderId = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(localThumbnailUrl))
            return null;

        var cached = await _hashCache.TryGetHashAsync(localThumbnailUrl, token).ConfigureAwait(false);
        if (cached != null)
            return cached;

        using var stream = await OpenLocalStreamAsync(localThumbnailUrl, mihonProviderId, token).ConfigureAwait(false);
        if (stream == null)
            return null;

        return await ComputeAndCacheAsync(localThumbnailUrl, stream, token).ConfigureAwait(false);
    }

    private async Task<Stream?> OpenLocalStreamAsync(string url, string? mihonProviderId, CancellationToken token)
    {
        // 1. ThumbCacheService (preferred — image is normally already on disk).
        try
        {
            string key;
            if (url.StartsWith(ApiImagePrefix, StringComparison.OrdinalIgnoreCase))
            {
                key = url[ApiImagePrefix.Length..];
            }
            else
            {
                // Record the owning Mihon provider id (when known) so the URL image provider can
                // fall back to the source for referer/header-protected hosts.
                key = await _thumbs.GetKeyAsync(url, mihonProviderId, token).ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(key))
            {
                var cache = await _thumbs.GetEtagAsync(key, token).ConfigureAwait(false);
                if (cache != null)
                {
                    var stream = await _thumbs.GetStreamAsync(cache, token).ConfigureAwait(false);
                    if (stream != null)
                        return stream;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ImageMatchCore: ThumbCache lookup failed for {Url}", url);
        }

        // 2. Mihon bridge fallback via the source that owns this series (referer/header aware).
        if (string.IsNullOrWhiteSpace(mihonProviderId))
            return null;

        try
        {
            ISourceInterop? interop = await _mihon.SourceFromProviderIdAsync(mihonProviderId, token).ConfigureAwait(false);
            if (interop == null)
                return null;

            ContentTypeStream? image = await _mihon.MihonErrorWrapperAsync(
                () => interop.DownloadUrlAsync(url, token),
                "Error downloading the local cover image for {Url}", url).ConfigureAwait(false);
            if (image == null)
                return null;

            var buffer = new MemoryStream();
            await image.CopyToAsync(buffer, token).ConfigureAwait(false);
            if (buffer.Length == 0)
                return null;
            buffer.Position = 0;
            return buffer;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ImageMatchCore: bridge local cover fetch failed for {Url}", url);
            return null;
        }
    }

    // ── Provider side ───────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the PDQ hash of a provider cover URL, reusing the persisted hash when present.
    /// </summary>
    public async Task<PdqHash256?> GetProviderHashAsync(string? coverUrl, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
            return null;

        var cached = await _hashCache.TryGetHashAsync(coverUrl, token).ConfigureAwait(false);
        if (cached != null)
            return cached;

        using var stream = await OpenRemoteStreamAsync(coverUrl, token).ConfigureAwait(false);
        if (stream == null)
            return null;

        return await ComputeAndCacheAsync(coverUrl, stream, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a provider cover stream through <see cref="ThumbCacheService"/>: the URL is
    /// registered (add-if-missing), its cache key resolved, then the cached/remote stream is
    /// obtained — the URL image provider transparently downloads and stores it, with the Mihon
    /// bridge <c>DownloadUrlAsync</c> fallback for referer/header-protected hosts. A dedicated DI
    /// scope is used per call so parallel fetches never share an <c>AppDbContext</c>.
    /// </summary>
    private async Task<Stream?> OpenRemoteStreamAsync(string coverUrl, CancellationToken token)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var thumbs = scope.ServiceProvider.GetRequiredService<ThumbCacheService>();

            // GetKeyAsync registers the URL if missing (same effect as AddUrlAsync) and returns the
            // cache KEY — which is what GetEtagAsync expects. AddUrlAsync returns the URL instead,
            // so pairing it with GetEtagAsync would look up the wrong column and miss.
            string key = await thumbs.GetKeyAsync(coverUrl, token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(key))
                return null;

            var cache = await thumbs.GetEtagAsync(key, token).ConfigureAwait(false);
            if (cache == null)
                return null;

            var stream = await thumbs.GetStreamAsync(cache, token).ConfigureAwait(false);
            if (stream == null)
                return null;

            // Copy into memory so the returned stream does not depend on the child scope's
            // provider / DbContext lifetime.
            var buffer = new MemoryStream();
            await using (stream)
                await stream.CopyToAsync(buffer, token).ConfigureAwait(false);
            if (buffer.Length == 0)
                return null;
            buffer.Position = 0;
            return buffer;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ImageMatchCore: cover fetch failed for {Url}", coverUrl);
            return null;
        }
    }

    /// <summary>
    /// Downloads and hashes provider covers in bounded parallel, reusing cached hashes and
    /// skipping URLs that fail. Returns a URL→hash map (case-insensitive).
    /// </summary>
    public async Task<Dictionary<string, PdqHash256>> GetProviderHashesAsync(
        IEnumerable<string> coverUrls, CancellationToken token = default)
    {
        var urls = coverUrls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new ConcurrentDictionary<string, PdqHash256>(StringComparer.OrdinalIgnoreCase);
        if (urls.Count == 0)
            return new Dictionary<string, PdqHash256>(results, StringComparer.OrdinalIgnoreCase);

        using var gate = new SemaphoreSlim(MaxParallelDownloads);
        var tasks = urls.Select(async url =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var hash = await GetProviderHashAsync(url, token).ConfigureAwait(false);
                if (hash is { IsEmpty: false })
                    results[url] = hash.Value;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ImageMatchCore: hashing provider cover failed for {Url}", url);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new Dictionary<string, PdqHash256>(results, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the local hash, then finds the best provider candidate by cover similarity.
    /// Returns null when no comparable hashes are available (callers fall back to title-only).
    /// </summary>
    public async Task<(TId Id, int Distance, int Percentage)?> FindBestMatchAsync<TId>(
        string? localThumbnailUrl,
        string? mihonProviderId,
        IEnumerable<(TId Id, string? CoverUrl)> candidates,
        CancellationToken token = default)
    {
        var localHash = await GetLocalHashAsync(localThumbnailUrl, mihonProviderId, token).ConfigureAwait(false);
        if (localHash == null || localHash.Value.IsEmpty)
            return null;

        var list = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.CoverUrl))
            .ToList();
        if (list.Count == 0)
            return null;

        var hashes = await GetProviderHashesAsync(list.Select(c => c.CoverUrl!), token).ConfigureAwait(false);

        var scored = list
            .Select(c => (c.Id, Hash: hashes.TryGetValue(c.CoverUrl!, out var h) ? h : PdqHash256.Empty))
            .Where(x => !x.Hash.IsEmpty)
            .Select(x => (x.Id, x.Hash))
            .ToArray();

        var matches = ImageMatcher.MatchImages(localHash.Value, scored);
        if (matches.Length == 0)
            return null;

        return matches[0];
    }

    // ── Shared hashing ──────────────────────────────────────────────────────

    private async Task<PdqHash256?> ComputeAndCacheAsync(string url, Stream stream, CancellationToken token)
    {
        try
        {
            if (stream.CanSeek)
                stream.Position = 0;

            var result = PdqHashUtility.Compute(stream);
            if (result.Hash.IsEmpty)
            {
                _logger.LogDebug("ImageMatchCore: PDQ produced an empty hash for {Url}", url);
                return null;
            }

            await _hashCache.SaveHashAsync(url, result, token).ConfigureAwait(false);
            return result.Hash;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Non-image payloads, unsupported formats, truncated downloads, etc.
            _logger.LogDebug(ex, "ImageMatchCore: PDQ compute failed for {Url}", url);
            return null;
        }
    }
}
