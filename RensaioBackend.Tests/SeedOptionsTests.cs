using RensaioBackend.Models;
using Xunit;

namespace RensaioBackend.Tests;

/// <summary>
/// Tests for <see cref="SeedOptions"/> parsing, covering the online-sweep modes
/// (<c>--seed-all</c>) and the single/multi package forms (<c>--seed pkg[,pkg...]</c>) added for
/// the "use all online extensions" capability, plus the preserved <c>--force</c> behavior.
/// </summary>
public sealed class SeedOptionsTests
{
    [Fact]
    public void NoArgs_Disabled()
    {
        SeedOptions options = SeedOptions.Parse([]);
        Assert.False(options.Enabled);
        Assert.False(options.AllOnline);
        Assert.Empty(options.Packages);
    }

    [Fact]
    public void SeedOnly_Enabled_SweepsAllInstalled()
    {
        SeedOptions options = SeedOptions.Parse(["--seed"]);
        Assert.True(options.Enabled);
        Assert.False(options.AllOnline);
        Assert.Null(options.StartPackage);
        Assert.Empty(options.Packages);
    }

    [Fact]
    public void SeedAll_SetsAllOnline()
    {
        SeedOptions options = SeedOptions.Parse(["--seed-all"]);
        Assert.True(options.Enabled);
        Assert.True(options.AllOnline);
    }

    [Fact]
    public void SeedAll_WithForce_KeepsForce()
    {
        SeedOptions options = SeedOptions.Parse(["--force", "--seed-all"]);
        Assert.True(options.Enabled);
        Assert.True(options.AllOnline);
        Assert.True(options.Force);
        Assert.False(options.ForceAfterSeed);
    }

    [Fact]
    public void SeedAll_NoPackage_StartPackageNull()
    {
        SeedOptions options = SeedOptions.Parse(["--seed-all"]);
        Assert.True(options.Enabled);
        Assert.True(options.AllOnline);
        Assert.Null(options.StartPackage);
    }

    [Fact]
    public void SeedAllWithPackage_RecordsStartPackage()
    {
        SeedOptions options = SeedOptions.Parse(["--seed-all", "eu.kanade.tachiyomi.extension.en.mangadex"]);
        Assert.True(options.Enabled);
        Assert.True(options.AllOnline);
        Assert.Equal("eu.kanade.tachiyomi.extension.en.mangadex", options.StartPackage);
        // A start package is a scope cursor, not an explicit package list.
        Assert.Empty(options.Packages);
    }

    [Fact]
    public void SeedAllEqualsForm_RecordsStartPackage()
    {
        SeedOptions options = SeedOptions.Parse(["--seed-all=pkg.a"]);
        Assert.True(options.Enabled);
        Assert.True(options.AllOnline);
        Assert.Equal("pkg.a", options.StartPackage);
    }

    [Fact]
    public void SeedAll_SwitchAfter_IsNotConsumedAsPackage()
    {
        SeedOptions options = SeedOptions.Parse(["--seed-all", "--force"]);
        Assert.True(options.Enabled);
        Assert.True(options.AllOnline);
        Assert.True(options.Force);
        Assert.Null(options.StartPackage);
    }

    [Fact]
    public void SeedWithPackage_RecordsStartPackage()
    {
        SeedOptions options = SeedOptions.Parse(["--seed", "eu.kanade.tachiyomi.extension.en.mangadex"]);
        Assert.True(options.Enabled);
        Assert.Equal("eu.kanade.tachiyomi.extension.en.mangadex", options.StartPackage);
        Assert.Single(options.Packages);
        Assert.Contains("eu.kanade.tachiyomi.extension.en.mangadex", options.Packages);
    }

    [Fact]
    public void SeedWithCommaSeparatedPackages_RecordsAll()
    {
        SeedOptions options = SeedOptions.Parse(["--seed", "pkg.a,pkg.b, pkg.c"]);
        Assert.True(options.Enabled);
        Assert.Equal(3, options.Packages.Count);
        Assert.Contains("pkg.a", options.Packages);
        Assert.Contains("pkg.b", options.Packages);
        Assert.Contains("pkg.c", options.Packages);
    }

    [Fact]
    public void SeedEqualsForm_RecordsPackage()
    {
        SeedOptions options = SeedOptions.Parse(["--seed=pkg.a,pkg.b"]);
        Assert.True(options.Enabled);
        Assert.Equal(2, options.Packages.Count);
    }

    [Fact]
    public void ForceAfterSeed_IsFlagged()
    {
        SeedOptions options = SeedOptions.Parse(["--seed", "--force"]);
        Assert.True(options.Enabled);
        Assert.True(options.Force);
        Assert.True(options.ForceAfterSeed);
    }

    [Fact]
    public void SwitchAfterSeed_IsNotConsumedAsPackage()
    {
        SeedOptions options = SeedOptions.Parse(["--seed", "--force"]);
        Assert.Empty(options.Packages);
        Assert.Null(options.StartPackage);
    }
}
