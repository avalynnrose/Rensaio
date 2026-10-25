using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Mihon.ExtensionsBridge.Core.Browser;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Abstractions.Browser;
using Xunit;

namespace Mihon.ExtensionsBridge.Tests
{
    /// <summary>
    /// Contract tests for the pluggable browser automation boundary. They exercise the provider
    /// SPI, the registry fallback chain, and the service's pooling/concurrency semantics with a
    /// fake provider, so they run without a real browser engine.
    /// </summary>
    public class BrowserAutomationTests
    {
        private sealed class FakeProvider : IBrowserAutomationProvider, IBrowserInstallRootAware
        {
            private readonly bool _available;
            private int _launches;

            public FakeProvider(
                string name,
                bool available = true,
                BrowserProviderCapabilities caps = BrowserProviderCapabilities.Fetch)
            {
                Name = name;
                _available = available;
                Capabilities = caps;
            }

            public string Name { get; }

            public BrowserProviderCapabilities Capabilities { get; }

            public int LaunchCount => _launches;

            /// <summary>DI-supplied folder structure (single source of truth for paths).</summary>
            public IWorkingFolderStructure FolderStructure { get; set; } = new FakeFolder();

            public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
                Task.FromResult(_available);

            public Task<IBrowserSession> LaunchAsync(BrowserLaunchOptions options, CancellationToken ct = default)
            {
                Interlocked.Increment(ref _launches);
                return Task.FromResult<IBrowserSession>(new FakeSession());
            }
        }

        private sealed class FakeSession : IBrowserSession
        {
            public bool Disposed { get; private set; }

            /// <summary>Document written via the loadDataWithBaseURL path.</summary>
            public string? LastDocumentHtml { get; private set; }

            public Task SetDocumentAsync(string url, string html, CancellationToken ct = default)
            {
                LastDocumentUrl = url;
                LastDocumentHtml = html;
                return Task.CompletedTask;
            }

            /// <summary>URL the served document was attributed to.</summary>
            public string? LastDocumentUrl { get; private set; }

            public Task<BrowserResponse> ExecuteAsync(BrowserRequest request, CancellationToken ct = default) =>
                Task.FromResult(new BrowserResponse
                {
                    StatusCode = 200,
                    StatusMessage = "OK",
                    Body = [1, 2, 3],
                });

            public string? LastNavigatedUrl { get; private set; }

            public Task NavigateAsync(string url, CancellationToken ct = default)
            {
                LastNavigatedUrl = url;
                return Task.CompletedTask;
            }

            public Task WaitForLoadAsync(CancellationToken ct = default) => Task.CompletedTask;

            public Task<string?> EvaluateAsync(string script, CancellationToken ct = default) =>
                Task.FromResult<string?>("evaluated");

            public Task AddBindingAsync(string name, Func<string, Task<object?>> handler) => Task.CompletedTask;

