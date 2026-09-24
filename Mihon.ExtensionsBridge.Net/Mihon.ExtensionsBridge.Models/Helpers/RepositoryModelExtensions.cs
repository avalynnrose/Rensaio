using System;
using System.IO;

namespace Mihon.ExtensionsBridge.Models.Helpers;

/// <summary>
/// Pure, IKVM-free helpers over the shared model types.
///
/// These mirror the equivalents that historically lived in
/// <c>Mihon.ExtensionsBridge.Core.Extensions</c> (RepositoriesExtensions /
/// MiscExtensions) but with no java/android/ikvm dependency. They exist so the
/// backend can access repository metadata (active entry, version folders, apk
/// and icon URLs) after the Core reference is removed, without carrying the
/// whole extension runtime.
///
/// Kept in a distinct namespace to avoid ambiguity with the Core extensions
/// while both assemblies are still referenced during the migration.
/// </summary>
public static class RepositoryModelExtensions
{
    /// <summary>Returns the active entry of a repository group, validating the index.</summary>
    public static RepositoryEntry GetActiveEntry(this RepositoryGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.Entries == null || group.Entries.Count == 0)
            throw new ArgumentException("RepositoryGroup must have valid Entries", nameof(group));
        if (group.ActiveEntry < 0 || group.ActiveEntry >= group.Entries.Count)
            throw new ArgumentOutOfRangeException(nameof(group), "ActiveEntry index is out of range");
        return group.Entries[group.ActiveEntry];
    }

    /// <summary>Relative folder used to store one extension version on disk.</summary>
    public static string GetRelativeVersionFolder(this RepositoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.RepositoryId))
            throw new ArgumentException("RepositoryEntry must have a valid RepositoryId", nameof(entry));
        if (string.IsNullOrWhiteSpace(entry.Extension.Version))
            throw new ArgumentException("RepositoryEntry must have a valid Extension.Version", nameof(entry));
        return Path.Combine(entry.Extension.GetName(), entry.Extension.Version + "_" + entry.RepositoryId);
    }

    /// <summary>Derives the extension folder name from its APK file name.</summary>
    public static string GetName(this TachiyomiExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        string apk = extension.GetApkFilename();
        string expectedSuffix = $"-v{extension.Version}.apk";
        if (apk.EndsWith(expectedSuffix, StringComparison.Ordinal))
            return apk[..^expectedSuffix.Length];

        // Fallback: strip only the trailing extension/version marker.
        string nameWithoutExt = Path.GetFileNameWithoutExtension(apk);
        int idx = nameWithoutExt.LastIndexOf("-v", StringComparison.Ordinal);
        return idx > 0 ? nameWithoutExt[..idx] : nameWithoutExt;
    }

    /// <summary>
    /// Display name for an extension, stripping the legacy "Tachiyomi:" prefix.
    /// Mirrors the Core helper of the same name; purely string-based.
    /// </summary>
    public static string ParsedName(this TachiyomiExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return extension.Name.StartsWith("Tachiyomi:", StringComparison.Ordinal)
            ? extension.Name[10..].Trim()
            : extension.Name;
    }

    /// <summary>Extracts the APK file name from the extension's APK location.</summary>
    public static string GetApkFilename(this TachiyomiExtension ext)
    {
        ArgumentNullException.ThrowIfNull(ext);
        if (string.IsNullOrWhiteSpace(ext.Apk))
            return string.Empty;

        string path = ext.Apk;
        int cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
            path = path[..cut];
        int slash = path.LastIndexOf('/');
        string segment = slash >= 0 ? path[(slash + 1)..] : path;
        if (string.IsNullOrWhiteSpace(segment))
            return string.Empty;
        return Uri.UnescapeDataString(segment);
    }

    /// <summary>Absolute APK URL for an extension within a repository.</summary>
    public static string GetApkUrl(this TachiyomiExtension ext, TachiyomiRepository repository)
    {
        ArgumentNullException.ThrowIfNull(ext);
        ArgumentNullException.ThrowIfNull(repository);
        if (ext.Apk.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return ext.Apk;
        return $"{RepoFromUrl(repository.Url)}/apk/{ext.Apk}";
    }

    /// <summary>Absolute icon URL for an extension within a repository.</summary>
    public static string GetIconUrl(this TachiyomiExtension ext, TachiyomiRepository repository)
    {
        ArgumentNullException.ThrowIfNull(ext);
        ArgumentNullException.ThrowIfNull(repository);
        if (ext.Icon != null && ext.Icon.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return ext.Icon;
        string iconName = ext.Package + "." + "png";
        return $"{RepoFromUrl(repository.Url)}/icon/{iconName}";
    }

    /// <summary>Normalizes a repository URL by stripping index.json / trailing slash.</summary>
    public static string RepoFromUrl(string repo)
    {
        if (string.IsNullOrEmpty(repo))
            return repo;

        if (repo.EndsWith("index.json", StringComparison.InvariantCultureIgnoreCase))
            repo = repo[..^10];
        if (repo.EndsWith("index.min.json", StringComparison.InvariantCultureIgnoreCase))
            repo = repo[..^14];
        if (repo.EndsWith('/'))
            repo = repo[..^1];
        return repo;
    }

    /// <summary>Appends path segments to a base URL.</summary>
    public static string CombineUrl(this string repoUrl, params string[] segments)
    {
        if (string.IsNullOrEmpty(repoUrl))
            return string.Empty;

        string trimmed = repoUrl.TrimEnd('/');
        foreach (string seg in segments)
            trimmed += "/" + seg.Trim('/');
        return trimmed;
    }
}
