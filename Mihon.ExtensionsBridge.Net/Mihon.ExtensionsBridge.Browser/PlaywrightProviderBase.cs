using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Abstractions.Browser;

namespace Mihon.ExtensionsBridge.Browser
{
    /// <summary>
    /// Shared implementation for Playwright-compatible engines. Patchright ships a drop-in
    /// <c>Microsoft.Playwright.dll</c>, so both the Patchright and the vanilla Playwright provider
    /// projects compile this same source against their respective package. Only the engine
    /// identity (<see cref="Name"/>, <see cref="Capabilities"/>) differs between the two.
    ///
    /// The provider owns the Playwright driver handle; sessions own their browser/context.
    /// </summary>
    public abstract class PlaywrightProviderBase : IBrowserAutomationProvider, IBrowserInstallRootAware
    {
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private IPlaywright? _playwright;

        protected PlaywrightProviderBase(
            ILogger logger,
            bool stealth,
            PlaywrightBaseRuntimeInstaller installer,
            IWorkingFolderStructure folderStructure)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Stealth = stealth;
            Installer = installer ?? throw new ArgumentNullException(nameof(installer));
            FolderStructure = folderStructure ?? throw new ArgumentNullException(nameof(folderStructure));
        }

        /// <summary>Runtime installer/locator for this engine (plan §5.4).</summary>
        protected PlaywrightBaseRuntimeInstaller Installer { get; }

        /// <summary>
        /// The host folder structure (DI). Single source of truth for every path: install under
        /// <see cref="IWorkingFolderStructure.AndroidFolder"/>, profile/cache under
        /// <see cref="IWorkingFolderStructure.TempFolder"/>.
        /// </summary>
        public IWorkingFolderStructure FolderStructure { get; set; }

        /// <summary>True when the engine applies anti-detect patches (Patchright).</summary>
        protected bool Stealth { get; }

        /// <inheritdoc />
        public abstract string Name { get; }

        /// <inheritdoc />
        public abstract BrowserProviderCapabilities Capabilities { get; }

