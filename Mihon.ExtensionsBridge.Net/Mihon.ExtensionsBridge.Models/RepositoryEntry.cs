namespace Mihon.ExtensionsBridge.Models;

public class RepositoryEntry
{
    public string Id => Apk.SHA256;
    public string RepositoryId { get; set; }
    public bool IsLocal { get; set; }

    /// <summary>
    /// True when this entry belongs to a group prepared from an online repository for a sweep
    /// rather than an installed extension. Mirrors <see cref="RepositoryGroup.Temporary"/>.
    /// </summary>
    public bool Temporary { get; set; }
    public string Name { get; set; }
    public string ClassName { get; set; }
    public TachiyomiExtension Extension { get; set; }
    public string DownloadUrl { get; set; }
    public DateTimeOffset DownloadUTC { get; set; }
    public FileHash Apk { get; set; }
    public FileHashVersion Jar { get; set; }
    //public FileHashVersion Dll { get; set; }
    public FileHash Icon { get; set; }
}

