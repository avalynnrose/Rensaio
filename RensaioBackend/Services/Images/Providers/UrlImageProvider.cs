using RensaioBackend.Data;
using RensaioBackend.Extensions;
using RensaioBackend.Models;
using RensaioBackend.Models.Database;
using RensaioBackend.Services.Bridge;
using Rensaio.Resilience;
using Microsoft.Extensions.Options;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using System.Net;
using System.Security.Cryptography;

namespace RensaioBackend.Services.Images.Providers
{
    public class UrlImageProvider : IImageProvider
    {
        private readonly ILogger _logger;
        private readonly IHttpClientFactory _factory;
        private readonly AppDbContext _db;
        private readonly CacheOptions _options;
        private readonly MihonBridgeService _mihonBridgeService;
        private readonly CoverUrlDomainResolver _domainResolver;


        public UrlImageProvider(ILogger<UrlImageProvider> logger, IHttpClientFactory factory, AppDbContext db, IOptions<CacheOptions> options, MihonBridgeService mihonBridgeService, CoverUrlDomainResolver domainResolver)
        {
            _logger = logger;
            _factory = factory;
            _db = db;
            _options = options.Value;
            _mihonBridgeService = mihonBridgeService;
            _domainResolver = domainResolver;
        }

        /// <summary>Attempts (initial + retries) for a single remote image fetch.</summary>
        private const int ImageFetchMaxAttempts = 3;
        /// <summary>Base delay for the image-fetch retry backoff (doubles each attempt).</summary>
        private static readonly TimeSpan ImageFetchBaseDelay = TimeSpan.FromSeconds(2);

        public static async Task<string> ComputeMd5HashFromStreamAsync(Stream stream, CancellationToken token = default)
        {
            using (var md5 = MD5.Create())
            {
                byte[] hash = await md5.ComputeHashAsync(stream, token).ConfigureAwait(false);
                return Convert.ToBase64String(hash);
            }
        }

        TimeSpan ResolveCacheDuration(HttpResponseMessage responseMessage)
        {
            var cacheControl = responseMessage.Headers.CacheControl;
            var maxAge = cacheControl?.MaxAge ?? cacheControl?.SharedMaxAge ?? cacheControl?.MaxStaleLimit;

            if (maxAge.HasValue && maxAge.Value > TimeSpan.Zero)
            {
                return maxAge.Value;
            }

            var fallbackDays = _options.AgeInDays > 0 ? _options.AgeInDays : 1;
            return TimeSpan.FromDays(fallbackDays);
        }

        static string NormalizeExtension(string? extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return string.Empty;
            }

            var trimmed = extension.Trim();
            return trimmed.StartsWith('.') ? trimmed : "." + trimmed.TrimStart('.');
        }
        public bool CanProcess(string url)
        {
            if (string.IsNullOrEmpty(url))
                return false;
            if (url.StartsWith("http://") || url.StartsWith("https://"))
                return true;
            return false;
        }
        public async Task<Stream?> ObtainStreamAsync(EtagCacheEntity cache, CancellationToken token)
        {
            string directory = Path.Combine(_options.CachePath, cache.Key.Substring(0, 2));
            if (!string.IsNullOrEmpty(cache.Extension))
            {
                string baseFile = Path.Combine(directory, cache.Key.Substring(2)) + cache.Extension;
                if (File.Exists(baseFile))
                {
                    return File.OpenRead(baseFile);
                }
            }
            var httpClient = _factory.CreateClient(nameof(ThumbCacheService));
            await UpdateCacheWithRemoteAsync(cache, httpClient, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(cache.Extension))
            {
                string baseFile = Path.Combine(directory, cache.Key.Substring(2)) + cache.Extension;
                if (File.Exists(baseFile))
                {
                    return File.OpenRead(baseFile);
                }
            }
            return null;
        }
        private static (string, string)[] HardCodedReplacements = new (string, string)[]
        {
            (@"(https://mangakatana\.com/imgs/cover/[^)\]\s]+)-l(?=\.jpg)","$1")
        };

