using Microsoft.Extensions.Options;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Configuration;

namespace RensaioBackend.Services.Rpc;

/// <summary>
/// Backend-side implementation of <see cref="IWorkingFolderStructure"/>.
///
/// The backend no longer hosts the extension runtime, so it only needs the
/// folder paths for display purposes (resolving extension icon paths). The RPC
/// process owns the real folder layout and the extension files themselves.
/// </summary>
public sealed class LocalWorkingFolderStructure : IWorkingFolderStructure
{
    public LocalWorkingFolderStructure(IOptions<Paths> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string bridge = paths.Value.BridgeFolder;
        if (string.IsNullOrWhiteSpace(bridge))
            throw new ArgumentException("Bridge Folder path cannot be null or whitespace.", nameof(paths));

        WorkingFolder = bridge;
        TempFolder = string.IsNullOrWhiteSpace(paths.Value.TempFolder)
            ? Path.Combine(Path.GetTempPath(), "extensionbridge")
            : paths.Value.TempFolder;
    }

    public string WorkingFolder { get; }

    /// <summary>Extensions live under the RPC-owned bridge folder.</summary>
    public string ExtensionsFolder => Path.Combine(WorkingFolder, "extensions");

    /// <summary>Android compat folder (RPC-owned; exposed for parity).</summary>
    public string AndroidFolder => Path.Combine(WorkingFolder, "android");

    public string TempFolder { get; }
}
