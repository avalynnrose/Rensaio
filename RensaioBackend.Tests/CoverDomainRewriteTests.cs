using System.Net;
using System.Net.Sockets;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Rensaio.Resilience;
using RensaioBackend.Services.Bridge;
using RensaioBackend.Services.Images;
using Xunit;

namespace RensaioBackend.Tests;

/// <summary>
/// Permanent-vs-transient transport classification shared by every provider client. A dead DNS
/// host (<c>SocketError.HostNotFound</c>, "No such host is known") must NEVER be retried, while a
/// transient network/DNS blip (refused, timed out, unreachable) must still be retried.
/// </summary>
public sealed class ProviderHttpFailuresTests
{
    [Fact]
    public void SocketException_HostNotFound_IsPermanent()
        => Assert.True(ProviderHttpFailures.IsPermanentDnsFailure(
            new HttpRequestException("No such host is known. (kagane.to:443)",
                new SocketException((int)SocketError.HostNotFound))));

    [Fact]
    public void BareSocketException_HostNotFound_IsPermanent()
        => Assert.True(ProviderHttpFailures.IsPermanentDnsFailure(
            new SocketException((int)SocketError.HostNotFound)));

    [Fact]
    public void MessageOnly_NoSuchHost_IsPermanent()
        => Assert.True(ProviderHttpFailures.IsPermanentDnsFailure(
            new HttpRequestException("No such host is known. (kagane.to:443)")));

    [Fact]
    public void NestedInner_NoSuchHost_IsPermanent()
        => Assert.True(ProviderHttpFailures.IsPermanentDnsFailure(
            new Exception("wrapper", new HttpRequestException(
                "No such host is known. (kagane.to:443)",
                new SocketException((int)SocketError.HostNotFound)))));

    [Theory]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.TimedOut)]
    [InlineData(SocketError.HostUnreachable)]
    public void TransientSocketErrors_AreNotPermanent(SocketError error)
        => Assert.False(ProviderHttpFailures.IsPermanentDnsFailure(
            new HttpRequestException("boom", new SocketException((int)error))));

    [Fact]
    public void NullOrUnrelated_IsNotPermanent()
    {
        Assert.False(ProviderHttpFailures.IsPermanentDnsFailure(null));
        Assert.False(ProviderHttpFailures.IsPermanentDnsFailure(new InvalidOperationException("nope")));
        Assert.False(ProviderHttpFailures.IsPermanentDnsFailure(
            new HttpRequestException("Server error", null, HttpStatusCode.InternalServerError)));
    }
}

/// <summary>
/// The cover-domain resolver rewrites a cover URL onto its owning source's current domains and —
/// critically — only after a permanent failure, remembering the correction in memory so later
/// covers skip the dead host. Nothing is persisted.
/// </summary>
public sealed class CoverUrlDomainResolverTests
{
    private const string DeadUrl = "https://kagane.to/api/v2/image/019c3410-0558-7430-b275-53bd966cf2bd";

    [Fact]
    public void HostOf_ReturnsHost_AndNullForRelative()
    {
        Assert.Equal("kagane.to", CoverUrlDomainResolver.HostOf(DeadUrl));
        Assert.Null(CoverUrlDomainResolver.HostOf("/api/image/abc"));
        Assert.Null(CoverUrlDomainResolver.HostOf(null));
    }

    [Fact]
    public void PackageOf_SplitsProviderId()
    {
        Assert.Equal("eu.kanade.tachiyomi.extension.all.kagane",
            CoverUrlDomainResolver.PackageOf("eu.kanade.tachiyomi.extension.all.kagane|4024736764982024684"));
        Assert.Null(CoverUrlDomainResolver.PackageOf("no-pipe"));
        Assert.Null(CoverUrlDomainResolver.PackageOf(null));
    }

