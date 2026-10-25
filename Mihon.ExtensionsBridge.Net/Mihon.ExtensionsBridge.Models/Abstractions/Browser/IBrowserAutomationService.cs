using System.Threading;
using System.Threading.Tasks;

namespace Mihon.ExtensionsBridge.Models.Abstractions.Browser
{
    /// <summary>
    /// The single entry point for browser automation. Owns provider selection, session pooling,
    /// the concurrency cap and lifecycle — all in plain .NET (see plan §4.2 / §12).
    /// </summary>
    public interface IBrowserAutomationService
    {
        bool IsAvailable { get; }

        /// <summary>
        /// Fast, non-reserving advisory check (the port of the old <c>RendererGate.canSpawn()</c>).
        /// False means the caller should degrade to the direct network chain.
        /// </summary>
        bool CapacityAvailable { get; }

        /// <summary>
        /// Atomically reserves a renderer/session slot — the authoritative cap, the port of the old
        /// <c>RendererGate.reserve()</c>. Called immediately before creating a browser.
        ///
        /// BLOCKS until a slot frees or <paramref name="timeout"/> elapses. Waiting (rather than
        /// refusing) is correct here: a caller that needs a live browser — an extension's
        /// <c>runWebView</c> — has no fallback, so failing it immediately would turn ordinary
        /// contention into a broken source. The timeout only exists so a genuine deadlock surfaces
        /// as a logged error instead of hanging forever.
        /// </summary>
        bool TryReserveSessionSlot(TimeSpan timeout);

        /// <summary>Releases a slot taken by <see cref="TryReserveSessionSlot"/>.</summary>
        void ReleaseSessionSlot();

        /// <summary>Diagnostics (mirrors the old <c>RendererGate.stats()</c>).</summary>
        string DescribeStats();

        Task<BrowserResponse> FetchAsync(BrowserRequest request, CancellationToken ct = default);

        Task<IBrowserSession> OpenSessionAsync(BrowserLaunchOptions? options = null, CancellationToken ct = default);

        Task WarmUpAsync(CancellationToken ct = default);

        Task ShutdownAsync();
    }
}
