using System.Collections.Generic;

namespace Mihon.ExtensionsBridge.Models.Abstractions.Browser
{
    /// <summary>
    /// One fetch/navigation performed on behalf of an extension request. This is the single DTO
    /// that crosses the IKVM boundary (as JSON) in the inverted host design.
    /// </summary>
    public sealed class BrowserRequest
    {
        public string Url { get; init; } = "";

        public string Method { get; init; } = "GET";

        public IDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

        public string? Body { get; init; }

        /// <summary>Origin to establish before the fetch (e.g. <c>https://example.com/</c>).</summary>
        public string? ContextUrl { get; init; }

        public int TimeoutSeconds { get; init; } = 60;

        /// <summary>One of <c>load</c> | <c>domcontentloaded</c> | <c>networkidle0</c>.</summary>
        public string? WaitUntil { get; init; }

        /// <summary>
        /// Cookies to seed into the session before the fetch (the JVM cookie store is the source
        /// of truth; see plan §12.2 "Cookie seeding -> browser").
        /// </summary>
        public IReadOnlyList<BrowserCookie> Cookies { get; init; } = [];
    }
}
