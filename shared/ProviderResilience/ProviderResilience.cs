using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Rensaio.Resilience;

/// <summary>
/// Tuning for the outbound provider resilience handler.
/// </summary>
public sealed class ProviderResilienceOptions
{
    /// <summary>Number of retries after the initial attempt (3 → up to 4 calls).</summary>
    public int RetryCount { get; init; } = 3;

    /// <summary>Base delay; the delay before retry N is Base * 2^(N-1).</summary>
    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound for a single retry delay (ignored when a longer Retry-After is sent).</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a host's circuit stays open once tripped.</summary>
    public TimeSpan CircuitBreakDuration { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Consecutive failures against a single host required to trip its circuit.</summary>
    public int CircuitBreakThreshold { get; init; } = 5;

    public static ProviderResilienceOptions Default { get; } = new();
}

/// <summary>
/// Classifies outbound HTTP transport failures shared by every provider client.
///
/// A PERMANENT DNS failure — the host no longer resolves (<c>SocketError.HostNotFound</c>,
/// "No such host is known") — must never be retried: the host is gone, so retrying only delays
/// the caller's own recovery (for example the image provider rotating a stale cover domain onto
/// the extension's new <c>homeUrl</c>/mirror) and floods the log with identical failures. A
/// transient network/DNS blip is NOT permanent: a routing error or a timeout is recovered by the
/// normal retry policy.
///
/// This file is compiled into both RensaioBackend and RensaioOAuthProxy (linked source), so the
/// classification stays identical in both services.
/// </summary>
public static class ProviderHttpFailures
{
    /// <summary>
    /// True when the failure (or one of its inner exceptions) is a permanent DNS resolution
    /// failure, i.e. the host itself does not resolve. Matched on the <see cref="SocketError"/>
    /// code first, then on the OS message so it is recognized regardless of how the exception was
    /// wrapped (a bare <see cref="SocketException"/>, or an <see cref="HttpRequestException"/>
    /// with the socket error as its inner exception).
    /// </summary>
    public static bool IsPermanentDnsFailure(Exception? ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket &&
                (socket.SocketErrorCode == SocketError.HostNotFound
                 || socket.SocketErrorCode == SocketError.NoData))
            {
                return true;
            }

