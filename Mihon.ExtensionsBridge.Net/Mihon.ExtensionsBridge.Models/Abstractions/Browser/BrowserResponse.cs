using System.Collections.Generic;

namespace Mihon.ExtensionsBridge.Models.Abstractions.Browser
{
    /// <summary>
    /// Result of a <see cref="BrowserRequest"/>. <see cref="Error"/> is set (instead of throwing)
    /// when the engine reported a failure so the caller can degrade to the direct network chain.
    /// </summary>
    public sealed class BrowserResponse
    {
        public int StatusCode { get; init; }

        public string StatusMessage { get; init; } = "";

        public IDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

        public byte[] Body { get; init; } = [];

        public IReadOnlyList<BrowserCookie> Cookies { get; init; } = [];

        public string? UserAgent { get; init; }

        public string? Error { get; init; }
    }
}
