namespace Rensaio.RPC.Runtime;

/// <summary>
/// Global concurrency budget for in-flight calls into Mihon source extensions.
///
/// Ported from the backend because each source call crosses the IKVM boundary
/// and can spawn/attach worker threads; keeping the number of concurrently
/// executing extension calls bounded protects the RPC process thread budget
/// regardless of how many backend threads call in parallel. No locking inside
/// Mihon/IKVM is modified — this only throttles at the RPC service layer.
/// </summary>
public sealed class SourceCallGate : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly int _capacity;

    public SourceCallGate(int capacity = 8)
    {
        _capacity = capacity;
        _gate = new SemaphoreSlim(capacity, capacity);
    }

    public int Capacity => _capacity;

    public int Available => _gate.CurrentCount;

    public async Task<IDisposable> AcquireAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        return new Releaser(_gate);
    }

    public void Dispose() => _gate.Dispose();

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _disposed;

        public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose()
        {
            if (_disposed)
                return;
            _semaphore.Release();
            _disposed = true;
        }
    }
}

/// <summary>
/// Wraps a source call with the global gate, retry/backoff on transient HTTP
/// failures, and a hard wall-clock timeout. Ported from MihonBridgeService so
/// retries that must inspect the extension's own HTTP status happen server-side
/// where the extension runs.
/// </summary>
public sealed class SourceCallRunner
{
    private const int DefaultMaxRetries = 5;
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private readonly SourceCallGate _gate;
    private readonly KeyedAsyncLock _perMangaLock;
    private readonly ILogger _logger;

    public SourceCallRunner(SourceCallGate gate, KeyedAsyncLock perMangaLock, ILogger<SourceCallRunner> logger)
    {
        _gate = gate;
        _perMangaLock = perMangaLock;
        _logger = logger;
    }

    private static bool IsRetryableHttpStatus(int status)
        => status == 429 || status == 404;

    private static TimeSpan ComputeBackoff(int attempt)
    {
        int shift = Math.Min(attempt, 4);
        double baseSeconds = RetryBaseDelay.TotalSeconds * (1 << shift);
        double jitter = Random.Shared.NextDouble() * 0.5;
        return TimeSpan.FromSeconds(baseSeconds + jitter);
    }

    /// <summary>
    /// Runs a source operation under the global gate, optionally serialized on
    /// <paramref name="mangaLockKey"/> for same-manga calls.
    ///
    /// Exceptions propagate to the caller, which maps them onto gRPC status so
    /// the backend observes the same failure categories as before the split.
    /// </summary>
    public async Task<T?> RunAsync<T>(
        Func<Task<T>> func,
        string lockKey = null!,
        CancellationToken token = default) where T : class
    {
        IDisposable? mangaLock = null;
        if (lockKey != null)
            mangaLock = await _perMangaLock.LockAsync(lockKey, token).ConfigureAwait(false);

        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (await _gate.AcquireAsync(token).ConfigureAwait(false))
                    {
                        return await func().ConfigureAwait(false);
                    }
                }
                catch (HttpRequestException httpEx)
                {
                    int status = (int?)httpEx.StatusCode ?? 500;
                    if (IsRetryableHttpStatus(status) && attempt < DefaultMaxRetries)
                    {
                        TimeSpan delay = ComputeBackoff(attempt);
                        _logger.LogDebug(
                            "Retrying source call (attempt {Attempt}/{Max}) after {Delay}s due to HTTP {Status}",
                            attempt + 1, DefaultMaxRetries, delay.TotalSeconds, status);
                        await Task.Delay(delay, token).ConfigureAwait(false);
                        continue;
                    }

                    throw;
                }
            }
        }
        finally
        {
            mangaLock?.Dispose();
        }
    }
}
