using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Mihon.ExtensionsBridge.Core.Browser;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Abstractions.Browser;
using Mihon.ExtensionsBridge.Models.Helpers;
using Xunit;

namespace Mihon.ExtensionsBridge.Tests
{
    /// <summary>
    /// Covers the trawl/HTTP proxy feature: proxy mapping into launch options, the HTTP-vs-SOCKS
    /// scheme selection, and the CA source resolver (URL vs PEM path).
    /// </summary>
    public class BrowserProxyTests
    {
        private sealed class CapturingProvider : IBrowserAutomationProvider
        {
            public BrowserLaunchOptions? Last { get; private set; }

            public string Name => "patchright";

            public BrowserProviderCapabilities Capabilities => BrowserProviderCapabilities.Fetch;

            public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

            public Task<IBrowserSession> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct = default)
            {
                Last = options;
                return Task.FromResult<IBrowserSession>(new FakeSession());
            }
        }

        private sealed class FakeSession : IBrowserSession
        {
            public Task SetDocumentAsync(string url, string html, CancellationToken ct = default) => Task.CompletedTask;
            public Task<BrowserResponse> ExecuteAsync(BrowserRequest request, CancellationToken ct = default) =>
                Task.FromResult(new BrowserResponse { StatusCode = 200, StatusMessage = "OK" });
            public Task NavigateAsync(string url, CancellationToken ct = default) => Task.CompletedTask;
            public Task WaitForLoadAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task<string?> EvaluateAsync(string script, CancellationToken ct = default) => Task.FromResult<string?>(null);
            public Task AddBindingAsync(string name, Func<string, Task<object?>> handler) => Task.CompletedTask;
            public Task AddInitScriptAsync(string script, CancellationToken ct = default) => Task.CompletedTask;
            public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(string url, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<BrowserCookie>>([]);
            public Task SetCookiesAsync(string url, IReadOnlyCollection<BrowserCookie> cookies, CancellationToken ct = default) => Task.CompletedTask;
            public Task<byte[]> ScreenshotAsync(CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class FakeFolder : IWorkingFolderStructure
        {
            public string ExtensionsFolder => Path.Combine(WorkingFolder, "extensions");
            public string TempFolder { get; init; } = Path.Combine(Path.GetTempPath(), "rensaio-proxy-tests");
            public string AndroidFolder => Path.Combine("A:", "android");
            public string WorkingFolder => Path.Combine("W:", "root");
        }

        private static BrowserAutomationService CreateService()
        {
            var registry = new BrowserProviderRegistry();
            registry.Register(new CapturingProvider());
            return new BrowserAutomationService(
                registry,
                NullLogger<BrowserAutomationService>.Instance,
                new FakeFolder(),
                () => new BrowserAutomationPreferences { Provider = "patchright" });
        }

        // ── Proxy mapping ────────────────────────────────────────────────────────────

        [Fact]
        public void HttpClientScheme_ProducesHttpProxyUrl_ForTrawl()
        {
            // trawl is HTTP-only on port 8192; the browser must be told "http", never SOCKS.
            var service = CreateService();
            service.ApplyProxyPreferences(new SocksProxyPreferences
            {
                Enabled = true,
                Scheme = "http",
                Host = "192.168.1.5",
                Port = 8192,
            });

            var options = service.BuildLaunchOptions();

            Assert.Equal("http://192.168.1.5:8192", options.ProxyUrl);
        }

        [Fact]
        public void SocksScheme_ProducesSocksUrl_ForLegacyProviders()
        {
            var service = CreateService();
            service.ApplyProxyPreferences(new SocksProxyPreferences
            {
                Enabled = true,
                Scheme = "socks5",
                Host = "10.0.0.1",
                Port = 1080,
            });

            Assert.Equal("socks5://10.0.0.1:1080", service.BuildLaunchOptions().ProxyUrl);
        }

        [Fact]
        public void ProxyDisabled_LeavesLaunchOptionsProxyFree()
        {
            // Regression: before this feature BuildLaunchOptions never set ProxyUrl, so browser
            // sessions egressed directly. That must remain true when no proxy is configured.
            var service = CreateService();
            var options = service.BuildLaunchOptions();

            Assert.Null(options.ProxyUrl);
            Assert.Null(options.CaCertSource);
            Assert.False(options.IgnoreTlsErrors);
        }

        [Fact]
        public void ProxyCarriesCredentialsAndCaSource()
        {
            var service = CreateService();
            service.ApplyProxyPreferences(new SocksProxyPreferences
            {
                Enabled = true,
                Scheme = "http",
                Host = "proxy.local",
                Port = 8192,
                Username = "user",
                Password = "secret",
                CaCertPath = "http://proxy.local:8191/proxy-ca.crt",
                IgnoreTlsErrors = true,
            });

            var options = service.BuildLaunchOptions();

            Assert.Equal("user", options.ProxyUsername);
            Assert.Equal("secret", options.ProxyPassword);
            Assert.Equal("http://proxy.local:8191/proxy-ca.crt", options.CaCertSource);
            Assert.True(options.IgnoreTlsErrors);
        }

        [Fact]
        public void ZeroPortOrBlankHost_IsTreatedAsNoProxy()
        {
            var service = CreateService();
            service.ApplyProxyPreferences(new SocksProxyPreferences { Enabled = true, Scheme = "http", Host = "", Port = 0 });

            Assert.Null(service.BuildLaunchOptions().ProxyUrl);
        }

        [Fact]
        public void ApplyingSameProxyTwice_IsANoOp()
        {
            // Guards ProxyEquals: an unchanged push must not needlessly drain the session pool.
            var service = CreateService();
            var proxy = new SocksProxyPreferences { Enabled = true, Scheme = "http", Host = "h", Port = 8192 };
            service.ApplyProxyPreferences(proxy);
            service.ApplyProxyPreferences(proxy);

            Assert.Equal("http://h:8192", service.BuildLaunchOptions().ProxyUrl);
        }

        // ── Host normalisation ───────────────────────────────────────────────────────

        [Theory]
        [InlineData("192.168.1.5", "192.168.1.5")]
        [InlineData("http://192.168.1.5", "192.168.1.5")]
        [InlineData("http://192.168.1.5/", "192.168.1.5")]
        [InlineData("socks5://192.168.1.5", "192.168.1.5")]
        [InlineData("socks4://192.168.1.5:1080", "192.168.1.5")]
        [InlineData("192.168.1.5:8192", "192.168.1.5")]
        [InlineData("http://user:pass@192.168.1.5:8192/", "192.168.1.5")]
        [InlineData("[::1]:1080", "[::1]")]
        [InlineData("  192.168.1.5  ", "192.168.1.5")]
        [InlineData("", "")]
        public void NormalizeHost_AcceptsBareHostAndFullUrl(string input, string expected)
        {
            Assert.Equal(expected, ProxyEndpoint.NormalizeHost(input));
        }

        [Fact]
        public void SchemeQualifiedHost_StillProducesCorrectProxyUrl()
        {
            // The user pasting "http://192.168.1.5" must not yield "http://http://192.168.1.5:8192".
            var service = CreateService();
            service.ApplyProxyPreferences(new SocksProxyPreferences
            {
                Enabled = true,
                Scheme = "http",
                Host = "http://192.168.1.5",
                Port = 8192,
            });

            Assert.Equal("http://192.168.1.5:8192", service.BuildLaunchOptions().ProxyUrl);
        }

        [Fact]
        public void Socks4Scheme_ProducesSocks4Url()
        {
            var service = CreateService();
            service.ApplyProxyPreferences(new SocksProxyPreferences
            {
                Enabled = true,
                Scheme = "socks4",
                Host = "10.0.0.1",
                Port = 1080,
            });

            Assert.Equal("socks4://10.0.0.1:1080", service.BuildLaunchOptions().ProxyUrl);
        }

        [Theory]
        [InlineData("http", "http")]
        [InlineData("HTTP", "http")]
        [InlineData("socks4", "socks4")]
        [InlineData("socks5", "socks5")]
        [InlineData("", "socks5")]
        [InlineData(null, "socks5")]
        [InlineData("nonsense", "socks5")]
        public void ToUrlScheme_MapsSchemeTokenWithSocks5Default(string? input, string expected)
        {
            Assert.Equal(expected, ProxyEndpoint.ToUrlScheme(input));
        }

        // ── CA source resolution ─────────────────────────────────────────────────────

        [Fact]
        public void EmptyCaSource_ResolvesToNull()
        {
            Assert.Null(TrawlCaCert.ResolveToPemFileAsync("", null).GetAwaiter().GetResult());
            Assert.Null(TrawlCaCert.ResolveToPemFileAsync(null, null).GetAwaiter().GetResult());
        }

        [Theory]
        [InlineData("http://127.0.0.1:8191/proxy-ca.crt", true)]
        [InlineData("https://host/proxy-ca.crt", true)]
        [InlineData("/etc/ssl/trawl-ca.crt", false)]
        [InlineData("C:\\certs\\trawl-ca.crt", false)]
        [InlineData("trawl-ca.crt", false)]
        public void UrlDetection_IsByAbsoluteUriParse_NotExtension(string source, bool expected)
        {
            Assert.Equal(expected, TrawlCaCert.IsUrl(source));
        }

        [Fact]
        public void MissingPemPath_FailsLoudly()
        {
            // A configured-but-unresolvable CA must throw rather than silently degrade to
            // unverified TLS (the plan's "never silently strip TLS" rule).
            var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nope.crt");
            var ex = Assert.Throws<InvalidOperationException>(
                () => TrawlCaCert.ResolveToPemFileAsync(missing, null).GetAwaiter().GetResult());
            Assert.Contains("not found", ex.Message);
        }

        [Fact]
        public void NonPemContent_FailsLoudly()
        {
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".crt");
            File.WriteAllText(path, "this is not a certificate");
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(
                    () => TrawlCaCert.ResolveToPemFileAsync(path, null).GetAwaiter().GetResult());
                Assert.Contains("not a PEM certificate", ex.Message);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ValidPemPath_IsReturnedAndSpkiHashIsStable()
        {
            using var cert = CreateSelfSigned();
            var pem = new string(PemEncoding.Write("CERTIFICATE", cert.RawData));
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".crt");
            File.WriteAllText(path, pem);
            try
            {
                var resolved = TrawlCaCert.ResolveToPemFileAsync(path, null).GetAwaiter().GetResult();
                Assert.Equal(path, resolved);

                // The SPKI hash is what --ignore-certificate-errors-spki-list consumes; it must be
                // deterministic and non-empty.
                var hash = TrawlCaCert.ComputeSpkiHash(pem);
                Assert.False(string.IsNullOrWhiteSpace(hash));
                Assert.Equal(hash, TrawlCaCert.ComputeSpkiHash(pem));

                // PEM -> DER is what the NSS import path needs.
                var der = TrawlCaCert.PemToDer(pem);
                Assert.NotEmpty(der);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void UnreachableCaUrl_FailsLoudly()
        {
            // A closed loopback port: the download must fail loudly, not return a bogus path.
            var url = "http://127.0.0.1:1/proxy-ca.crt";
            Assert.Throws<InvalidOperationException>(
                () => TrawlCaCert.ResolveToPemFileAsync(url, null).GetAwaiter().GetResult());
        }

        private static X509Certificate2 CreateSelfSigned()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=trawl-test-ca", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        }
    }
}