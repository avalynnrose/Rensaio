namespace Mihon.ExtensionsBridge.Models.Abstractions.Browser
{
    /// <summary>
    /// Implemented by providers whose runtime/browser data resolves from the host-supplied
    /// <see cref="IWorkingFolderStructure"/>. The structure is injected (DI), so there is a single
    /// source of truth for paths and no root has to be pushed in or configured.
    /// </summary>
    public interface IBrowserInstallRootAware
    {
        /// <summary>
        /// The host folder structure. Install under <see cref="IWorkingFolderStructure.AndroidFolder"/>;
        /// profile/cache under <see cref="IWorkingFolderStructure.TempFolder"/>.
        /// </summary>
        IWorkingFolderStructure FolderStructure { get; set; }
    }
}
