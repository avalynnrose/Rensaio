using RensaioBackend.Data;
using RensaioBackend.Models.Database;
using RensaioBackend.Utils.ImageHashing;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace RensaioBackend.Services.Metadata;

/// <summary>
/// Persistent PDQ hash cache keyed by cover image URL. A previously computed hash is reused
/// so the image matcher never re-downloads or re-hashes the same cover. Backed by an in-memory
/// bounded cache in front of the <c>ImageHashes</c> table.
/// </summary>
public class ImageHashCacheService
{
    private const int MaxEntries = 5000;

    private readonly AppDbContext _db;
    private readonly ILogger<ImageHashCacheService> _logger;

    // URL -> (revision, entity) with bounded size. All DB access is serialized by _dbLock
    // because AppDbContext is not thread-safe and the matcher downloads/hashes in parallel.
    private readonly ConcurrentDictionary<string, (long Revision, ImageHashCacheEntity Entity)> _mem = new(StringComparer.OrdinalIgnoreCase);
    private long _revision;
    private readonly SemaphoreSlim _dbLock = new(1, 1);

    public ImageHashCacheService(AppDbContext db, ILogger<ImageHashCacheService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Normalizes a cover URL into the stable cache key used for persistence.</summary>
    public static string Normalize(string url) => url.Trim();

    /// <summary>
    /// Returns the cached hash for a URL, or null when it has never been computed.
    /// </summary>
    public async Task<PdqHash256?> TryGetHashAsync(string url, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var key = Normalize(url);
        if (_mem.TryGetValue(key, out var memEntry) && PdqHash256.TryParse(memEntry.Entity.Hash, out var memHash))
        {
            _mem[key] = (++_revision, memEntry.Entity); // touch
            return memHash;
        }

        await _dbLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var row = await _db.ImageHashes.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Url == key, token).ConfigureAwait(false);
            if (row == null)
                return null;
            if (!PdqHash256.TryParse(row.Hash, out var hash))
            {
                _logger.LogWarning("ImageHashCache: stored hash for {Url} is malformed; ignoring.", key);
                return null;
            }
            _mem[key] = (++_revision, row);
            TrimExcess();
            return hash;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>
    /// Persists a computed hash for a URL (insert or update). Empty hashes are ignored.
    /// </summary>
    public async Task SaveHashAsync(string url, PdqResult result, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(url) || result.Hash.IsEmpty)
            return;

        var key = Normalize(url);
        var hex = result.Hash.ToString();
        var now = DateTime.UtcNow;

        await _dbLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var row = await _db.ImageHashes.FirstOrDefaultAsync(x => x.Url == key, token).ConfigureAwait(false);
            if (row == null)
            {
                row = new ImageHashCacheEntity
                {
                    Url = key,
                    Hash = hex,
                    Quality = result.Quality,
                    UpdatedUTC = now
                };
                _db.ImageHashes.Add(row);
            }
            else
            {
                row.Hash = hex;
                row.Quality = result.Quality;
                row.UpdatedUTC = now;
            }
            await _db.SaveChangesAsync(token).ConfigureAwait(false);
            _mem[key] = (++_revision, row);
            TrimExcess();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ImageHashCache: failed to persist hash for {Url}", key);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    /// <summary>Evicts the oldest (lowest-revision) in-memory entries once over the cap.</summary>
    private void TrimExcess()
    {
        int overflow = _mem.Count - MaxEntries;
        if (overflow <= 0)
            return;
        var oldest = _mem.ToList()
            .OrderBy(kvp => kvp.Value.Revision)
            .Take(overflow)
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var key in oldest)
            _mem.TryRemove(key, out _);
    }
}
