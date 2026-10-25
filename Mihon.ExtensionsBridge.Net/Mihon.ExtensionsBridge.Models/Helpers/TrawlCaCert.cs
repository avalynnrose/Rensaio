using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mihon.ExtensionsBridge.Models.Helpers
{
    /// <summary>
    /// Resolves the proxy's root CA for a TLS-terminating transparent proxy (trawl).
    ///
    /// The configured value (<see cref="SocksProxyPreferences.CaCertPath"/>, or
    /// <see cref="Abstractions.Browser.BrowserLaunchOptions.CaCertSource"/>) is EITHER:
    /// <list type="bullet">
    /// <item>an absolute <c>http</c>/<c>https</c> URL — trawl serves its CA at
    /// <c>http://<trawl-host>:8191/proxy-ca.crt</c>. It is downloaded at runtime and cached
    /// under the host temp folder so repeated launches do not re-fetch it; or</item>
    /// <item>a filesystem path to a PEM file.</item>
    /// </list>
    ///
    /// Detection is by absolute-URI parse, <b>never</b> by file extension.
    ///
    /// IMPORTANT: the download deliberately uses a DIRECT connection (no proxy). The CA is what
    /// makes the proxy's interception trusted, so fetching it THROUGH that proxy would be
    /// circular (and would fail certificate validation before the CA is installed).
    /// </summary>
    public static class TrawlCaCert
    {
        /// <summary>Default trawl CA endpoint, for reference/UI hints.</summary>
        public const string TrawlDefaultCaUrl = "http://localhost:8191/proxy-ca.crt";

        /// <summary>True when the value is an absolute http/https URL (download) rather than a path.</summary>
        public static bool IsUrl(string? source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            return Uri.TryCreate(source, UriKind.Absolute, out var uri)
                   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>
        /// Resolves the CA to a local PEM file path, downloading it first when
        /// <paramref name="source"/> is a URL.
        /// </summary>
        /// <param name="source">URL or PEM path. Empty/null returns <c>null</c> (no CA configured).</param>
        /// <param name="cacheDirectory">
        /// Where a downloaded CA is cached. Created if missing. When null the host temp folder is used.
        /// </param>
        /// <param name="log">Optional diagnostic sink (kept as a delegate so this pure model assembly
        /// takes no logging dependency).</param>
        /// <param name="forceRefresh">
        /// When true the cached copy is ignored and the URL is re-fetched (e.g. the proxy host
        /// changed, or the cached copy is expired).
        /// </param>
        /// <returns>A readable PEM file path, or <c>null</c> when no source is configured.</returns>
        /// <exception cref="InvalidOperationException">
        /// The source is configured but cannot be resolved (file missing, URL unreachable, or the
        /// payload is not a certificate). Never silently returns a bogus path: a failure here is
        /// meant to fail the launch loudly rather than degrade to unverified TLS.
        /// </exception>
        public static async Task<string?> ResolveToPemFileAsync(
            string? source,
            string? cacheDirectory,
            Action<string>? log = null,
            bool forceRefresh = false,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            if (!IsUrl(source))
            {
                var path = source.Trim();
                if (!File.Exists(path))
                {
                    throw new InvalidOperationException(
                        $"Configured proxy CA file was not found: '{path}'. " +
                        "Provide a valid PEM path, or a URL such as " + TrawlDefaultCaUrl + ".");
                }

                ValidatePem(File.ReadAllText(path), path);
                return path;
            }

            var cacheDir = string.IsNullOrWhiteSpace(cacheDirectory)
                ? Path.Combine(Path.GetTempPath(), "rensaio", "browser")
                : cacheDirectory;
            Directory.CreateDirectory(cacheDir);

            var cached = Path.Combine(cacheDir, "trawl-ca.crt");

            if (!forceRefresh && File.Exists(cached) && !IsExpiredOrUnreadable(cached))
            {
                log?.Invoke($"Using cached proxy CA at {cached}.");
                return cached;
            }

            var pem = await DownloadAsync(source, log, ct).ConfigureAwait(false);
            ValidatePem(pem, source);

            // Write via temp-then-move so a torn write can never leave a corrupt cached CA that
            // later fails validation on every launch.
            var tmp = cached + ".tmp";
            await File.WriteAllTextAsync(tmp, pem, ct).ConfigureAwait(false);
            File.Move(tmp, cached, overwrite: true);

            log?.Invoke($"Downloaded proxy CA from {source} to {cached}.");
            return cached;
        }

        private static async Task<string> DownloadAsync(
            string url,
            Action<string>? log,
            CancellationToken ct)
        {
            try
            {
                // Direct connection: never route the CA download through the proxy it secures.
                using var handler = new HttpClientHandler { UseProxy = false };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                using var response = await http
                    .GetAsync(url, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Failed to download proxy CA from {url}: {ex.Message}");
                throw new InvalidOperationException(
                    $"Failed to download the proxy CA from '{url}'. " +
                    "The proxy must be reachable on a DIRECT connection (the CA download never " +
                    "goes through the proxy itself). See inner exception for details.", ex);
            }
        }

        /// <summary>
        /// Converts a PEM certificate to DER (the format NSS's <c>certutil -A</c> and the Windows
        /// cert-store importers expect).
        /// </summary>
        public static byte[] PemToDer(string pem)
        {
            using var cert = new X509Certificate2(Encoding.UTF8.GetBytes(pem));
            return cert.Export(X509ContentType.Cert);
        }

        /// <summary>
        /// SHA-256 SPKI (Subject Public Key Info) hash, base64 — the value Chromium's
        /// <c>--ignore-certificate-errors-spki-list</c> expects.
        ///
        /// NOTE: the plan requires verifying whether the bundled Chromium matches this list against
        /// the LEAF or against the CA. A TLS-terminating proxy mints a fresh per-host leaf, so only
        /// a CA-level match makes this usable; the trust-store install is the primary mechanism and
        /// this is the alternative.
        /// </summary>
        public static string ComputeSpkiHash(string pem)
        {
            using var cert = new X509Certificate2(Encoding.UTF8.GetBytes(pem));
            var spki = cert.PublicKey.ExportSubjectPublicKeyInfo();
            var hash = SHA256.HashData(spki);
            return Convert.ToBase64String(hash);
        }

        private static void ValidatePem(string content, string origin)
        {
            if (string.IsNullOrWhiteSpace(content) ||
                !content.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The proxy CA resolved from '{origin}' is not a PEM certificate.");
            }

            try
            {
                using var _ = new X509Certificate2(Encoding.UTF8.GetBytes(content));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"The proxy CA resolved from '{origin}' could not be parsed as a certificate.", ex);
            }
        }

        private static bool IsExpiredOrUnreadable(string path)
        {
            try
            {
                using var cert = new X509Certificate2(path);
                return cert.NotAfter <= DateTime.Now;
            }
            catch
            {
                // Unreadable cache is treated as stale so it is re-fetched.
                return true;
            }
        }
    }
}