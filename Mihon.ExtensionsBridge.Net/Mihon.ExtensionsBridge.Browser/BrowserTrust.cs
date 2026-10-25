using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Mihon.ExtensionsBridge.Models.Abstractions.Browser;
using Mihon.ExtensionsBridge.Models.Helpers;

namespace Mihon.ExtensionsBridge.Browser
{
    /// <summary>
    /// Makes a TLS-terminating transparent proxy (trawl) trusted by the Chromium that Playwright
    /// launches.
    ///
    /// Chromium launched by Playwright does <b>not</b> read the JVM trust store, and a transparent
    /// proxy mints a <b>fresh per-host leaf</b> signed by its CA — so there is no stable leaf to
    /// pin. The CA is therefore the trust anchor, and this helper applies, in order of robustness:
    ///
    /// <list type="number">
    /// <item><b>Trust-store install.</b> On Linux (the Docker case) Chromium uses an NSS database;
    /// the CA is imported into the shared <c>$HOME/.pki/nssdb</c> via <c>certutil</c> so every
    /// context trusts it. On Windows/macOS Chromium reads the OS root store, which trawl's own
    /// documentation already requires the user to populate — nothing is done here.</item>
    /// <item><b>CA SPKI pin.</b> The CA's SHA-256 SPKI is passed via
    /// <c>--ignore-certificate-errors-spki-list</c>. Best-effort: whether Chromium matches this
    /// against the leaf or the CA is version-dependent, so it is layered on top of (1) rather than
    /// relied upon alone.</item>
    /// <item><b>Blanket override.</b> Only when the user explicitly sets
    /// <see cref="BrowserLaunchOptions.IgnoreTlsErrors"/>: <c>--ignore-certificate-errors</c> plus
    /// <c>IgnoreHTTPSErrors</c> on the context.</item>
    /// </list>
    /// </summary>
    internal static class BrowserTrust
    {
        /// <summary>
        /// Resolves/installs the configured proxy CA and returns extra Chromium launch arguments.
        /// Returns an empty list when no CA is configured and TLS errors are not being ignored.
        /// </summary>
        public static async Task<IReadOnlyList<string>> PrepareAsync(
            BrowserLaunchOptions options,
            ILogger logger,
            string? cacheDirectory,
            CancellationToken ct)
        {
            var args = new List<string>();

            if (!string.IsNullOrWhiteSpace(options.CaCertSource))
            {
                // Fails loudly (throws) if a configured source cannot be resolved, rather than
                // silently degrading to unverified TLS.
                var pem = await TrawlCaCert
                    .ResolveToPemFileAsync(
                        options.CaCertSource,
                        cacheDirectory,
                        log: m => logger.LogInformation("{Message}", m),
                        ct: ct)
                    .ConfigureAwait(false);

                if (pem is not null)
                {
                    TryAddSpkiPin(args, pem, logger);

                    if (OperatingSystem.IsLinux())
                    {
                        TryImportIntoLinuxNssStore(pem, logger);
                    }
                    else
                    {
                        logger.LogDebug(
                            "Proxy CA configured; on {Os} Chromium reads the OS root store, so no " +
                            "in-browser install is performed. Ensure the trawl CA is trusted system-wide.",
                            OperatingSystem.IsWindows() ? "Windows" : "macOS");
                    }
                }
            }

            if (options.IgnoreTlsErrors)
            {
                logger.LogWarning(
                    "IgnoreTlsErrors is ENABLED: the browser will accept invalid TLS certificates " +
                    "for ALL sites, not only the proxy. Prefer installing the proxy CA instead.");
                args.Add("--ignore-certificate-errors");
            }

            return args;
        }

        private static void TryAddSpkiPin(List<string> args, string pemPath, ILogger logger)
        {
            try
            {
                var hash = TrawlCaCert.ComputeSpkiHash(File.ReadAllText(pemPath));
                args.Add($"--ignore-certificate-errors-spki-list={hash}");
                logger.LogDebug("Pinned proxy CA SPKI {Hash}.", hash);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not derive the proxy CA SPKI pin; relying on trust-store install.");
            }
        }

        /// <summary>
        /// Best-effort import of the CA into the shared NSS database Chromium reads on Linux.
        /// Failure is logged, not thrown: the SPKI pin and (opt-in) ignore flag remain as fallbacks,
        /// and a missing <c>certutil</c> must not take the browser subsystem down.
        /// </summary>
        private static void TryImportIntoLinuxNssStore(string pemPath, ILogger logger)
        {
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var nssDir = Path.Combine(home, ".pki", "nssdb");
                Directory.CreateDirectory(nssDir);

                var psi = new ProcessStartInfo("certutil")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                psi.ArgumentList.Add("-A");
                psi.ArgumentList.Add("-n");
                psi.ArgumentList.Add("trawl-proxy-ca");
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add("C,,");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(pemPath);
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add("sql:" + nssDir);

                using var proc = Process.Start(psi);
                if (proc is null)
                {
                    logger.LogWarning("Could not start certutil to import the proxy CA into the NSS store.");
                    return;
                }

                proc.WaitForExit(10_000);
                if (proc.ExitCode == 0)
                {
                    logger.LogInformation("Imported proxy CA into NSS store at {Dir}.", nssDir);
                }
                else
                {
                    logger.LogWarning(
                        "certutil returned {Code} importing the proxy CA into {Dir}: {Error}",
                        proc.ExitCode,
                        nssDir,
                        proc.StandardError.ReadToEnd());
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to import the proxy CA into the NSS store (is libnss3-tools installed?). " +
                    "The browser may reject the proxy's certificates.");
            }
        }
    }
}