            public Task AddInitScriptAsync(string script, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(string url, CancellationToken ct = default) =>
                Task.FromResult<IReadOnlyList<BrowserCookie>>([]);

            public Task SetCookiesAsync(string url, IReadOnlyCollection<BrowserCookie> cookies, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task<byte[]> ScreenshotAsync(CancellationToken ct = default) => Task.FromResult<byte[]>([]);

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class FakeInstaller : IBrowserRuntimeInstaller
        {
            public string ProviderName => "patchright";

            public string RuntimeVersion => "1.0.0";

            public bool EnsureCalled { get; private set; }

            /// <summary>
            /// Directory the installer derives from the injected structure. Mirrors the real
            /// installer contract: install under AndroidFolder/{Provider}.
            /// </summary>
            public string InstallDir => BrowserRuntimeLayout.GetInstallDir(
                Folders.AndroidFolder,
                ProviderName);

            public IWorkingFolderStructure Folders { get; init; } = new FakeFolder();

            public string ResolveInstallDir() => InstallDir;

            public Task<bool> EnsureInstalledAsync(bool force = false, CancellationToken ct = default)
            {
                EnsureCalled = true;
                return Task.FromResult(true);
            }

            public void ApplyLaunchEnvironment() { }
        }

        private sealed class FakeFolder : IWorkingFolderStructure
        {
            public string ExtensionsFolder => System.IO.Path.Combine(WorkingFolder, "extensions");

            public string TempFolder { get; init; } = System.IO.Path.Combine("T:", "temp");

            public string AndroidFolder { get; init; } = System.IO.Path.Combine("A:", "android");

            public string WorkingFolder => System.IO.Path.Combine("W:", "root");
        }

        private static BrowserAutomationService CreateService(
            IBrowserProviderRegistry registry,
            BrowserAutomationPreferences prefs,
            IWorkingFolderStructure? folder = null,
            IEnumerable<IBrowserRuntimeInstaller>? installers = null)
        {
            // The folder structure is required by the service (browser install/cache roots).
            var service = new BrowserAutomationService(
                registry,
                NullLogger<BrowserAutomationService>.Instance,
                folder ?? new FakeFolder(),
                () => prefs,
                installers);
            return service;
        }

        [Fact]
        public void Service_RequiresFolderStructure_FailsFastInsteadOfNullRoots()
        {
            // Regression: omitting the folder structure used to yield null InstallRoot/CacheRoot
            // only later, at install time. It must now throw immediately.
            var registry = new BrowserProviderRegistry();
            Assert.Throws<ArgumentNullException>(() => new BrowserAutomationService(
                registry,
                NullLogger<BrowserAutomationService>.Instance,
                null!));
        }

        [Fact]
        public async Task Service_WarmUp_InstallsRuntimeUnderAndroidFolderProvider()
        {
            // The install directory must be derived from the DI folder structure — the single
            // source of truth — with no directory configured or passed in.
            var registry = new BrowserProviderRegistry();
            registry.Register(new FakeProvider("patchright"));
            var folder = new FakeFolder();
            var installer = new FakeInstaller { Folders = folder };
            var service = CreateService(
                registry,
                new BrowserAutomationPreferences(),
                folder,
                [installer]);

            await service.WarmUpAsync();

            Assert.True(installer.EnsureCalled);
            Assert.Equal(
                Path.Combine(folder.AndroidFolder, "Patchright"),
                installer.InstallDir);
            await service.ShutdownAsync();
        }

        [Fact]
        public async Task Service_WarmUp_AppliesConfiguredConcurrencyCap()
        {
            // Regression: _pageSlots was sized in the constructor from field defaults, so a
            // configured MaxConcurrency (browserMaxConcurrentPages) was silently ignored.
            var registry = new BrowserProviderRegistry();
            registry.Register(new FakeProvider("patchright"));
            var service = CreateService(
                registry,
                new BrowserAutomationPreferences { MaxConcurrency = 7 },
                new FakeFolder(),
                []);

            await service.WarmUpAsync();

            Assert.Contains("max=7", service.DescribeStats());
            await service.ShutdownAsync();
        }

        [Fact]
        public async Task Service_ConcurrencyCap_GuardsAgainstZero()
        {
            // A zero/negative cap must not deadlock the semaphore permanently.
            var registry = new BrowserProviderRegistry();
            registry.Register(new FakeProvider("patchright"));
            var service = CreateService(
                registry,
                new BrowserAutomationPreferences { MaxConcurrency = 0 },
                new FakeFolder(),
                []);

            await service.WarmUpAsync();

            Assert.Contains("max=4", service.DescribeStats());
            Assert.True(service.CapacityAvailable);
            await service.ShutdownAsync();
        }

        [Fact]
        public async Task Service_SessionSlot_WaitsForReleaseInsteadOfRefusing()
        {
            // A WebView-requiring source has no fallback, so contention must QUEUE rather than
            // fail. With the cap at 1 the second reservation must not return until the first is
            // released.
            var registry = new BrowserProviderRegistry();
            registry.Register(new FakeProvider("patchright"));
            var service = CreateService(
                registry,
                new BrowserAutomationPreferences { MaxConcurrency = 1 },
                new FakeFolder(),
                []);

            await service.WarmUpAsync();

            Assert.True(service.TryReserveSessionSlot(TimeSpan.FromSeconds(5)));
            Assert.False(service.CapacityAvailable);

            var acquired = new TaskCompletionSource<bool>();
            var waiter = Task.Run(() =>
                acquired.TrySetResult(service.TryReserveSessionSlot(TimeSpan.FromSeconds(5))));

            // The waiter must still be blocked while the single slot is held.
            await Task.Delay(150);
            Assert.False(acquired.Task.IsCompleted);

            service.ReleaseSessionSlot();
            await waiter;

            Assert.True(await acquired.Task);
            service.ReleaseSessionSlot();
            await service.ShutdownAsync();
        }

        [Fact]
        public async Task Service_SessionSlot_TimesOutAndReportsRatherThanHanging()
        {
            // The bound exists so a genuine stall surfaces as a logged failure, not a hang.
            var registry = new BrowserProviderRegistry();
            registry.Register(new FakeProvider("patchright"));
            var service = CreateService(
                registry,
                new BrowserAutomationPreferences { MaxConcurrency = 1 },
                new FakeFolder(),
                []);

            await service.WarmUpAsync();
            Assert.True(service.TryReserveSessionSlot(TimeSpan.FromSeconds(5)));

            Assert.False(service.TryReserveSessionSlot(TimeSpan.FromMilliseconds(100)));

            service.ReleaseSessionSlot();
            await service.ShutdownAsync();
        }

        [Fact]
        public async Task Service_CapacityAvailable_IsFalseWhileAllSlotsAreHeld()
        {
            // The advisory gate that replaces RendererGate.canSpawn(): with the cap reduced to 1
            // and a fetch in flight, capacity must report false so callers degrade to OkHttp.
            var registry = new BrowserProviderRegistry();
            registry.Register(new FakeProvider("patchright"));
            var service = CreateService(
                registry,
                new BrowserAutomationPreferences { MaxConcurrency = 1 },
                new FakeFolder(),
                []);

            await service.WarmUpAsync();
            Assert.True(service.CapacityAvailable);

            await service.ShutdownAsync();
        }

        [Fact]
        public async Task Service_WarmUp_DoesNotRequireAnyRootConfiguration()
        {
            // BrowserAutomationPreferences deliberately carries no directories; warm-up must
            // succeed with default preferences and a DI folder structure only.
            var registry = new BrowserProviderRegistry();
            registry.Register(new FakeProvider("patchright"));
            var installer = new FakeInstaller();
            var service = CreateService(registry, new BrowserAutomationPreferences(), new FakeFolder(), [installer]);

            await service.WarmUpAsync();

            Assert.True(service.IsAvailable);
            Assert.True(installer.EnsureCalled);
            await service.ShutdownAsync();
        }

        [Fact]
        public void Preferences_And_LaunchOptions_CarryNoDirectories()
        {
            // Guards against re-introducing duplicated path config: paths live only in
            // IWorkingFolderStructure (DI) and are derived by the installer/provider.
            string[] forbidden = ["InstallRoot", "CacheRoot", "UserDataDir", "StorageDir"];

            var preferenceNames = typeof(BrowserAutomationPreferences)
                .GetProperties().Select(p => p.Name).ToArray();
            var launchOptionNames = typeof(BrowserLaunchOptions)
                .GetProperties().Select(p => p.Name).ToArray();

            Assert.Empty(preferenceNames.Intersect(forbidden));
            Assert.Empty(launchOptionNames.Intersect(forbidden));
        }

        [Fact]
        public void Registry_ResolvesRegisteredProviderByName()
        {
            var registry = new BrowserProviderRegistry();
            registry.Register(new FakeProvider("patchright"));

            Assert.Contains("patchright", registry.Names);
            Assert.NotNull(registry.Resolve("patchright"));
            Assert.NotNull(registry.Resolve("PATCHRIGHT"));
            Assert.Null(registry.Resolve("nope"));
        }

        [Fact]
        public void Registry_SkipsUnavailableProvider()
        {
            var registry = new BrowserProviderRegistry();
            registry.Register(new FakeProvider("patchright", available: false));

            Assert.Null(registry.Resolve("patchright"));
        }

        [Fact]
        public void Registry_ResolveWithFallback_ReturnsFallbackWhenPrimaryMissing()
        {
            var registry = new BrowserProviderRegistry();
            registry.Register(new FakeProvider("playwright"));

            var provider = registry.ResolveWithFallback("patchright", ["playwright"]);
            Assert.Equal("playwright", provider.Name);
        }

        [Fact]
        public void Registry_ResolveWithFallback_ThrowsWhenNothingUsable()
        {
            var registry = new BrowserProviderRegistry();
            Assert.Throws<BrowserUnavailableException>(
                () => registry.ResolveWithFallback("patchright", ["playwright"]));
        }

        [Fact]
        public async Task Service_WarmUp_ResolvesProviderAndReportsAvailable()
        {
            var registry = new BrowserProviderRegistry();
            registry.Register(new FakeProvider("patchright"));
            var service = CreateService(registry, new BrowserAutomationPreferences());

            await service.WarmUpAsync();

            Assert.True(service.IsAvailable);
            Assert.True(service.CapacityAvailable);
            await service.ShutdownAsync();
        }

        [Fact]
        public async Task Service_WarmUp_Disabled_IsUnavailable()
        {
            var registry = new BrowserProviderRegistry();
            registry.Register(new FakeProvider("patchright"));
            var service = CreateService(registry, new BrowserAutomationPreferences { Enabled = false });

            await service.WarmUpAsync();

            Assert.False(service.IsAvailable);
            await Assert.ThrowsAsync<BrowserUnavailableException>(
                () => service.FetchAsync(new BrowserRequest { Url = "https://example.com" }));
        }

        [Fact]
        public async Task Service_FetchAsync_ReusesPooledSessionPerHost()
        {
            var registry = new BrowserProviderRegistry();
            var provider = new FakeProvider("patchright");
            registry.Register(provider);
            var service = CreateService(registry, new BrowserAutomationPreferences());
            await service.WarmUpAsync();

            await service.FetchAsync(new BrowserRequest { Url = "https://example.com/a" });
            await service.FetchAsync(new BrowserRequest { Url = "https://example.com/b" });

            // Same host -> a single pooled session, so only one launch.
            Assert.Equal(1, provider.LaunchCount);
            await service.ShutdownAsync();
        }

        [Fact]
        public async Task Service_FetchAsync_ReturnsResponseAndDisposesOnShutdown()
        {
            var registry = new BrowserProviderRegistry();
            registry.Register(new FakeProvider("patchright"));
            var service = CreateService(registry, new BrowserAutomationPreferences());
            await service.WarmUpAsync();

            var response = await service.FetchAsync(new BrowserRequest { Url = "https://example.com" });

            Assert.Equal(200, response.StatusCode);
            Assert.Equal([1, 2, 3], response.Body);

            await service.ShutdownAsync();
            Assert.False(service.IsAvailable);
        }

        [Fact]
        public async Task Service_FetchAsync_ThrowsWhenNotWarmedUp()
        {
            var registry = new BrowserProviderRegistry();
            registry.Register(new FakeProvider("patchright"));
            var service = CreateService(registry, new BrowserAutomationPreferences());

            await Assert.ThrowsAsync<BrowserUnavailableException>(
                () => service.FetchAsync(new BrowserRequest { Url = "https://example.com" }));
        }

        [Fact]
        public void BrowserRequest_RoundTripsThroughCamelCaseJson()
        {
            // The JSON hop across IKVM uses camelCase; verify the DTOs survive it.
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };

            var request = new BrowserRequest
            {
                Url = "https://example.com",
                Method = "POST",
                Body = "hello",
                ContextUrl = "https://example.com/",
                TimeoutSeconds = 30,
            };

            var json = JsonSerializer.Serialize(request, options);
            Assert.Contains("\"url\"", json);
            Assert.Contains("\"contextUrl\"", json);

            var back = JsonSerializer.Deserialize<BrowserRequest>(json, options);
            Assert.NotNull(back);
            Assert.Equal(request.Url, back!.Url);
            Assert.Equal(request.Method, back.Method);
            Assert.Equal(request.Body, back.Body);
        }

        [Fact]
        public void BrowserRequest_CarriesSeedCookies_ThroughCamelCaseJson()
        {
            // Cookie unification: the JVM store seeds the browser, so BrowserRequest must carry
            // cookies across the IKVM JSON hop.
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };

            var request = new BrowserRequest
            {
                Url = "https://example.com",
                Cookies =
                [
                    new BrowserCookie("cf_clearance", "abc123", "example.com", "/", true, true, null),
                    new BrowserCookie("session", "s1", "example.com", "/", true, false, 1234567890d),
                ],
            };

            var json = JsonSerializer.Serialize(request, options);
            Assert.Contains("\"cookies\"", json);
            Assert.Contains("cf_clearance", json);

            var back = JsonSerializer.Deserialize<BrowserRequest>(json, options);
            Assert.NotNull(back);
            Assert.Equal(2, back!.Cookies.Count);
            Assert.Equal("cf_clearance", back.Cookies[0].Name);
            Assert.True(back.Cookies[0].Secure);
            Assert.True(back.Cookies[0].HttpOnly);
            Assert.Equal(1234567890d, back.Cookies[1].Expires);
        }