    [Fact]
    public async Task BuildFallbackUrls_RewritesStaleHostToHomeUrlThenMirrors()
    {
        // Each test uses a UNIQUE package: the host-list decision is cached in-process per package.
        var (resolver, provider) = BuildResolver(
            "kagane.si", "kagane.si", "kagane.cx", "kagane.moe", "kagane.net");

        var fallbacks = await resolver.BuildFallbackUrlsAsync(DeadUrl, provider, default);

        Assert.Equal(
            new[]
            {
                "https://kagane.si/api/v2/image/019c3410-0558-7430-b275-53bd966cf2bd",
                "https://kagane.cx/api/v2/image/019c3410-0558-7430-b275-53bd966cf2bd",
                "https://kagane.moe/api/v2/image/019c3410-0558-7430-b275-53bd966cf2bd",
                "https://kagane.net/api/v2/image/019c3410-0558-7430-b275-53bd966cf2bd"
            },
            fallbacks);
    }

    [Fact]
    public async Task BuildFallbackUrls_WhenHostIsHomeUrl_SkipsItAndOffersMirrorsOnly()
    {
        var (resolver, provider) = BuildResolver("kagane.si", "kagane.si", "kagane.cx");

        var fallbacks = await resolver.BuildFallbackUrlsAsync(
            "https://kagane.si/some/cover.jpg", provider, default);

        Assert.Equal(new[] { "https://kagane.cx/some/cover.jpg" }, fallbacks);
    }

    [Fact]
    public async Task BuildFallbackUrls_ReturnsEmpty_WhenProviderUnknown()
    {
        var (resolver, _) = BuildResolver("kagane.si");

        var fallbacks = await resolver.BuildFallbackUrlsAsync(DeadUrl, null, default);
        Assert.Empty(fallbacks);

        var missingSource = await resolver.BuildFallbackUrlsAsync(
            DeadUrl, "unknown.pkg|1", default);
        Assert.Empty(missingSource);
    }

    [Fact]
    public async Task BuildFallbackUrls_ReturnsEmpty_WhenNoAlternativeDomain()
    {
        // Only the dead host is declared — nothing to rotate to.
        var (resolver, provider) = BuildResolver("kagane.to");

        var fallbacks = await resolver.BuildFallbackUrlsAsync(DeadUrl, provider, default);
        Assert.Empty(fallbacks);
    }

    [Fact]
    public async Task RememberedCorrection_IsAppliedToLaterCovers_WithoutProbingTheDeadHost()
    {
        // Unique package so the (static) in-memory cache cannot leak between tests.
        const string deadUrl = "https://dead.example/img/1.jpg";

        var (resolver, provider) = BuildResolver("dead.example", "live.example");

        // First cover: no correction known yet.
        Assert.Equal(deadUrl, resolver.ApplyKnownRewrite(deadUrl, provider));

        // After the rewrite succeeds the mapping is remembered...
        resolver.RememberLiveHost(provider, "dead.example", "live.example");

        // ...so the NEXT cover skips the dead host entirely.
        Assert.Equal("https://live.example/img/1.jpg", resolver.ApplyKnownRewrite(deadUrl, provider));
    }

    [Fact]
    public async Task RememberedCorrection_IsScopedToItsPackage()
    {
        var (resolver, _) = BuildResolver("dead.example", "live.example");
        const string url = "https://dead.example/img/2.jpg";

        resolver.RememberLiveHost("scope.pkg.a|1", "dead.example", "live.example");

        // A different package must NOT inherit another package's correction.
        Assert.Equal(url, resolver.ApplyKnownRewrite(url, "scope.pkg.b|1"));
    }

    /// <summary>
    /// Builds a resolver backed by a fake bridge declaring the given domains in order, under a
    /// UNIQUE package (the host-list decision is cached in-process per package, so tests must not
    /// share one).
    /// </summary>
    private static (CoverUrlDomainResolver Resolver, string ProviderId) BuildResolver(params string[] hosts)
    {
        string package = "test.kagane." + Guid.NewGuid().ToString("N");
        return (new CoverUrlDomainResolver(
            BuildService(package, hosts), NullLogger<CoverUrlDomainResolver>.Instance),
            package + "|4024736764982024684");
    }

