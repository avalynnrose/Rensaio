using System;
using System.IO;
using Mihon.ExtensionsBridge.Models.Abstractions;

namespace Mihon.ExtensionsBridge.Models.Helpers;

/// <summary>
/// Pure, IKVM-free folder helpers over the extension working folder.
///
/// Mirrors the path-only parts of <c>Mihon.ExtensionsBridge.Core.Extensions.WorkingFolderStructureExtensions</c>
/// so the backend can resolve extension folders after the Core reference is
/// removed. Verify-extension/manifest and temporary-directory helpers stay in
/// Core because they depend on the runtime.
/// </summary>
public static class FolderModelExtensions
{
    /// <summary>Folder that holds every version of one extension.</summary>
    public static string GetExtensionFolder(this IWorkingFolderStructure folder, TachiyomiExtension extension)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(extension);
        return Path.Combine(folder.ExtensionsFolder, extension.GetName());
    }

    /// <summary>Folder that holds one specific extension version's artifacts.</summary>
    public static string GetExtensionVersionFolder(this IWorkingFolderStructure folder, RepositoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.RepositoryId))
            throw new ArgumentException("RepositoryEntry must have a valid RepositoryId", nameof(entry));
        if (string.IsNullOrWhiteSpace(entry.Extension.Version))
            throw new ArgumentException("RepositoryEntry must have a valid Extension.Version", nameof(entry));

        string folderName = Path.Combine(folder.ExtensionsFolder, entry.Extension.GetName());
        return Path.Combine(folderName, entry.Extension.Version + "_" + entry.RepositoryId);
    }

    /// <summary>Folder of the active version of a repository group.</summary>
    public static string GetActiveExtensionVersionFolder(this IWorkingFolderStructure folder, RepositoryGroup group)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(group);
        return folder.GetExtensionVersionFolder(group.GetActiveEntry());
    }
}
