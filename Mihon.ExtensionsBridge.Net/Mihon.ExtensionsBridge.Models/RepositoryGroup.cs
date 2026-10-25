namespace Mihon.ExtensionsBridge.Models;

public class RepositoryGroup
{
    public string Name { get; set; }
    public int ActiveEntry { get; set; }
    public bool AutoUpdate { get; set; } = true;

    /// <summary>
    /// True when this group was prepared from an online repository for a sweep and is NOT an
    /// installed extension. Temporary groups share the same registry/persistence as installed
    /// ones but are hidden from the default installed listing. Installing a temporary group is
    /// a flag flip to <c>false</c>; uninstalling removes it.
    /// </summary>
    public bool Temporary { get; set; }

    public List<RepositoryEntry> Entries { get; set; } = [];
}