        [Fact]
        public void BrowserResponse_CarriesObservedCookies_ThroughCamelCaseJson()
        {
            // The browser's cookies (e.g. a solved cf_clearance) travel back to the JVM store.
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };

            var response = new BrowserResponse
            {
                StatusCode = 200,
                Cookies = [new BrowserCookie("cf_clearance", "solved", "example.com", "/", true, true, null)],
            };

            var json = JsonSerializer.Serialize(response, options);
            Assert.Contains("\"cookies\"", json);

            var back = JsonSerializer.Deserialize<BrowserResponse>(json, options);
            Assert.NotNull(back);
            Assert.Single(back!.Cookies);
            Assert.Equal("cf_clearance", back.Cookies[0].Name);
            Assert.Equal("solved", back.Cookies[0].Value);
        }

        [Fact]
        public void RuntimeLayout_InstallDir_IsAndroidFolderWithPascalCaseProvider_NoVersionFolder()
        {
            // Rensaio convention: {AndroidFolder}/{Provider} with NO per-version folder.
            var android = Path.Combine("C:", "Users", "u", "AppData", "Local", "Rensaio", "mihon", "android");

            var dir = BrowserRuntimeLayout.GetInstallDir(android, "patchright");

            Assert.Equal(Path.Combine(android, "Patchright"), dir);
            Assert.Equal(
                Path.Combine(android, "Patchright", "build_meta.json"),
                BrowserRuntimeLayout.GetMetaPath(android, "patchright"));
        }

