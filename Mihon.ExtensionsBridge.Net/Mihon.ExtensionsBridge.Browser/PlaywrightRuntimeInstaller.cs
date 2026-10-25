using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Abstractions.Browser;

namespace Mihon.ExtensionsBridge.Browser
{
    /// <summary>
    /// Installs and locates the Playwright/Patchright runtime following the Rensaio config
    /// convention (plan §5.4). Every path comes from the injected
    /// <see cref="IWorkingFolderStructure"/> — the single source of truth:
    ///
    /// <code>
    ///   install: {AndroidFolder}/{Provider}/     (e.g. .../Rensaio/mihon/android/Patchright)
    ///   cache:   {TempFolder}/browser/{Provider}/
    /// </code>
    ///
    /// There is no per-version folder: a new version overwrites the existing provider folder in
    /// place. The driver ships inside the NuGet package, so installation points the engine at the
    /// install dir and downloads the browser binary there via the bundled <c>Program.Main</c>
    /// entry point (<c>install <browser></c>).
    /// </summary>
    public class PlaywrightBaseRuntimeInstaller : IBrowserRuntimeInstaller
    {
        private readonly ILogger _logger;
        private readonly IWorkingFolderStructure _folders;
        private readonly string _provider;
        private readonly string _engine;

        public PlaywrightBaseRuntimeInstaller(
            ILogger logger,
            IWorkingFolderStructure folders,
            string provider,
            string engine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _folders = folders ?? throw new ArgumentNullException(nameof(folders));
            _provider = provider;
            _engine = engine;
        }

        /// <inheritdoc />
        public string ProviderName => _provider;

        /// <inheritdoc />
        public virtual string RuntimeVersion => ResolveVersion();

