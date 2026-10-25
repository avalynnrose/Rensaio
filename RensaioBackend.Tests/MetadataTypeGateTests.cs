using Microsoft.Extensions.Configuration;
using RensaioBackend.Services.Metadata;
using RensaioBackend.Services.Scrobbling.Abstractions;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using Xunit;

namespace RensaioBackend.Tests;

/// <summary>
/// Tests for <see cref="MetadataTypeGate"/> — the content-type gate that replaced the old
/// ComicVine "Vine" mechanism. Covers the <c>TypeMappings</c> lookup, the empty-content "manga"
/// default, the skip-only <c>usercreatedcontent</c>/<c>doujinshi</c> special case, and the
/// <c>SupportedTypes</c> filtering.
/// </summary>
public sealed class MetadataTypeGateTests
{
    private static MetadataTypeGate BuildGate(params (string Package, string Content)[] mappings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(mappings
                .SelectMany((m, i) => new[]
                {
                    new KeyValuePair<string, string?>($"TypeMappings:{i}:package", m.Package),
                    new KeyValuePair<string, string?>($"TypeMappings:{i}:content", m.Content),
                }))
            .Build();
        return new MetadataTypeGate(config);
    }

    // ── NormalizeContent (empty → manga default) ──

    [Theory]
    [InlineData("", "manga")]
    [InlineData("   ", "manga")]
    [InlineData("manga", "manga")]
    [InlineData("Manga", "manga")]
    [InlineData("manga,manhwa,manhua", "manga,manhwa,manhua")]
    [InlineData(" doujinshi ", "doujinshi")]
    public void NormalizeContent_SplitsAndDefaultsToManga(string content, string expectedCsv)
    {
        var expected = expectedCsv.Split(',');
        Assert.Equal(expected, MetadataTypeGate.NormalizeContent(content));
    }

    // ── ShouldSkipAll (usercreatedcontent / doujinshi only) ──

    [Theory]
    [InlineData("usercreatedcontent")]
    [InlineData("doujinshi")]
    [InlineData("usercreatedcontent,doujinshi")]
    [InlineData("doujinshi,usercreatedcontent")]
    [InlineData("Doujinshi, UserCreatedContent")]
    public void ShouldSkipAll_SkipsWhenOnlyUserCreatedOrDoujinshi(string content)
    {
        var gate = BuildGate(("pkg", content));
        Assert.True(gate.ShouldSkipAll("pkg"));
    }

    [Theory]
    [InlineData("manga")]
    [InlineData("manga,doujinshi")]
    [InlineData("comics,usercreatedcontent")]
    [InlineData("")] // empty → "manga" default → not skip-only
    public void ShouldSkipAll_DoesNotSkipWhenOtherContentPresent(string content)
    {
        var gate = BuildGate(("pkg", content));
        Assert.False(gate.ShouldSkipAll("pkg"));
    }

    // ── GetContentTypes ──

    [Fact]
    public void GetContentTypes_UnknownPackage_FallsBackToManga()
    {
        var gate = BuildGate();
        Assert.Equal(new[] { "manga" }, gate.GetContentTypes("does.not.exist"));
    }

    [Fact]
    public void GetContentTypes_EmptyContent_FallsBackToManga()
    {
        var gate = BuildGate(("pkg", ""));
        Assert.Equal(new[] { "manga" }, gate.GetContentTypes("pkg"));
    }

    // ── ProviderSupports / FilterProviders ──

    [Fact]
    public void ProviderSupports_UsesSupportedTypes()
    {
        var gate = BuildGate(("comics.pkg", "comics"));
        var comicsProvider = new FakeProvider(["Comics"]);
        var mangaProvider = new FakeProvider(["Manga", "Manhwa", "Manhua"]);

        Assert.True(gate.ProviderSupports("comics.pkg", comicsProvider));
        Assert.False(gate.ProviderSupports("comics.pkg", mangaProvider));
    }

    [Fact]
    public void ProviderSupports_SkipOnlyPackage_SkipsEveryProvider()
    {
        var gate = BuildGate(("doujin.pkg", "doujinshi,usercreatedcontent"));
        var provider = new FakeProvider(["Manga", "Comics"]);

        Assert.False(gate.ProviderSupports("doujin.pkg", provider));
        Assert.Empty(gate.FilterProviders("doujin.pkg", [provider]));
    }

    [Fact]
    public void ProviderSupports_EmptyContent_BehavesAsManga()
    {
        var gate = BuildGate(("unknown", ""));
        var mangaProvider = new FakeProvider(["Manga"]);
        var comicsProvider = new FakeProvider(["Comics"]);

        Assert.True(gate.ProviderSupports("unknown", mangaProvider));
        Assert.False(gate.ProviderSupports("unknown", comicsProvider));
    }

    [Fact]
    public void AnyPackageSupports_TrueWhenAnyPackageMatches()
    {
        var gate = BuildGate(("a.pkg", "comics"), ("b.pkg", "manga"));
        var provider = new FakeProvider(["Manga"]);

        Assert.True(gate.AnyPackageSupports(["a.pkg", "b.pkg"], provider));
        Assert.False(gate.AnyPackageSupports(["a.pkg"], provider));
    }

    private sealed class FakeProvider : IExternalSeriesProvider
    {
        public FakeProvider(string[] supportedTypes) => SupportedTypes = supportedTypes;

        public string[] SupportedTypes { get; }

        public ExternalSeriesProvider ProviderType => ExternalSeriesProvider.MangaBaka;
        public ProviderFeatures Features => ProviderFeatures.Metadata;
        public string DisplayName => "Fake";
        public string? Icon => null;
        public string? Link => null;
        public string? LinkDescription => null;
        public string? SeriesUrlTemplate => null;
        public string? ImageTemplateUrl => null;
        public bool RequiresOAuth => false;
        public bool SupportsDirectAuth => false;

        public Task<ScrobblerAuthUrlResult> GetAuthorizationUrlAsync(string redirectUri, string state) => throw new NotSupportedException();
        public Task<ScrobblerTokenResult> ExchangeCodeAsync(string code, string redirectUri) => throw new NotSupportedException();
        public Task<ScrobblerTokenResult> RefreshTokenAsync(string refreshToken, CancellationToken token = default) => throw new NotSupportedException();
        public Task<ScrobblerTokenResult> AuthenticateDirectAsync(DirectAuthRequest request) => throw new NotSupportedException();
        public void SetAccessToken(string accessToken, Guid userid) { }
        public Task<bool> ValidateApiKeyAsync(string apiKey) => Task.FromResult(true);
        public Task<List<ScrobblerSearchResult>> SearchSeriesAsync(string query, CancellationToken token = default) => Task.FromResult(new List<ScrobblerSearchResult>());
        public Task EnsureAuthenticatedAsync(Guid userId, CancellationToken token = default) => Task.CompletedTask;
        public Task<bool> ValidateTokenAsync(CancellationToken token = default) => Task.FromResult(true);
        public List<string> FilterLookupTitles(IEnumerable<string> titles) => titles.ToList();
        public bool CanSearchSeries(List<string> genres, string? category = null) => true;
    }
}
