using System;
using System.Collections.Generic;
using System.Linq;
using Mihon.ExtensionsBridge.Models.Abstractions.Browser;

namespace Mihon.ExtensionsBridge.Core.Browser
{
    /// <summary>
    /// Default <see cref="IBrowserProviderRegistry"/>. Registration is keyed by the provider's
    /// stable <see cref="IBrowserAutomationProvider.Name"/> (case-insensitive). Resolution only
    /// returns a provider that reports itself available, so the fallback chain is honest.
    /// </summary>
    public sealed class BrowserProviderRegistry : IBrowserProviderRegistry
    {
        private readonly Dictionary<string, IBrowserAutomationProvider> _providers =
            new(StringComparer.OrdinalIgnoreCase);

        public void Register(IBrowserAutomationProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (string.IsNullOrWhiteSpace(provider.Name))
            {
                throw new ArgumentException("Provider name must not be empty.", nameof(provider));
            }

            _providers[provider.Name] = provider;
        }

        public IReadOnlyCollection<string> Names => _providers.Keys.ToArray();

        public IReadOnlyCollection<IBrowserAutomationProvider> Providers => _providers.Values.ToArray();

        public IBrowserAutomationProvider? Resolve(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            if (!_providers.TryGetValue(name, out var provider))
            {
                return null;
            }

            // Cheap, non-throwing availability probe. A provider that cannot run here is treated
            // as absent so the caller moves on to the next candidate in the chain.
            try
            {
                return provider.IsAvailableAsync().GetAwaiter().GetResult() ? provider : null;
            }
            catch
            {
                return null;
            }
        }

        public IBrowserAutomationProvider ResolveWithFallback(
            string primary,
            IReadOnlyList<string> fallbacks)
        {
            var resolved = Resolve(primary);
            if (resolved is not null)
            {
                return resolved;
            }

            foreach (var fallback in fallbacks ?? [])
            {
                resolved = Resolve(fallback);
                if (resolved is not null)
                {
                    return resolved;
                }
            }

            throw new BrowserUnavailableException(
                $"No browser automation provider available. Requested '{primary}'; " +
                $"registered: [{string.Join(", ", Names)}].");
        }
    }
}