        /// <inheritdoc />
        public string ResolveInstallDir()
        {
            var dir = BrowserRuntimeLayout.GetInstallDir(_folders.AndroidFolder, _provider);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Cache directory for transient browser data (under the host temp folder).</summary>
        public string ResolveCacheDir()
        {
            var dir = BrowserRuntimeLayout.GetCacheDir(_folders.TempFolder, _provider);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <inheritdoc />
        public async Task<bool> EnsureInstalledAsync(bool force = false, CancellationToken ct = default)
        {
            var root = _folders.AndroidFolder;
            var dir = ResolveInstallDir();
            var meta = BrowserRuntimeLayout.TryReadMeta(root, _provider);
            var currentVersion = RuntimeVersion;

            var sameVersion = meta is not null && meta.Version == currentVersion;
            if (!force && sameVersion)
            {
                _logger.LogDebug(
                    "Browser runtime '{Provider}' {Version} already installed at {Dir}.",
                    _provider,
                    currentVersion,
                    dir);
                return true;
            }

            // No side-by-side versions: clear the provider folder and overwrite in place.
            if (meta is not null && !sameVersion)
            {
                _logger.LogInformation(
                    "Browser runtime '{Provider}' version changed {Old} -> {New}; overwriting {Dir}.",
                    _provider,
                    meta.Version,
                    currentVersion,
                    dir);
                BrowserRuntimeLayout.ClearInstallDir(root, _provider);
            }
            else if (force)
            {
                _logger.LogInformation("Browser runtime '{Provider}' reinstall forced; clearing {Dir}.", _provider, dir);
                BrowserRuntimeLayout.ClearInstallDir(root, _provider);
            }

            // MUST run before invoking the installer: PLAYWRIGHT_BROWSERS_PATH is what tells the
            // engine where to put the browser build. Without it the CLI falls back to its default
            // (%USERPROFILE%\AppData\Local\ms-playwright), which is why the download landed outside
            // the Android folder.
            ApplyLaunchEnvironment();

            _logger.LogInformation(
                "Installing browser runtime '{Provider}' {Version} into {Dir}...",
                _provider,
                currentVersion,
                dir);

            var installed = await InstallBrowsersAsync(dir, ct).ConfigureAwait(false);

            // Only record the version once the browser build is actually present. Writing the
            // marker on a failed install would make the NEXT startup believe the runtime is ready
            // (meta.Version == RuntimeVersion -> early return at the top) and silently never retry.
            if (installed)
            {
                BrowserRuntimeLayout.WriteMeta(root, _provider, new BrowserBuildMeta
                {
                    Provider = _provider,
                    Version = currentVersion,
                    Platform = ResolvePlatform(),
                    Engine = _engine,
                    InstalledUtc = DateTime.UtcNow,
                });
            }
            else
            {
                _logger.LogWarning(
                    "Browser runtime '{Provider}' {Version} was not fully installed at {Dir}; " +
                    "it will be retried on the next startup (or lazily on first launch).",
                    _provider,
                    currentVersion,
                    dir);
            }

            return installed;
        }

        /// <inheritdoc />
        public void ApplyLaunchEnvironment()
        {
            var dir = ResolveInstallDir();
            var cacheDir = ResolveCacheDir();

            // Playwright resolves its bundled driver from this env var; point it at the driver that
            // ships beside this assembly (falling back to the install dir).
            var baseDir = AppContext.BaseDirectory;
            var driverDir = File.Exists(Path.Combine(baseDir, "Microsoft.Playwright.dll"))
                ? baseDir
                : dir;
            Environment.SetEnvironmentVariable("PLAYWRIGHT_DRIVER_SEARCH_PATH", driverDir);

            // Persistent browser binaries live in the Android folder (overwritten in place)...
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", dir);

            // ...while transient cache goes to the temp folder.
            Environment.SetEnvironmentVariable("PLAYWRIGHT_TEMP_DIR", cacheDir);
            Environment.SetEnvironmentVariable("TMPDIR", cacheDir);
        }

        private async Task<bool> InstallBrowsersAsync(string dir, CancellationToken ct)
        {
            try
            {
                var assembly = typeof(Microsoft.Playwright.Playwright).Assembly;
                var programType = assembly.GetType("Microsoft.Playwright.Program");
                var main = programType?.GetMethod(
                    "Main",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string[]) },
                    null);

                if (main is null)
                {
                    // No in-process entry point: the driver will install lazily on first launch.
                    _logger.LogDebug("Playwright Program.Main not found; browsers will install on first use.");
                    return false;
                }

                var args = new List<string> { "install" };
                args.AddRange(ResolveBrowserNames());

                var exitCode = await Task.Run(
                    () => main.Invoke(null, new object[] { args.ToArray() }),
                    ct).ConfigureAwait(false);

                if (exitCode is int code && code != 0)
                {
                    _logger.LogWarning(
                        "Playwright browser install returned exit code {Code}; browsers may install on first use.",
                        code);
                    return false;
                }

                // The CLI exits 0 even when it wrote the browser somewhere else (its default
                // %USERPROFILE%\AppData\Local\ms-playwright) — which happens whenever
                // PLAYWRIGHT_BROWSERS_PATH was not in effect. Verify the payload actually landed in
                // our directory so a mis-location is reported instead of silently "succeeding".
                var fileCount = Directory.Exists(dir)
                    ? Directory.EnumerateFileSystemEntries(dir)
                        .Count(p => !string.Equals(Path.GetFileName(p), BrowserRuntimeLayout.MetaFileName, StringComparison.OrdinalIgnoreCase))
                    : 0;

                if (fileCount == 0)
                {
                    _logger.LogError(
                        "Playwright reported a successful install but nothing was written to {Dir}. " +
                        "The browser was most likely installed to the engine's default location " +
                        "(PLAYWRIGHT_BROWSERS_PATH={BrowsersPath} was not honoured).",
                        dir,
                        Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") ?? "<unset>");
                    return false;
                }

                _logger.LogInformation(
                    "Browser runtime '{Provider}' {Version} installed at {Dir} ({Count} entries).",
                    _provider,
                    RuntimeVersion,
                    dir,
                    fileCount);
                return true;
            }
            catch (Exception ex)
            {
                // Best-effort: a failure here must not break startup. The engine will attempt a
                // lazy install on first launch, and a failure there degrades to the direct chain.
                _logger.LogWarning(ex, "Automatic browser install failed; browsers may install on first use.");
                return false;
            }
        }

        private static string[] ResolveBrowserNames() => ["chromium"];

        /// <summary>
        /// Resolves the engine version for the install marker.
        ///
        /// Order matters: AssemblyInformationalVersion is what the NuGet package sets, but it is
        /// absent on some builds — falling straight through to AssemblyVersion then yields a bare
        /// "1.0.0", which never distinguishes releases and so silently defeats the
        /// version-change/overwrite logic. FileVersion and the package version are tried before
        /// giving up.
        /// </summary>
        private static string ResolveVersion()
        {
            var assembly = typeof(Microsoft.Playwright.Playwright).Assembly;

            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            var stripped = Strip(informational);
            if (!string.IsNullOrWhiteSpace(stripped) && stripped != "1.0.0")
            {
                return stripped!;
            }

            var fileVersion = Strip(assembly
                .GetCustomAttribute<AssemblyFileVersionAttribute>()
                ?.Version);
            if (!string.IsNullOrWhiteSpace(fileVersion) && fileVersion != "1.0.0")
            {
                return fileVersion!;
            }

            var packageVersion = Strip(assembly
                .GetCustomAttribute<AssemblyMetadataAttribute>()
                ?.Value is { Length: > 0 } metadata
                ? metadata
                : null);

            if (!string.IsNullOrWhiteSpace(packageVersion) && packageVersion != "1.0.0")
            {
                return packageVersion!;
            }

            // Last resort: the assembly version. Known to be a poor signal ("1.0.0"); the caller
            // logs it so a meaningless marker is at least visible.
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }

        /// <summary>Strips build metadata (e.g. "1.63.0+abc" -> "1.63.0") and trims.</summary>
        private static string? Strip(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            var plus = version!.IndexOf('+');
            return (plus >= 0 ? version[..plus] : version).Trim();
        }

        private static string ResolvePlatform()
        {
            var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
                : "linux";

            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                Architecture.X86 => "x86",
                _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            };

            return $"{os}-{arch}";
        }
    }
}
