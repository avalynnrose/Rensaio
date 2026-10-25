namespace Mihon.ExtensionsBridge.Models.Abstractions.Browser
{
    /// <summary>
    /// Engine-neutral cookie representation. <paramref name="Expires"/> is a Unix timestamp in
    /// seconds, or <c>null</c> for a session cookie.
    /// </summary>
    public sealed record BrowserCookie(
        string Name,
        string Value,
        string Domain,
        string Path,
        bool Secure,
        bool HttpOnly,
        double? Expires);
}
