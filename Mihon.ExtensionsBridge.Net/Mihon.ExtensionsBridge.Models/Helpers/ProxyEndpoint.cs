using System;

namespace Mihon.ExtensionsBridge.Models.Helpers
{
    /// <summary>
    /// Helpers for the proxy host/endpoint string.
    ///
    /// The UI accepts either a bare host or a full URL, and users naturally paste both forms:
    /// <c>192.168.1.5</c> and <c>http://192.168.1.5</c> must yield the same host. Normalising here
    /// (rather than at each consumer) keeps the browser, the JVM bridge and the solver request
    /// consistent.
    /// </summary>
    public static class ProxyEndpoint
    {
        /// <summary>
        /// Extracts a bare host from a user-supplied value, tolerating a scheme
        /// (<c>http://</c>, <c>socks5://</c>), userinfo (<c>user:pass@</c>), a path/<c>/</c> suffix
        /// and a <c>:port</c> suffix. IPv6 literals in brackets (<c>[::1]</c>) are preserved.
        /// </summary>
        /// <returns>The bare host, or an empty string when nothing usable was supplied.</returns>
        public static string NormalizeHost(string? raw)
        {
            var value = (raw ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return string.Empty;
            }

            // "scheme://rest" -> "rest"
            var schemeSep = value.IndexOf("://", StringComparison.Ordinal);
            if (schemeSep >= 0)
            {
                value = value[(schemeSep + 3)..];
            }

            // Drop any path/query/fragment ("host:8192/" -> "host:8192").
            var pathStart = value.IndexOfAny(['/', '?', '#']);
            if (pathStart >= 0)
            {
                value = value[..pathStart];
            }

            // Drop userinfo ("user:pass@host" -> "host").
            var at = value.LastIndexOf('@');
            if (at >= 0)
            {
                value = value[(at + 1)..];
            }

            if (value.StartsWith('['))
            {
                // Bracketed IPv6: keep through the closing bracket ("[::1]:1080" -> "[::1]").
                var close = value.IndexOf(']');
                if (close >= 0)
                {
                    value = value[..(close + 1)];
                }
            }
            else
            {
                // Drop a :port suffix, but only for non-bracketed hosts: a bracketless value with
                // more than one colon is an IPv6 literal, and its colons are part of the address.
                var colon = value.LastIndexOf(':');
                if (colon > 0 && value.IndexOf(':') == colon)
                {
                    value = value[..colon];
                }
            }

            return value.Trim();
        }

        /// <summary>
        /// Maps the stored scheme token to a URL scheme token, defaulting to SOCKS5 for anything
        /// unrecognised (the historical behaviour).
        /// </summary>
        public static string ToUrlScheme(string? scheme)
        {
            var value = (scheme ?? string.Empty).Trim();
            if (value.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                return "http";
            }
            if (value.Equals("socks4", StringComparison.OrdinalIgnoreCase))
            {
                return "socks4";
            }
            return "socks5";
        }
    }
}