    private static MihonBridgeService BuildService(string package, IReadOnlyList<string> hosts)
    {
        var source = new TachiyomiSource
        {
            Id = "4024736764982024684",
            Name = "Kagane",
            Language = "ja",
            BaseUrl = hosts.Count > 0 ? "https://" + hosts[0] : string.Empty,
            MirrorUrls = hosts.Skip(1).Select(h => "https://" + h).ToList()
        };
        var extension = new TachiyomiExtension
        {
            Name = "Kagane",
            Package = package,
            Sources = [source]
        };
        var group = new RepositoryGroup
        {
            Name = "Kagane",
            ActiveEntry = 0,
            Entries = [new RepositoryEntry { Extension = extension, RepositoryId = "test" }]
        };

        var bridge = new FakeBridgeManager(group);
        return new MihonBridgeService(
            NullLogger<MihonBridgeService>.Instance, bridge, new FakeWorkingFolderStructure());
    }
}

/// <summary>
/// <see cref="MihonBridgeService.TryGetSourceHostsAsync"/> reads the extension metadata so a cover
/// can be redirected onto the domains the package uses TODAY.
/// </summary>
public sealed class MihonBridgeSourceHostTests
{
    [Fact]
    public async Task ReturnsHomeUrlThenMirrors_DeduplicatedAndOrdered()
    {
        var service = BuildService("https://kagane.si",
            "https://kagane.si", "https://kagane.cx", "https://kagane.moe", "https://kagane.net");

        var hosts = await service.TryGetSourceHostsAsync("eu.kanade.tachiyomi.extension.all.kagane|4024736764982024684");

        Assert.NotNull(hosts);
        Assert.Equal(new[] { "kagane.si", "kagane.cx", "kagane.moe", "kagane.net" }, hosts);
    }

    [Fact]
    public async Task ReturnsNull_WhenProviderIdEmptyOrMalformed()
    {
        var service = BuildService("https://kagane.si");
        Assert.Null(await service.TryGetSourceHostsAsync(null));
        Assert.Null(await service.TryGetSourceHostsAsync(""));
        Assert.Null(await service.TryGetSourceHostsAsync("not-a-provider-id"));
    }

    [Fact]
    public async Task ReturnsNull_WhenSourceNotInstalled()
    {
        var service = BuildService("https://kagane.si");
        Assert.Null(await service.TryGetSourceHostsAsync("some.other.pkg|1"));
    }

    private static MihonBridgeService BuildService(string homeUrl, params string[] mirrors)
    {
        var source = new TachiyomiSource
        {
            Id = "4024736764982024684",
            Name = "Kagane",
            Language = "ja",
            BaseUrl = homeUrl,
            MirrorUrls = mirrors.ToList()
        };
        var extension = new TachiyomiExtension
        {
            Name = "Kagane",
            Package = "eu.kanade.tachiyomi.extension.all.kagane",
            Sources = [source]
        };
        var group = new RepositoryGroup
        {
            Name = "Kagane",
            ActiveEntry = 0,
            Entries = [new RepositoryEntry { Extension = extension, RepositoryId = "test" }]
        };
        return new MihonBridgeService(
            NullLogger<MihonBridgeService>.Instance, new FakeBridgeManager(group),
            new FakeWorkingFolderStructure());
    }
}

/// <summary>
/// Minimal bridge double: exposes the group as both the local extension manager (sync + async RPC
/// listing) and the online repository manager, so <see cref="MihonBridgeService"/> resolution works
/// without an extension runtime.
/// </summary>
internal sealed class FakeBridgeManager : IBridgeManager, IExtensionManager, IRepositoryManager, RensaioBackend.Services.Rpc.IExtensionRpcBridge
{
    private readonly List<RepositoryGroup> _groups;

    public FakeBridgeManager(params RepositoryGroup[] groups) => _groups = groups.ToList();

