using System.Collections.Generic;

namespace Mihon.ExtensionsBridge.Models.Abstractions.Browser
{
    /// <summary>
    /// Plugin resolution for browser automation providers. Providers register themselves by
    /// <see cref="IBrowserAutomationProvider.Name"/> and are resolved from preferences.
    /// </summary>
    public interface IBrowserProviderRegistry
    {
        void Register(IBrowserAutomationProvider provider);

        IReadOnlyCollection<string> Names { get; }

        /// <summary>
        /// Every registered provider, regardless of availability. Needed because a provider's
        /// install/cache roots must be pushed to it <b>before</b> its availability is probed
        /// (see BrowserAutomationService.WarmUpAsync).
        /// </summary>
        IReadOnlyCollection<IBrowserAutomationProvider> Providers { get; }

        /// <summary>Returns <c>null</c> when the name is unknown or the provider is unavailable.</summary>
        IBrowserAutomationProvider? Resolve(string name);

        /// <summary>Primary -> fallbacks -> throws when nothing is usable.</summary>
        IBrowserAutomationProvider ResolveWithFallback(
            string primary,
            IReadOnlyList<string> fallbacks);
    }
}
