using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RensaioBackend.Services.Scrobbling.Providers;

namespace RensaioBackend.Services.Rpc;

/// <summary>
/// Pushes the bound <see cref="SourceRateLimitsOptions"/> into the process-wide
/// <see cref="ProviderRateLimits"/> registry at startup and on every configuration
/// reload, so an operator can tune the per-package source limits by editing
/// appsettings.json without restarting the backend or the RPC process.
/// </summary>
internal sealed class SourceRateLimitsConfigurator : IHostedService
{
    private readonly IOptionsMonitor<SourceRateLimitsOptions> _options;
    private IDisposable? _subscription;

    public SourceRateLimitsConfigurator(IOptionsMonitor<SourceRateLimitsOptions> options)
    {
        _options = options;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ProviderRateLimits.ConfigureSourceLimits(_options.CurrentValue);
        _subscription = _options.OnChange(ProviderRateLimits.ConfigureSourceLimits);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }
}
