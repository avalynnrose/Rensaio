using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mihon.ExtensionsBridge.Models.Abstractions.Browser
{
    /// <summary>
    /// On-disk layout for browser runtimes, following the Rensaio config convention.
    ///
    /// Install (persistent, version-overwritten in place):
    /// <code>
    ///   {AndroidFolder}/{Provider}/          e.g. .../Rensaio/mihon/android/Patchright
    ///       build_meta.json
    ///       ...driver + browser files...
    /// </code>
    ///
    /// Cache (ephemeral) is kept under the temp folder:
    /// <code>
    ///   {TempFolder}/browser/{Provider}/
    /// </code>
    ///
    /// A new version is never installed side by side: the existing provider folder is cleared and
    /// overwritten in place.
    /// </summary>
    public static class BrowserRuntimeLayout
    {
        public const string MetaFileName = "build_meta.json";
        public const string CacheFolderName = "browser";

        /// <summary>Provider folder names use PascalCase (e.g. "Patchright", "Playwright").</summary>
        public static string ToProviderFolder(string provider)
        {
            if (string.IsNullOrWhiteSpace(provider))
            {
                return "Browser";
            }

            return char.ToUpperInvariant(provider[0]) + provider[1..].ToLowerInvariant();
        }

        /// <summary>
        /// Version-independent install directory (the provider folder is overwritten in place).
        /// The root is supplied by the host via <c>IWorkingFolderStructure.AndroidFolder</c>; there
        /// is no hardcoded fallback.
        /// </summary>
        public static string GetInstallDir(string installRoot, string provider)
        {
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                throw new ArgumentException(
                    "Install root (IWorkingFolderStructure.AndroidFolder) is required for browser install.",
                    nameof(installRoot));
            }

            return Path.Combine(installRoot, ToProviderFolder(provider));
        }

        /// <summary>
        /// Ephemeral cache directory under the temp root. The temp root is supplied by the host via
        /// <c>IWorkingFolderStructure.TempFolder</c>; there is no hardcoded fallback.
        /// </summary>
        public static string GetCacheDir(string tempRoot, string provider)
        {
            if (string.IsNullOrWhiteSpace(tempRoot))
            {
                throw new ArgumentException(
                    "Temp folder (IWorkingFolderStructure.TempFolder) is required for browser cache.",
                    nameof(tempRoot));
            }

            return Path.Combine(tempRoot, CacheFolderName, ToProviderFolder(provider));
        }

        /// <summary>Absolute path of the build metadata marker for a provider.</summary>
        public static string GetMetaPath(string installRoot, string provider) =>
            Path.Combine(GetInstallDir(installRoot, provider), MetaFileName);

        /// <summary>Reads <c>build_meta.json</c>, or null when absent/unreadable.</summary>
        public static BrowserBuildMeta? TryReadMeta(string installRoot, string provider)
        {
            var path = GetMetaPath(installRoot, provider);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<BrowserBuildMeta>(File.ReadAllText(path));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Writes <c>build_meta.json</c>, creating the install directory.</summary>
        public static void WriteMeta(string installRoot, string provider, BrowserBuildMeta meta)
        {
            var dir = GetInstallDir(installRoot, provider);
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(dir, MetaFileName), json);
        }

        /// <summary>
        /// Clears the provider's install directory in place (used when overwriting a new version).
        /// The directory itself is recreated.
        /// </summary>
        public static void ClearInstallDir(string installRoot, string provider)
        {
            var dir = GetInstallDir(installRoot, provider);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>Metadata written next to an installed browser runtime (jcefmaven analogue).</summary>
    public sealed class BrowserBuildMeta
    {
        [JsonPropertyName("provider")]
        public string Provider { get; set; } = "";

        [JsonPropertyName("version")]
        public string Version { get; set; } = "";

        /// <summary>OS/arch identifier (e.g. <c>linux-x64</c>, <c>win-x64</c>).</summary>
        [JsonPropertyName("platform")]
        public string Platform { get; set; } = "";

        [JsonPropertyName("installedUtc")]
        public DateTime InstalledUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Engine display name, e.g. "patchright" or "playwright".</summary>
        [JsonPropertyName("engine")]
        public string Engine { get; set; } = "";
    }
}
