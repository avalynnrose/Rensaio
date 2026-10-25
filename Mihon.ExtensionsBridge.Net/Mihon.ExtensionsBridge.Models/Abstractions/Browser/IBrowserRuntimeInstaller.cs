using System.Threading;
using System.Threading.Tasks;

namespace Mihon.ExtensionsBridge.Models.Abstractions.Browser
{
    /// <summary>
    /// Installs and locates a provider's runtime (driver + browser build) on disk, mirroring the
    /// existing jcefmaven behaviour (plan §5.4).
    ///
    /// <para>
    /// All paths come from the DI <see cref="IWorkingFolderStructure"/> — the single source of
    /// truth — and are never passed in or configured:
    /// </para>
    /// <code>
    ///   install: {AndroidFolder}/{Provider}/     (e.g. .../Rensaio/mihon/android/Patchright)
    ///   cache:   {TempFolder}/browser/{Provider}/
    /// </code>
    ///
    /// There is no per-version folder: a new version <b>overwrites</b> the existing provider folder.
    /// </summary>
    public interface IBrowserRuntimeInstaller
    {
        /// <summary>Provider name this installer belongs to (e.g. "patchright").</summary>
        string ProviderName { get; }

        /// <summary>Version of the runtime being installed (e.g. the Playwright package version).</summary>
        string RuntimeVersion { get; }

        /// <summary>
        /// Resolves the install directory for this provider (creating it if necessary), derived from
        /// <see cref="IWorkingFolderStructure.AndroidFolder"/>.
        /// </summary>
        string ResolveInstallDir();

        /// <summary>
        /// Ensures the runtime is installed and ready. Idempotent: returns immediately when
        /// <c>build_meta.json</c> already records the expected version. When the version differs,
        /// the existing provider folder is cleared and re-installed <b>in place</b> (no side-by-side
        /// versions). <paramref name="force"/> forces a reinstall of the same version.
        /// </summary>
        Task<bool> EnsureInstalledAsync(bool force = false, CancellationToken ct = default);

        /// <summary>
        /// Sets the environment so the engine resolves its driver/browser from the install dir and
        /// writes transient data to the cache dir. Called before <c>Playwright.CreateAsync()</c>.
        /// </summary>
        void ApplyLaunchEnvironment();
    }
}
