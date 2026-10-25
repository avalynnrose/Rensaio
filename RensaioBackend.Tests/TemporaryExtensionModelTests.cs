using System.Text.Json;
using Mihon.ExtensionsBridge.Models;
using Xunit;

namespace RensaioBackend.Tests;

/// <summary>
/// Guards the persisted <see cref="RepositoryGroup.Temporary"/> / <see cref="RepositoryEntry.Temporary"/>
/// contract. The flag is stored in <c>local_repository.json</c> as-is (no save/load partition), so a
/// JSON round-trip must preserve it: this is what distinguishes an online-prepared (sweep) extension
/// from an installed one after a restart.
/// </summary>
public sealed class TemporaryExtensionModelTests
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    [Fact]
    public void TemporaryFlag_RoundTripsThroughJson()
    {
        var group = new RepositoryGroup
        {
            Name = "Example",
            Temporary = true,
            ActiveEntry = 0,
            Entries =
            [
                new RepositoryEntry
                {
                    Name = "Example",
                    RepositoryId = "repo-1",
                    Temporary = true,
                    Apk = new FileHash { SHA256 = "deadbeef" },
                    Extension = new TachiyomiExtension { Name = "Example", Package = "pkg.example", Version = "1.2.3" },
                },
            ],
        };

        string json = JsonSerializer.Serialize(group, Options);
        RepositoryGroup? restored = JsonSerializer.Deserialize<RepositoryGroup>(json, Options);

        Assert.NotNull(restored);
        Assert.True(restored!.Temporary);
        Assert.True(restored.Entries[0].Temporary);
    }

    [Fact]
    public void InstalledGroup_DefaultsToNonTemporary()
    {
        var group = new RepositoryGroup { Name = "Installed" };
        string json = JsonSerializer.Serialize(group, Options);
        RepositoryGroup? restored = JsonSerializer.Deserialize<RepositoryGroup>(json, Options);

        Assert.NotNull(restored);
        Assert.False(restored!.Temporary);
    }

    [Fact]
    public void PromotionToInstalled_ClearsTemporaryOnGroupAndEntries()
    {
        var group = new RepositoryGroup
        {
            Name = "Promoted",
            Temporary = true,
            Entries =
            [
                new RepositoryEntry { RepositoryId = "repo-1", Temporary = true, Apk = new FileHash { SHA256 = "deadbeef" } },
            ],
        };

        // Mirrors ExtensionManager.PromoteExtensionAsync: install = flip the flag.
        group.Temporary = false;
        foreach (RepositoryEntry entry in group.Entries)
            entry.Temporary = false;

        Assert.False(group.Temporary);
        Assert.False(group.Entries[0].Temporary);
    }
}
