using RensaioBackend.Extensions;
using RensaioBackend.Services.Settings;
using Microsoft.AspNetCore.Http;
using System.Collections.Concurrent;

namespace RensaioBackend.Services.Opds;

/// <summary>
/// Shared static helper for caching client image format capabilities.
/// Used by both <see cref="OpdsController"/> and <see cref="OpdsImageController"/>.
/// Cache key is "user-agent:client-ip" — a naturally bounded set, no eviction needed.
/// </summary>
public class ClientCapabilitiesHelper
{
    /// <summary>
    /// Bounds the client-capability caches. Keys are "user-agent:client-ip"; mobile readers
    /// behind carrier NAT rotate IPs frequently, so the comment "naturally bounded set"
    /// does not hold in practice — cap and evict oldest-first instead.
    /// </summary>
    private static readonly int MaxClientEntries = 1000;

    /// <summary>Monotonic revision for oldest-first eviction of <see cref="_formatsCache"/>.</summary>
    private static long _formatsRevision = 0;
    private static readonly ConcurrentDictionary<string, (long Revision, List<string> Formats)> _formatsCache = new();

    /// <summary>Monotonic revision for oldest-first eviction of <see cref="_supportsProgression"/>.</summary>
    private static long _progressionRevision = 0;
    private static readonly ConcurrentDictionary<string, (long Revision, bool Supported)> _supportsProgression = new();

    private string[] _supportProgressionClients;

    public ClientCapabilitiesHelper(IConfiguration config)
    {
        _supportProgressionClients = config.GetSection("SupportProgressionClients").Get<string[]>() ?? [];
    }

    /// <summary>
    /// Gets the cache key from the current request's User-Agent and client IP.
    /// </summary>
    public string GetClientUserCapabilitiesKey(HttpRequest request, HttpContext httpContext)
    {
        string userAgent = request.Headers["User-Agent"].FirstOrDefault() ?? "";
        string ip = request.Headers["X-Forwarded-For"].FirstOrDefault()
                    ?? httpContext.Connection.RemoteIpAddress?.ToString()
                    ?? "unknown";
        return $"{userAgent}:{ip}";
    }

    public void SetSupportProgression(HttpRequest request, HttpContext httpContext)
    {
        string key = GetClientUserCapabilitiesKey(request, httpContext);
        _supportsProgression[key] = (Interlocked.Increment(ref _progressionRevision), true);
        TrimProgressionExcess();
    }


    /// <summary>
    /// Captures and caches the client's supported image formats from the Accept header.
    /// Overwrites the cached entry if the formats differ from what's already cached.
    /// </summary>
    public void Capture(HttpRequest request, HttpContext httpContext)
    {
        string key = GetClientUserCapabilitiesKey(request, httpContext);
        List<string> formats = request.SupportedImageTypesFromRequest();

        var cached = _formatsCache.TryGetValue(key, out var existing) && existing.Formats != null
            ? existing.Formats
            : null;
        if (cached != null &&
            cached.Count == formats.Count &&
            cached.OrderBy(x => x).SequenceEqual(formats.OrderBy(x => x)))
        {
            // Unchanged — just touch the entry so it stays fresh.
            _formatsCache[key] = (Interlocked.Increment(ref _formatsRevision), cached);
            return;
        }

        _formatsCache[key] = (Interlocked.Increment(ref _formatsRevision), formats);
        TrimFormatsExcess();
    }

    /// <summary>
    /// Gets the cached client capabilities for the current request, or empty list.
    /// </summary>
    public List<string> GetSupportedImageFormats(HttpRequest request, HttpContext httpContext)
    {
        string key = GetClientUserCapabilitiesKey(request, httpContext);
        if (_formatsCache.TryGetValue(key, out var formats))
        {
            // Touch so frequently-returning clients stay resident.
            _formatsCache[key] = (Interlocked.Increment(ref _formatsRevision), formats.Formats);
            return formats.Formats;
        }
        return [];
    }
    public bool SupportProgression(HttpRequest request, HttpContext httpContext)
    {
        string userAgent = request.Headers["User-Agent"].FirstOrDefault() ?? "";
        if (!string.IsNullOrEmpty(userAgent) && _supportProgressionClients.Any(client => userAgent.Contains(client, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        string key = GetClientUserCapabilitiesKey(request, httpContext);
        return _supportsProgression.TryGetValue(key, out var resEntry) ? resEntry.Supported : false;
    }

    /// <summary>
    /// Evicts the oldest (lowest-revision) entries once the formats cache exceeds the cap.
    /// </summary>
    private static void TrimFormatsExcess()
    {
        TrimExcess(_formatsCache, kvp => kvp.Value.Item1, MaxClientEntries);
    }

    /// <summary>
    /// Evicts the oldest (lowest-revision) entries once the progression cache exceeds the cap.
    /// </summary>
    private static void TrimProgressionExcess()
    {
        TrimExcess(_supportsProgression, kvp => kvp.Value.Item1, MaxClientEntries);
    }

    private static void TrimExcess<T>(ConcurrentDictionary<string, T> cache, Func<KeyValuePair<string, T>, long> revisionOf, int max)
    {
        int overflow = cache.Count - max;
        if (overflow <= 0)
            return;

        var oldest = cache.ToList()
            .OrderBy(revisionOf)
            .Take(overflow)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (string key in oldest)
        {
            cache.TryRemove(key, out _);
        }
    }
}