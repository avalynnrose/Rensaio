using RensaioBackend.Models.Abstractions;
using RensaioBackend.Models.Enums;
using System.Text.Json.Serialization;

namespace RensaioBackend.Models.Dto;

/// <summary>
/// One member source within a contribution mapping group (source scope). The group header on the
/// Contribution Mappings page lists these merged sources; provider rows reuse
/// <see cref="ExternalMappingsSeriesProviderDto"/> so the two pages render identically.
/// </summary>
public sealed class ContributionMappingSourceDto : IThumb
{
    [JsonPropertyName("sourceId")]
    public Guid SourceId { get; set; }

    /// <summary>Canonical source id (package:sourceId) — stable across contribution DBs.</summary>
    [JsonPropertyName("sourceKey")]
    public string SourceKey { get; set; } = string.Empty;

    [JsonPropertyName("package")]
    public string Package { get; set; } = string.Empty;

    [JsonPropertyName("sourceName")]
    public string SourceName { get; set; } = string.Empty;

    [JsonPropertyName("sourceLanguage")]
    public string SourceLanguage { get; set; } = string.Empty;

    /// <summary>Title of this source entry (from the mapping's TitleEntity), may differ from the display title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Cover from the source record (Data.ThumbnailUrl) — the group shows the first non-empty one.</summary>
    [JsonPropertyName("thumbnailUrl")]
    public string? ThumbnailUrl { get; set; }

    /// <summary>
    /// Fully-resolved, absolute source page url (the entry's Data.Url transformed through the
    /// owning extension's <c>getMangaUrl</c>), so the UI can deep-link straight to the series page.
    /// </summary>
    [JsonPropertyName("sourceUrl")]
    public string? SourceUrl { get; set; }

    /// <summary>Alias for the thumbnail-cache rewrite machine; never serialized (see thumbnailUrl).</summary>
    [JsonIgnore]
    public string? ThumbnailAlias
    {
        get => ThumbnailUrl;
        set => ThumbnailUrl = value;
    }
}

/// <summary>
/// One grouped mapping of the Contribution Mappings page (source scope). Pagination is over
/// mappings: <c>MappingId</c> / <c>DisplayTitle</c> / <c>CoverUrl</c> identify the group;
/// <c>Titles</c> holds every normalized title linked to the mapping (the automerge key);
/// <c>Sources</c> holds the member source entries (shared-title automerge);
/// <c>Providers</c> holds one row per metadata provider (same shape as the External Mappings page).
/// </summary>
public sealed class ContributionMappingGroupDto : IThumb
{
    [JsonPropertyName("mappingId")]
    public Guid MappingId { get; set; }

    /// <summary>First non-empty title of the mapping (primary display name).</summary>
    [JsonPropertyName("displayTitle")]
    public string? DisplayTitle { get; set; }

    /// <summary>First non-empty source cover among the group's sources.</summary>
    [JsonPropertyName("coverUrl")]
    public string? CoverUrl { get; set; }

    /// <summary>Alias for the thumbnail-cache rewrite machine; never serialized (see coverUrl).</summary>
    [JsonIgnore]
    public string? ThumbnailUrl
    {
        get => CoverUrl;
        set => CoverUrl = value;
    }

    [JsonPropertyName("titles")]
    public List<string> Titles { get; set; } = [];

    /// <summary>
    /// Set when the mapping is Verified (EVERY live source entry carries a VerifiedDateUTC — the
    /// most recent one is reported); null when Unverified (at least one live entry is null).
    /// </summary>
    [JsonPropertyName("verifiedDateUtc")]
    public DateTime? VerifiedDateUtc { get; set; }

    [JsonPropertyName("sources")]
    public List<ContributionMappingSourceDto> Sources { get; set; } = [];

    /// <summary>Provider rows — same shape as the External Mappings page so the UI rendering is identical.</summary>
    [JsonPropertyName("providers")]
    public List<ExternalMappingsSeriesProviderDto> Providers { get; set; } = [];
}

/// <summary>Paginated response for the Contribution Mappings list.</summary>
public sealed class ContributionMappingsPageDto
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("total")]
    public long Total { get; set; }

    /// <summary>Per-provider icon + series URL template, keyed by provider name (same as External Mappings).</summary>
    [JsonPropertyName("providerMeta")]
    public Dictionary<string, ExternalProviderMetaDto> ProviderMeta { get; set; } = [];

    /// <summary>Source-scope: grouped by mapping (pagination counts mappings, not provider rows).</summary>
    [JsonPropertyName("groups")]
    public List<ContributionMappingGroupDto> Groups { get; set; } = [];
}

/// <summary>Request body for a manual contribution-mapping link confirm.</summary>
public sealed class ContributionMappingLinkDto
{
    [JsonPropertyName("externalSeriesId")]
    public string ExternalSeriesId { get; set; } = string.Empty;

    [JsonPropertyName("externalSeriesTitle")]
    public string? ExternalSeriesTitle { get; set; }
}

/// <summary>Request body for joining two or more contribution mappings into one.</summary>
public sealed class ContributionMappingJoinDto
{
    /// <summary>The mapping (Series) ids to merge. Two or more required.</summary>
    [JsonPropertyName("mappingIds")]
    public List<Guid> MappingIds { get; set; } = [];
}

/// <summary>
/// One ranked candidate returned by the Contribution page's "Smart Search". It carries the same
/// surface the normal provider search exposes (<see cref="ScrobblerSearchResult"/>) plus the
/// combined scoring used to rank it: <c>Score</c> is the blended value the list is sorted by,
/// <c>TitleScore</c> the pure title-match percentage and <c>ImageScore</c>/<c>ImageDistance</c>
/// the best PDQ cover comparison against the series' reference covers (null when no image could
/// be compared).
/// </summary>
public sealed class SmartSearchHitDto : IThumb
{
    [JsonPropertyName("externalId")]
    public string ExternalId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("alternateTitles")]
    public List<string> AlternateTitles { get; set; } = [];

    [JsonPropertyName("coverUrl")]
    public string? CoverUrl { get; set; }

    /// <summary>Alias for the thumbnail-cache rewrite machine; never serialized (see coverUrl).</summary>
    [JsonIgnore]
    public string? ThumbnailUrl
    {
        get => CoverUrl;
        set => CoverUrl = value;
    }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    /// <summary>Blended ranking score (0..100); the list is sorted by this descending.</summary>
    [JsonPropertyName("score")]
    public double Score { get; set; }

    /// <summary>Best title-match percentage (0..100) against the mapping's candidate titles.</summary>
    [JsonPropertyName("titleScore")]
    public int TitleScore { get; set; }

    /// <summary>Best cover similarity percentage (0..100) against the series' reference images; null when none.</summary>
    [JsonPropertyName("imageScore")]
    public int? ImageScore { get; set; }

    /// <summary>PDQ Hamming distance of the best cover comparison; null when no image was compared.</summary>
    [JsonPropertyName("imageDistance")]
    public int? ImageDistance { get; set; }
}

/// <summary>Response for the Contribution page's "Smart Search" (top ranked provider candidates).</summary>
public sealed class SmartSearchResultDto
{
    [JsonPropertyName("provider")]
    public ExternalSeriesProvider Provider { get; set; }

    [JsonPropertyName("results")]
    public List<SmartSearchHitDto> Results { get; set; } = [];
}