    public bool Initialized => true;
    public Task InitializationCompleted => Task.CompletedTask;
    public IExtensionManager LocalExtensionManager => this;
    public IRepositoryManager OnlineRepositoryManager => this;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Shutdown() { }
    public Task<Preferences> GetPreferencesAsync(CancellationToken cancellationToken) => Task.FromResult(new Preferences());
    public Task SetPreferencesAsync(Preferences prefs, CancellationToken cancellationToken) => Task.CompletedTask;

    // IExtensionManager
    public Task<RepositoryGroup?> AddExtensionAsync(TachiyomiExtension extension, bool force = false, CancellationToken token = default)
        => Task.FromResult<RepositoryGroup?>(null);
    public Task<RepositoryGroup?> AddExtensionAsync(TachiyomiRepository repository, TachiyomiExtension extension, bool force = false, CancellationToken token = default)
        => Task.FromResult<RepositoryGroup?>(null);
    public Task<RepositoryGroup?> AddExtensionAsync(byte[] apk, bool force = false, CancellationToken token = default)
        => Task.FromResult<RepositoryGroup?>(null);
    public Task<IExtensionInterop> GetInteropAsync(RepositoryGroup entry, CancellationToken token = default)
        => throw new NotSupportedException();
    public List<RepositoryGroup> ListExtensions(bool includeTemporary = false) => _groups;
    public RepositoryGroup? FindExtension(string name, bool includeTemporary = false)
        => _groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
    public Task<bool> RemoveExtensionAsync(RepositoryGroup group, CancellationToken token = default) => Task.FromResult(false);
    public Task<RepositoryGroup?> RemoveExtensionVersionAsync(RepositoryEntry entry, CancellationToken token = default)
        => Task.FromResult<RepositoryGroup?>(null);
    public Task<RepositoryGroup> SetActiveExtensionVersionAsync(RepositoryGroup group, CancellationToken token = default)
        => Task.FromResult(group);

    // IRepositoryManager
    public Task<TachiyomiRepository> AddOnlineRepositoryAsync(TachiyomiRepository repository, CancellationToken token = default)
        => Task.FromResult(repository);
    public List<TachiyomiRepository> ListOnlineRepositories() => [];
    public Task RefreshAllRepositoriesAsync(CancellationToken token = default) => Task.CompletedTask;
    public Task<bool> RemoveOnlineRespositoryAsync(TachiyomiRepository repository, CancellationToken token = default) => Task.FromResult(false);

    // IExtensionRpcBridge — only the async extension listing is exercised by MihonBridgeService.
    public Task<List<RepositoryGroup>> ListExtensionsAsync(bool includeTemporary = false, CancellationToken token = default)
        => Task.FromResult(_groups);
    public Task<List<TachiyomiRepository>> ListOnlineRepositoriesAsync(CancellationToken token = default)
        => Task.FromResult(new List<TachiyomiRepository>());
    public Task<List<Rensaio.RPC.Contracts.SourceDescriptor>> ListSourceDescriptorsAsync(string package, CancellationToken token = default)
        => Task.FromResult(new List<Rensaio.RPC.Contracts.SourceDescriptor>());
    public Task<ISourceInterop> GetSourceInteropAsync(string package, long sourceId, CancellationToken token = default)
        => throw new NotSupportedException();
    public Task<Stream?> OpenExtensionIconAsync(string packageId, CancellationToken token = default)
        => Task.FromResult<Stream?>(null);
    public Task<Rensaio.RPC.Contracts.OnlineExtensionDescriptor?> PrepareOnlineExtensionAsync(string package, CancellationToken token = default)
        => Task.FromResult<Rensaio.RPC.Contracts.OnlineExtensionDescriptor?>(null);
    public Task<bool> ReleaseOnlineExtensionAsync(string package, CancellationToken token = default) => Task.FromResult(false);
    public Task<int> ReleaseAllTemporaryExtensionsAsync(CancellationToken token = default) => Task.FromResult(0);
}

internal sealed class FakeWorkingFolderStructure : IWorkingFolderStructure
{
    public string ExtensionsFolder => "extensions";
    public string TempFolder => "temp";
    public string AndroidFolder => "android";
    public string WorkingFolder => ".";
}