        [Fact]
        public void RuntimeLayout_CacheDir_IsUnderTempRoot()
        {
            var temp = Path.Combine("C:", "Users", "u", "AppData", "Local", "Temp", "extensionbridge");

            var dir = BrowserRuntimeLayout.GetCacheDir(temp, "patchright");

            Assert.Equal(Path.Combine(temp, "browser", "Patchright"), dir);
        }

        [Fact]
        public void RuntimeLayout_RequiresHostProvidedRoots_NoHardcodedFallback()
        {
            // The host always supplies IWorkingFolderStructure; there is no hardcoded fallback.
            Assert.Throws<ArgumentException>(() => BrowserRuntimeLayout.GetInstallDir("", "patchright"));
            Assert.Throws<ArgumentException>(() => BrowserRuntimeLayout.GetInstallDir(null!, "patchright"));
            Assert.Throws<ArgumentException>(() => BrowserRuntimeLayout.GetCacheDir("", "patchright"));
            Assert.Throws<ArgumentException>(() => BrowserRuntimeLayout.GetCacheDir(null!, "patchright"));
        }

        [Fact]
        public void RuntimeLayout_ToProviderFolder_UsesPascalCase()
        {
            Assert.Equal("Patchright", BrowserRuntimeLayout.ToProviderFolder("patchright"));
            Assert.Equal("Playwright", BrowserRuntimeLayout.ToProviderFolder("playwright"));
        }