        public async Task UpdateCacheWithRemoteAsync(EtagCacheEntity cache, HttpClient httpClient, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(cache.Url))
            {
                _logger.LogWarning($"Cache URL is null or empty for {cache.Key}");
                return;
            }
            try
            {
                string directory = Path.Combine(_options.CachePath, cache.Key.Substring(0, 2));
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                string baseFile = Path.Combine(directory, cache.Key.Substring(2));
                string? originalFile = null;
                if (!string.IsNullOrEmpty(cache.Extension))
                {
                    originalFile = baseFile + cache.Extension;
                    if (!File.Exists(originalFile))
                        cache.ExternalEtag = string.Empty;
                }
                using var memoryStream = new MemoryStream();
                string mediaType = "application/octet-stream";
                string url = cache.Url;
                foreach (var (pattern, replacement) in HardCodedReplacements)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(url, pattern))
                       url = System.Text.RegularExpressions.Regex.Replace(url, pattern, replacement);
                }

                // The host the URL carried BEFORE any correction — the "dead" host we record once a
                // rewrite succeeds, so later covers on it are redirected with no failed request.
                string originalHost = CoverUrlDomainResolver.HostOf(url) ?? string.Empty;

                // A domain already known dead is skipped entirely: start on the remembered live host.
                url = _domainResolver.ApplyKnownRewrite(url, cache.MihonProviderId);

                // Candidate URLs in order: the (possibly rewritten) URL first, then — only after it
                // fails PERMANENTLY — the source's other domains (homeUrl, then mirrors), expanded
                // lazily so a healthy cover never pays for the extension-metadata lookup.
                var candidates = new List<string> { url };
                bool sourceFallbackExpanded = false;

                TimeSpan cacheDuration = TimeSpan.Zero;
                bool fetched = false;

                // Try each host in turn. A TRANSIENT failure (transport / timeout / truncated body —
                // the body is read with ResponseHeadersRead, so "The response ended prematurely"
                // surfaces while COPYING, outside any HTTP resilience handler) is retried with
                // exponential backoff. A PERMANENT failure (DNS "No such host is known") is never
                // retried; the loop rotates to the source's next domain instead.
                for (int candidate = 0; candidate < candidates.Count && !fetched; candidate++)
                {
                    string candidateUrl = candidates[candidate];

                    for (int attempt = 0; attempt < ImageFetchMaxAttempts && !fetched; attempt++)
                    {
                        if (attempt > 0)
                        {
                            TimeSpan backoff = TimeSpan.FromTicks(ImageFetchBaseDelay.Ticks * (1L << (attempt - 1)));
                            _logger.LogDebug(
                                "Retrying image download for {Key} (attempt {Attempt}/{Max}) in {Delay:0.##}s",
                                cache.Key, attempt + 1, ImageFetchMaxAttempts, backoff.TotalSeconds);
                            await Task.Delay(backoff, token).ConfigureAwait(false);
                            memoryStream.SetLength(0);
                            memoryStream.Position = 0;
                        }

                        using var request = new HttpRequestMessage(HttpMethod.Get, candidateUrl);
                        if (!string.IsNullOrWhiteSpace(cache.ExternalEtag))
                        {
                            request.Headers.TryAddWithoutValidation("If-None-Match", cache.ExternalEtag);
                        }

                        try
                        {
                            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

                            if (response.Headers.ETag != null)
                            {
                                cache.ExternalEtag = response.Headers.ETag.Tag;
                            }

                            cacheDuration = ResolveCacheDuration(response);

                            if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.NotModified)
                            {
                                cache.NextUpdateUTC = DateTime.UtcNow.Add(cacheDuration);
                                await _db.SaveChangesAsync().ConfigureAwait(false);
                                return;
                            }

                            if (response.StatusCode != HttpStatusCode.OK)
                            {
                                // Non-OK: fall back to the Mihon source interop (its own wrapper retries
                                // transient failures). Not retried by the loop above — the fallback IS the
                                // recovery path for a bad HTTP status.
                                bool ok = await TryCopyFromMihonSourceAsync(cache, candidateUrl, memoryStream, token).ConfigureAwait(false);
                                if (!ok)
                                {
                                    _logger.LogWarning("Error downloading the image for {Key}. Http error: {StatusCode}", cache.Key, response.StatusCode);
                                }
                                string? fallbackMed = response.Content.Headers.ContentType?.MediaType;
                                if (!string.IsNullOrEmpty(fallbackMed))
                                    mediaType = fallbackMed;
                                fetched = true;
                                break;
                            }

                            await response.Content.CopyToAsync(memoryStream, token).ConfigureAwait(false);
                            string? med = response.Content.Headers.ContentType?.MediaType;
                            if (!string.IsNullOrEmpty(med))
                                mediaType = med;
                            fetched = true;
                        }
                        catch (Exception ex) when (IsTransientImageFetch(ex, token)
                                                   && !ProviderHttpFailures.IsPermanentDnsFailure(ex))
                        {
                            // Quiet: a transient failure is retried, so it must not be reported until
                            // every candidate host is exhausted.
                            _logger.LogDebug(ex,
                                "Transient failure downloading image for {Key} (attempt {Attempt}/{Max}); retrying.",
                                cache.Key, attempt + 1, ImageFetchMaxAttempts);
                        }
                        catch (Exception ex) when (ProviderHttpFailures.IsPermanentDnsFailure(ex))
                        {
                            // PERMANENT: the host does not resolve. Never retry it; fall through to the
                            // next candidate. The source's alternative domains (homeUrl, then mirrors)
                            // are expanded HERE, on the first permanent failure only, so a healthy cover
                            // never pays for the extension-metadata lookup.
                            if (!sourceFallbackExpanded)
                            {
                                sourceFallbackExpanded = true;
                                var fallbacks = await _domainResolver
                                    .BuildFallbackUrlsAsync(candidateUrl, cache.MihonProviderId, token)
                                    .ConfigureAwait(false);
                                if (fallbacks.Count > 0)
                                {
                                    candidates.AddRange(fallbacks);
                                    _logger.LogDebug(
                                        "Cover host {Host} is dead for {Key}; trying {Count} source domain(s).",
                                        CoverUrlDomainResolver.HostOf(candidateUrl) ?? candidateUrl,
                                        cache.Key, fallbacks.Count);
                                }
                            }

                            break; // Stop retrying this (dead) host.
                        }
                    }

                    // A successful fetch on a corrected host is remembered so later covers skip the
                    // dead host entirely (in memory only — nothing is persisted).
                    if (fetched && !string.IsNullOrEmpty(originalHost))
                    {
                        string servedHost = CoverUrlDomainResolver.HostOf(candidateUrl) ?? string.Empty;
                        if (!string.IsNullOrEmpty(servedHost))
                            _domainResolver.RememberLiveHost(cache.MihonProviderId, originalHost, servedHost);
                    }
                }

                if (!fetched)
                {
                    _logger.LogWarning(
                        "Failed to download image for {Key} after trying {Count} host(s): {Hosts}.",
                        cache.Key, candidates.Count,
                        string.Join(", ", candidates.Select(c => CoverUrlDomainResolver.HostOf(c) ?? c)));
                    return;
                }

                if (memoryStream.Length == 0)
                {
                    _logger.LogWarning("Received empty payload when refreshing cache for {Key}", cache.Key);
                    return;
                }
                memoryStream.Position = 0;
                cache.Etag = await ComputeMd5HashFromStreamAsync(memoryStream, token).ConfigureAwait(false);
                memoryStream.Position = 0;
                (string? detectedContentType, string? detectedExtension) = memoryStream.GetImageMimeTypeAndExtension();
                var contentType = !string.IsNullOrWhiteSpace(detectedContentType)
                    ? detectedContentType
                    : mediaType;

                var normalizedExtension = NormalizeExtension(detectedExtension);
                if (string.IsNullOrEmpty(normalizedExtension))
                {
                    normalizedExtension = NormalizeExtension(Path.GetExtension(cache.Url));
                    if (string.IsNullOrEmpty(normalizedExtension))
                    {
                        normalizedExtension = ".bin";
                    }
                }
                var targetFile = baseFile + normalizedExtension;
                if (originalFile != null && File.Exists(originalFile) && (originalFile != baseFile))
                {
                    try
                    {
                        File.Delete(originalFile);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete old cache file for {Key}", cache.Key);
                    }
                }

                memoryStream.Position = 0;
                using (var fileStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true))
                {
                    await memoryStream.CopyToAsync(fileStream, token).ConfigureAwait(false);
                }
                cache.Extension = normalizedExtension;
                cache.ContentType = contentType;
                cache.NextUpdateUTC = DateTime.UtcNow.Add(cacheDuration);
                await _db.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating cache for key {Key}", cache.Key);
            }
        }

        /// <summary>
        /// Falls back to the Mihon source interop to fetch an image the direct HTTP call could not
        /// serve (non-OK status). The interop wrapper retries transient failures itself. The
        /// <paramref name="url"/> is the CANDIDATE url currently being tried (after any domain
        /// correction), never the original cache URL — otherwise a corrected cover would be
        /// re-fetched from the dead domain. Returns <c>true</c> when an image was copied into
        /// <paramref name="destination"/>.
        /// </summary>
        private async Task<bool> TryCopyFromMihonSourceAsync(EtagCacheEntity cache, string url, Stream destination, CancellationToken token)
        {
            if (cache.MihonProviderId is null)
                return false;

            ISourceInterop? interop = await _mihonBridgeService.SourceFromProviderIdAsync(cache.MihonProviderId).ConfigureAwait(false);
            if (interop is null)
                return false;

            ContentTypeStream? image;
            int retries = 2; //try twice with the interop in case of referer or missing headers.
            do
            {
                string message = "Error downloading the image for {Key}";
                if (retries == 2)
                    message = "Error downloading the image for {Key}. Retrying...";
                image = await _mihonBridgeService.MihonErrorWrapperAsync(
                    () => interop.DownloadUrlAsync(url, token),
                    message, cache.Key).ConfigureAwait(false);
                if (image != null)
                    break;
            } while (retries-- > 0);

            if (image is null)
                return false; //Warning already logged in the wrapper

            await image.CopyToAsync(destination, token).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// A transient image-fetch failure worth retrying: a transport error
        /// (<see cref="HttpRequestException"/>), a truncated/aborted body
        /// (<see cref="IOException"/>, e.g. "The response ended prematurely"), or a timeout-derived
        /// cancellation. A deliberate caller cancellation is never retried.
        /// </summary>
        private static bool IsTransientImageFetch(Exception ex, CancellationToken token)
        {
            if (ex is OperationCanceledException && token.IsCancellationRequested)
                return false;

            return ex is HttpRequestException or IOException or OperationCanceledException;
        }
    }
}
