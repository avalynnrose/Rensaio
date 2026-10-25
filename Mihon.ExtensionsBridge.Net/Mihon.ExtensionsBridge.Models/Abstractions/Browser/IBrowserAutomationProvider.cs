using System.Threading;
using System.Threading.Tasks;

namespace Mihon.ExtensionsBridge.Models.Abstractions.Browser
{
    /// <summary>
    /// A pluggable way of running web content. Implementations are the only place that knows about
    /// a specific engine (Patchright, Playwright, CDP, ...). The orchestration service and the
    /// Java/Kotlin layer depend only on this abstraction, so a new backend needs no Java change.
    /// </summary>
    public interface IBrowserAutomationProvider
    {
        /// <summary>Stable identifier used in settings ("patchright", "playwright", "camoufox", "cdp").</summary>
        string Name { get; }

        /// <summary>What this backend can actually do; callers must degrade gracefully.</summary>
        BrowserProviderCapabilities Capabilities { get; }

        /// <summary>
        /// Cheap, non-throwing check that this provider can run on this machine (driver present,
        /// browser installed, required OS libs available).
        /// </summary>
        Task<bool> IsAvailableAsync(CancellationToken ct = default);

        /// <summary>Start a session. One session == one isolated browser context.</summary>
        Task<IBrowserSession> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct = default);
    }
}