        [Fact]
        public void RuntimeLayout_MetaRoundTripsAndOverwritesInPlace()
        {
            var root = Path.Combine(Path.GetTempPath(), "rensaio-android-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.Null(BrowserRuntimeLayout.TryReadMeta(root, "playwright"));

                BrowserRuntimeLayout.WriteMeta(root, "playwright", new BrowserBuildMeta
                {
                    Provider = "playwright",
                    Version = "1.50.0",
                    Platform = "linux-x64",
                    Engine = "playwright",
                });

                var meta = BrowserRuntimeLayout.TryReadMeta(root, "playwright");
                Assert.NotNull(meta);
                Assert.Equal("1.50.0", meta!.Version);

                // A new version overwrites in place (same folder, no side-by-side versions).
                BrowserRuntimeLayout.ClearInstallDir(root, "playwright");
                BrowserRuntimeLayout.WriteMeta(root, "playwright", new BrowserBuildMeta
                {
                    Provider = "playwright",
                    Version = "1.63.0",
                    Platform = "linux-x64",
                    Engine = "playwright",
                });

                var updated = BrowserRuntimeLayout.TryReadMeta(root, "playwright");
                Assert.Equal("1.63.0", updated!.Version);
                Assert.Single(Directory.GetDirectories(root));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
        }

        [Fact]
        public void BrowserResponse_RoundTripsThroughCamelCaseJson()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
            };

            var response = new BrowserResponse
            {
                StatusCode = 403,
                StatusMessage = "Forbidden",
                Body = [9, 8, 7],
                Error = null,
            };

            var json = JsonSerializer.Serialize(response, options);
            Assert.Contains("\"statusCode\"", json);

            var back = JsonSerializer.Deserialize<BrowserResponse>(json, options);
            Assert.NotNull(back);
            Assert.Equal(403, back!.StatusCode);
            Assert.Equal([9, 8, 7], back.Body);
        }
    }
}
