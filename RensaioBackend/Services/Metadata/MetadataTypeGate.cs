using Microsoft.Extensions.Configuration;
using RensaioBackend.Services.Scrobbling.Abstractions;

namespace RensaioBackend.Services.Metadata;

/// <summary>
/// Content-type gate for the metadata providers, driven by the root <c>TypeMappings</c> list in
/// appsettings.json (replaces the old <c>Vine</c> list).
///
/// Each entry maps an extension <c>package</c> to the comma-separated <c>content</c> types its
/// sources carry (for example <c>"manga,manhwa,manhua"</c> or <c>"comics"</c>). When a package (or
/// a mapping sourced from it) is seeded, the mappings are looked up here to derive the content
/// types; a metadata provider only runs when it declares support (<see
/// cref="IExternalSeriesProvider.SupportedTypes"/>) for at least one of those types. Providers that
/// do not support any of the content types are skipped entirely.
///
/// Special cases:
///   * An EMPTY content string assumes <c>"manga"</c> — the safe default for unclassified sources.
///   * A content string whose types are ALL <c>"usercreatedcontent"</c> or <c>"doujinshi"</c> is
///     skipped altogether: no metadata provider is run for such a source.
/// </summary>
public sealed class MetadataTypeGate
{
    /// <summary>Assumed content when a package has an empty (or missing) content string.</summary>
    private const string DefaultContent = "manga";

    /// <summary>
    /// Content types that, on their own, make the whole source ineligible for any metadata provider.
    /// </summary>
    private static readonly HashSet<string> SkipOnlyContents =
        new(StringComparer.OrdinalIgnoreCase) { "usercreatedcontent", "doujinshi" };

    private readonly IConfiguration _configuration;

    public MetadataTypeGate(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// The configured package → content types map. Later entries win on a duplicated package so a
    /// layered configuration override behaves intuitively.
    /// </summary>
    private Dictionary<string, string> BuildMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _configuration.GetSection("TypeMappings").GetChildren())
        {
            string? package = entry["package"];
            if (string.IsNullOrWhiteSpace(package))
                continue;
            map[package.Trim()] = entry["content"] ?? string.Empty;
        }
        return map;
    }

    /// <summary>
    /// The raw content string configured for <paramref name="package"/> (may be empty). When the
    /// package is unknown an empty string is returned, which the callers treat as the default
    /// (<c>"manga"</c>).
    /// </summary>
    public string GetContent(string? package)
    {
        if (string.IsNullOrWhiteSpace(package))
            return string.Empty;

        return BuildMap().TryGetValue(package.Trim(), out var content) ? content : string.Empty;
    }

    /// <summary>
    /// The normalized content-type tokens for <paramref name="package"/>. An empty/missing content
    /// string yields <c>["manga"]</c>; otherwise the comma-separated tokens are trimmed, lowercased
    /// and de-duplicated.
    /// </summary>
    public IReadOnlyList<string> GetContentTypes(string? package)
    {
        string content = GetContent(package);
        return NormalizeContent(content);
    }

    /// <summary>
    /// Splits a content string into normalized tokens, applying the empty → <c>"manga"</c> default.
    /// </summary>
    public static IReadOnlyList<string> NormalizeContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [DefaultContent];

        var tokens = content
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return tokens.Count == 0 ? [DefaultContent] : tokens;
    }

    /// <summary>
    /// True when the package's content types are exclusively <c>usercreatedcontent</c> /
    /// <c>doujinshi</c>, meaning NO metadata provider must run for its sources.
    /// </summary>
    public bool ShouldSkipAll(string? package)
    {
        var types = GetContentTypes(package);
        return types.Count == 0 || types.Any(SkipOnlyContents.Contains);
//        return types.Count > 0 && types.All(SkipOnlyContents.Contains);
    }

    /// <summary>
    /// True when <paramref name="provider"/> declares support for at least one of the package's
    /// content types (<see cref="IExternalSeriesProvider.SupportedTypes"/> compared case-insensitively).
    /// A skip-only package always returns false.
    /// </summary>
    public bool ProviderSupports(string? package, IExternalSeriesProvider provider)
    {
        if (provider is null)
            return false;
        if (ShouldSkipAll(package))
            return false;

        var supported = provider.SupportedTypes
            ?.Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        if (supported.Count == 0)
            return false;

        foreach (var type in GetContentTypes(package))
            if (supported.Contains(type))
                return true;

        return false;
    }

    /// <summary>
    /// Filters <paramref name="providers"/> down to those allowed for <paramref name="package"/>
    /// (see <see cref="ProviderSupports"/>). Order is preserved.
    /// </summary>
    public List<IExternalSeriesProvider> FilterProviders(
        string? package, IEnumerable<IExternalSeriesProvider> providers)
        => providers.Where(p => ProviderSupports(package, p)).ToList();

    /// <summary>
    /// True when <paramref name="provider"/> is allowed for at least ONE of the supplied mapping
    /// source packages. A mapping whose packages all resolve to skip-only content (or whose
    /// packages are unknown — treated as the <c>manga</c> default) is evaluated per package; the
    /// provider runs when it supports any package's content types.
    /// </summary>
    public bool AnyPackageSupports(
        IEnumerable<string?> sourcePackages, IExternalSeriesProvider provider)
    {
        if (provider is null)
            return false;

        foreach (var package in sourcePackages)
            if (ProviderSupports(package, provider))
                return true;

        return false;
    }
}