        /// <inheritdoc />
        /// <remarks>
        /// Deliberately CHEAP and side-effect free. This is called during registry resolution (via
        /// the fallback chain), i.e. before the install/cache roots have necessarily been applied,
        /// and it must not create the driver process. Creating the driver here previously ran
        /// <see cref="PlaywrightRuntimeInstaller.ApplyLaunchEnvironment"/> with null roots, which
        /// threw, was swallowed as "unavailable", and caused warm-up to degrade without installing.
        /// </remarks>
        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            try
            {
                // The driver (Microsoft.Playwright.dll + its bundled node) ships inside the NuGet
                // package and is deployed beside this assembly. Its presence is the honest
                // availability signal; the browser binary is resolved lazily at launch.
                var driverPresent = File.Exists(
                    Path.Combine(AppContext.BaseDirectory, "Microsoft.Playwright.dll"));

                if (!driverPresent)
                {
                    _logger.LogDebug(
                        "Browser provider '{Provider}' driver not found beside {Dir}.",
                        Name,
                        AppContext.BaseDirectory);
                }

                return Task.FromResult(driverPresent);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Browser provider '{Provider}' availability probe failed.", Name);
                return Task.FromResult(false);
            }
        }

        /// <inheritdoc />
        public async Task<IBrowserSession> LaunchAsync(
            BrowserLaunchOptions options,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(options);

            var playwright = await EnsurePlaywrightAsync(ct).ConfigureAwait(false)
                ?? throw new BrowserUnavailableException($"Playwright driver unavailable for '{Name}'.");

            var browserType = playwright.Chromium;

            // Proxy CA trust is applied as launch args (NSS import / SPKI pin / opt-in ignore) AND,
            // for the persistent path, as IgnoreHTTPSErrors on the context. Resolved ONCE per launch
            // so the CA is fetched/downloaded at most once for both branches.
            var trustArgs = await BrowserTrust
                .PrepareAsync(options, _logger, ResolveTrustCacheDir(), ct)
                .ConfigureAwait(false);

            if (options.Persistent)
            {
                var userDataDir = ResolveUserDataDir();
                Directory.CreateDirectory(userDataDir);

                var persistentOptions = BuildPersistentOptions(options);
                persistentOptions.IgnoreHTTPSErrors = options.IgnoreTlsErrors;

                var context = await browserType.LaunchPersistentContextAsync(
                    userDataDir,
                    persistentOptions).ConfigureAwait(false);

                _logger.LogInformation(
                    "Launched persistent '{Provider}' context (headless={Headless}, dir={Dir}).",
                    Name,
                    options.Headless,
                    userDataDir);

                return new PlaywrightBrowserSession(context, browser: null, Stealth, _logger);
            }

            var launchOptions = BuildLaunchOptions(options);
            launchOptions.Args = [.. (launchOptions.Args ?? []), .. trustArgs];

            var browser = await browserType.LaunchAsync(launchOptions).ConfigureAwait(false);
            try
            {
                var contextOptions = BuildContextOptions(options);
                contextOptions.IgnoreHTTPSErrors = options.IgnoreTlsErrors;
                var context = await browser.NewContextAsync(contextOptions).ConfigureAwait(false);
                _logger.LogInformation(
                    "Launched '{Provider}' context (headless={Headless}).",
                    Name,
                    options.Headless);
                return new PlaywrightBrowserSession(context, browser, Stealth, _logger);
            }
            catch
            {
                await browser.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>
        /// Persistent-profile directory. Derived from the injected
        /// <see cref="IWorkingFolderStructure.TempFolder"/> (browser profiles are transient state),
        /// never from a hardcoded temp path.
        /// </summary>
        private string ResolveUserDataDir()
        {
            var root = Path.Combine(
                FolderStructure.TempFolder,
                BrowserRuntimeLayout.CacheFolderName,
                BrowserRuntimeLayout.ToProviderFolder(Name),
                "profiles");
            Directory.CreateDirectory(root);

            return Path.Combine(root, "profile-" + Guid.NewGuid().ToString("N"));
        }

        /// <summary>
        /// Directory for the downloaded proxy CA. Browser profiles/cache already live under
        /// <c>TempFolder/browser/{Provider}</c>, so the CA is cached alongside them.
        /// </summary>
        private string ResolveTrustCacheDir()
        {
            var dir = Path.Combine(
                FolderStructure.TempFolder,
                BrowserRuntimeLayout.CacheFolderName,
                BrowserRuntimeLayout.ToProviderFolder(Name));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static Proxy BuildProxy(BrowserLaunchOptions options) => new()
        {
            Server = options.ProxyUrl!,
            Username = string.IsNullOrWhiteSpace(options.ProxyUsername) ? null : options.ProxyUsername,
            Password = string.IsNullOrWhiteSpace(options.ProxyPassword) ? null : options.ProxyPassword,
        };

        private BrowserTypeLaunchPersistentContextOptions BuildPersistentOptions(BrowserLaunchOptions options)
        {
            var opts = new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = options.Headless,
                // See BuildLaunchOptions: the Chromium sandbox is unusable in Docker.
                ChromiumSandbox = false,
            };

            if (!string.IsNullOrWhiteSpace(options.Channel))
            {
                opts.Channel = options.Channel;
            }

            if (!string.IsNullOrWhiteSpace(options.ProxyUrl))
            {
                opts.Proxy = BuildProxy(options);
            }

            // Stealth engines must not receive custom UA/headers (that would defeat the patches).
            if (!Stealth && !string.IsNullOrWhiteSpace(options.UserAgent))
            {
                opts.UserAgent = options.UserAgent;
            }

            return opts;
        }

        private BrowserTypeLaunchOptions BuildLaunchOptions(BrowserLaunchOptions options)
        {
            var opts = new BrowserTypeLaunchOptions
            {
                Headless = options.Headless,
                // Required inside containers (Docker): the setuid sandbox cannot be used unless the
                // user namespace / privileges are configured, and Chromium aborts on startup
                // otherwise. Harmless on Windows/macOS, so it is applied unconditionally.
                ChromiumSandbox = false,
            };

            if (!string.IsNullOrWhiteSpace(options.Channel))
            {
                opts.Channel = options.Channel;
            }

            if (!string.IsNullOrWhiteSpace(options.ProxyUrl))
            {
                opts.Proxy = BuildProxy(options);
            }

            return opts;
        }

        private BrowserNewContextOptions BuildContextOptions(BrowserLaunchOptions options)
        {
            var opts = new BrowserNewContextOptions();

            if (!Stealth && !string.IsNullOrWhiteSpace(options.UserAgent))
            {
                opts.UserAgent = options.UserAgent;
            }

            // Storage state (if any) is persisted by the engine inside the browser profile under
            // TempFolder; no directory is configured here.
            return opts;
        }

        private async Task<IPlaywright?> EnsurePlaywrightAsync(CancellationToken ct)
        {
            if (_playwright is not null)
            {
                return _playwright;
            }

            await _initLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_playwright is null)
                {
                    // Point the engine at the install dir (Android folder) and cache dir (temp)
                    // BEFORE creating the driver (plan §5.4). Paths come from the injected
                    // IWorkingFolderStructure, never from callers or preferences.
                    Installer.ApplyLaunchEnvironment();
                    _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
                }
                return _playwright;
            }
            finally
            {
                _initLock.Release();
            }
        }
    }
}