            if (current.Message.Contains("No such host is known", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("nodename nor servname", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A <see cref="DelegatingHandler"/> applied to every outbound scrobbler / metadata
/// provider client (AniList, MyAnimeList, Kitsu, MangaDex, MangaBaka, Bangumi,
/// MangaUpdates, ComicVine) and to the OAuth proxy's provider token endpoints.
/// Providers routinely answer with rate limits (429) and transient 5xx errors, so
/// each request is attempted with exponential backoff, and each provider host is
/// protected by an independent circuit breaker that fails fast while it is unhealthy.
///
/// Retry: up to <see cref="ProviderResilienceOptions.RetryCount"/> retries with
/// delays of 0.5s, 1s, 2s (capped at 10s), covering transport failures, request
/// timeouts, and HTTP 408/425/429/5xx. A provider-supplied <c>Retry-After</c>
/// header, when present and longer, wins (still capped at 10s).
///
/// Circuit breaker: per host (<c>scheme://host:port</c>), so one failing provider
/// never short-circuits a healthy one. After
/// <see cref="ProviderResilienceOptions.CircuitBreakThreshold"/> consecutive failed
/// requests the circuit opens for <see cref="ProviderResilienceOptions.CircuitBreakDuration"/>
/// (1 minute). While open, calls are short-circuited with a synthetic HTTP 503 so
/// existing callers (which either check <c>IsSuccessStatusCode</c> or call
/// <c>EnsureSuccessStatusCode</c>) behave exactly as they would against a real
/// provider outage. The next call after the open window acts as a half-open probe.
///
/// This file is compiled into both RensaioBackend and RensaioOAuthProxy (linked
/// source) so the resilience behaviour cannot drift between the two services.
/// </summary>
public sealed class ResilientProviderHttpHandler : DelegatingHandler
{
    private static readonly ConcurrentDictionary<string, HostCircuit> Circuits = new();

    private readonly ProviderResilienceOptions _options;
    private readonly ILogger _logger;

    public ResilientProviderHttpHandler(ProviderResilienceOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Fail fast while the provider's circuit is open.
        var host = HostKey(request.RequestUri);
        if (!TryEnterCircuit(host, out var circuitOpenResponse))
        {
            _logger.LogWarning("Provider circuit open for {Host}; short-circuiting {Method} {Uri}",
                host, request.Method, request.RequestUri);
            return circuitOpenResponse!;
        }

        // Buffer the body once so the request can be rebuilt for each retry.
        byte[]? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        HttpResponseMessage? response = null;
        Exception? lastException = null;

        for (var attempt = 0; attempt <= _options.RetryCount; attempt++)
        {
            if (attempt > 0)
            {
                var delay = ComputeDelay(attempt, response);
                _logger.LogWarning(
                    "Retrying provider call {Method} {Uri} (attempt {Attempt}/{Total}) in {Delay:0.##}s",
                    request.Method, request.RequestUri, attempt, _options.RetryCount, delay.TotalSeconds);
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Caller cancelled during backoff — surface the last result/exception.
                    break;
                }
                response?.Dispose();
                response = null;
            }

            var attemptRequest = BuildRequest(request, body);
            try
            {
                response = await base.SendAsync(attemptRequest, cancellationToken).ConfigureAwait(false);
                lastException = null;
            }
            catch (HttpRequestException ex)
            {
                // A permanent DNS failure (the host no longer resolves) is NOT retried and does NOT
                // trip the host circuit: the host is gone, so retrying only delays the caller's own
                // recovery (e.g. the image provider rotating a stale cover domain onto the source's
                // new homeUrl/mirror). Rethrow immediately — the caller classifies and acts on it.
                if (ProviderHttpFailures.IsPermanentDnsFailure(ex))
                {
                    throw;
                }

                lastException = ex;
                response = null;
            }
            catch (TimeoutException ex)
            {
                // A socket/connect timeout that surfaced unwrapped — transient.
                lastException = ex;
                response = null;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A timeout (HttpClient.Timeout / SocketsHttpHandler) surfaces as TaskCanceledException
                // — an OperationCanceledException — while the caller token is NOT cancelled; it is a
                // transient timeout worth retrying rather than a deliberate cancellation. Catching the
                // base type also covers the less common timeout shapes that are not TaskCanceledException.
                lastException = ex;
                response = null;
            }

            if (lastException is null && !IsTransientStatus(response!.StatusCode))
            {
                RecordSuccess(host);
                return response;
            }

            RecordFailure(host, lastException, response);
        }

        if (response is not null)
        {
            return response;
        }

        throw lastException ?? new HttpRequestException($"Provider request to {request.RequestUri} failed after {_options.RetryCount + 1} attempts.");
    }

    private static string HostKey(Uri? uri) =>
        uri is null ? "unknown" : $"{uri.Scheme}://{uri.Authority}";

    private bool TryEnterCircuit(string host, out HttpResponseMessage? shortCircuit)
    {
        var circuit = Circuits.GetOrAdd(host, _ => new HostCircuit());
        lock (circuit.Gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (circuit.OpenUntil > now)
            {
                shortCircuit = CreateCircuitOpenResponse();
                return false;
            }

            // Half-open probe: leave the counter one below the threshold so a single
            // failure re-opens the circuit immediately, while a success resets it.
            if (circuit.OpenUntil != DateTimeOffset.MinValue)
            {
                circuit.ConsecutiveFailures = Math.Max(0, _options.CircuitBreakThreshold - 1);
                circuit.OpenUntil = DateTimeOffset.MinValue;
            }

            shortCircuit = null;
            return true;
        }
    }

    private void RecordSuccess(string host)
    {
        var circuit = Circuits.GetOrAdd(host, _ => new HostCircuit());
        lock (circuit.Gate)
        {
            circuit.ConsecutiveFailures = 0;
            circuit.OpenUntil = DateTimeOffset.MinValue;
        }
    }

    private void RecordFailure(string host, Exception? exception, HttpResponseMessage? response)
    {
        var circuit = Circuits.GetOrAdd(host, _ => new HostCircuit());
        lock (circuit.Gate)
        {
            circuit.ConsecutiveFailures++;
            if (circuit.ConsecutiveFailures >= _options.CircuitBreakThreshold && circuit.OpenUntil <= DateTimeOffset.UtcNow)
            {
                circuit.OpenUntil = DateTimeOffset.UtcNow + _options.CircuitBreakDuration;
                _logger.LogError(
                    "Provider circuit opened for {Host} after {Failures} consecutive failures; calls short-circuited for {Break:0.##}s ({Reason})",
                    host, circuit.ConsecutiveFailures, _options.CircuitBreakDuration.TotalSeconds, Describe(exception, response));
            }
        }
    }

    private TimeSpan ComputeDelay(int attempt, HttpResponseMessage? response)
    {
        // Provider-supplied Retry-After, if longer than the backoff, wins (capped).
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter is not null)
        {
            var hinted = retryAfter.Delta ?? (retryAfter.Date.HasValue ? retryAfter.Date.Value - DateTimeOffset.UtcNow : null);
            if (hinted is { } value && value > TimeSpan.Zero)
            {
                return value > _options.MaxRetryDelay ? _options.MaxRetryDelay : value;
            }
        }

        var delay = TimeSpan.FromTicks(_options.BaseRetryDelay.Ticks * (long)Math.Pow(2, attempt - 1));
        return delay > _options.MaxRetryDelay ? _options.MaxRetryDelay : delay;
    }

    /// <summary>Transient status codes worth retrying: 408, 425, 429 and all 5xx.</summary>
    public static bool IsTransientStatus(HttpStatusCode status) =>
        status == HttpStatusCode.RequestTimeout
        || status == (HttpStatusCode)425
        || status == (HttpStatusCode)429
        || (int)status >= 500;

    private static HttpRequestMessage BuildRequest(HttpRequestMessage template, byte[]? body)
    {
        var message = new HttpRequestMessage(template.Method, template.RequestUri)
        {
            Version = template.Version,
            VersionPolicy = template.VersionPolicy
        };

        if (body is not null)
        {
            var content = new ByteArrayContent(body);
            if (template.Content is not null)
            {
                foreach (var header in template.Content.Headers)
                {
                    content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
            message.Content = content;
        }

        foreach (var header in template.Headers)
        {
            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return message;
    }

    private static HttpResponseMessage CreateCircuitOpenResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            ReasonPhrase = "Provider circuit open",
            Content = new StringContent("{\"error\":\"provider circuit open\"}", System.Text.Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation("X-Rensaio-Circuit", "open");
        return response;
    }

    private static string Describe(Exception? exception, HttpResponseMessage? response)
    {
        if (exception is not null) return exception.GetType().Name;
        return $"HTTP {(int?)response?.StatusCode}";
    }

    private sealed class HostCircuit
    {
        public readonly object Gate = new();
        public int ConsecutiveFailures;
        public DateTimeOffset OpenUntil = DateTimeOffset.MinValue;
    }
}

/// <summary>
/// Registration helpers for <see cref="ResilientProviderHttpHandler"/>.
/// </summary>
public static class ProviderResilienceExtensions
{
    /// <summary>
    /// Registers the shared <see cref="ProviderResilienceOptions"/> instance. Call once during startup.
    /// </summary>
    public static IServiceCollection AddProviderResilience(this IServiceCollection services)
    {
        services.AddSingleton(ProviderResilienceOptions.Default);
        return services;
    }

    /// <summary>
    /// Attaches the provider resilience handler (exponential retry + per-host
    /// circuit breaker) to a named HTTP client.
    /// </summary>
    public static IHttpClientBuilder AddProviderResilience(this IHttpClientBuilder builder)
    {
        return builder.AddHttpMessageHandler(sp =>
            new ResilientProviderHttpHandler(
                sp.GetService<ProviderResilienceOptions>() ?? ProviderResilienceOptions.Default,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<ResilientProviderHttpHandler>()));
    }
}
