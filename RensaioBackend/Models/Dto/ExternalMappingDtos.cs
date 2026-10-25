using RensaioBackend.Models.Abstractions;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Enums;
using System.Text.Json.Serialization;

namespace RensaioBackend.Models.Dto;

/// <summary>
/// One provider row inside a series group (series scope). Backed by a global SeriesMapping,
/// or synthesized as an Unmatched row when no mapping exists for that provider.
/// </summary>
public sealed class ExternalMappingsSeriesProviderDto : IThumb
{
    [JsonPropertyName("providerCoverUrl")]
    public string? ProviderCoverUrl { get; set; }

    /// <summary>Alias for the thumbnail-cache rewrite machine; never serialized (see providerCoverUrl).</summary>
    [JsonIgnore]
    public string? ThumbnailUrl
    {
        get => ProviderCoverUrl;
        set => ProviderCoverUrl = value;
    }

    [JsonPropertyName("provider")]
    public ExternalSeriesProvider Provider { get; set; }

    [JsonPropertyName("externalSeriesId")]
    public string ExternalSeriesId { get; set; } = string.Empty;

    [JsonPropertyName("externalSeriesTitle")]
    public string? ExternalSeriesTitle { get; set; }

    [JsonPropertyName("mappingStatus")]
    public SeriesMappingStatus MappingStatus { get; set; }

    [JsonPropertyName("linkedDate")]
    public DateTime? LinkedDate { get; set; }

    [JsonPropertyName("linkedSitesIds")]
    public List<string> LinkedSitesIds { get; set; } = [];

    [JsonPropertyName("alternativeTitles")]
    public List<string> AlternativeTitles { get; set; } = [];
}

/// <summary>
/// One grouped row of the External Mappings page (series scope). Pagination is over series:
/// <c>SeriesId</c> / <c>SeriesTitle</c> / <c>SeriesCoverUrl</c> (local thumbnail) identify the
/// group; <c>Providers</c> holds one entry per metadata provider with that provider's own cover,
/// link state, cross-site IDs and alternate titles.
/// </summary>
public sealed class ExternalSeriesGroupDto : IThumb
{
    [JsonPropertyName("seriesId")]
    public Guid SeriesId { get; set; }

    [JsonPropertyName("seriesTitle")]
    public string? SeriesTitle { get; set; }

    /// <summary>Local series thumbnail (never a provider cover — see providerCoverUrl).</summary>
    [JsonPropertyName("seriesCoverUrl")]
    public string? SeriesCoverUrl { get; set; }

    /// <summary>Alias for the thumbnail-cache rewrite machine; never serialized (see seriesCoverUrl).</summary>
    [JsonIgnore]
    public string? ThumbnailUrl
    {
        get => SeriesCoverUrl;
        set => SeriesCoverUrl = value;
    }

    [JsonPropertyName("providers")]
    public List<ExternalMappingsSeriesProviderDto> Providers { get; set; } = [];
}

/// <summary>One row of the External Mappings page (titles scope: an in-memory global title).</summary>
public sealed class ExternalTitleMappingDto
{
    [JsonPropertyName("titleId")]
    public string TitleId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("associations")]
    public List<ExternalTitleAssociationDto> Associations { get; set; } = [];
}

public sealed class ExternalTitleAssociationDto
{
    [JsonPropertyName("provider")]
    public ExternalSeriesProvider Provider { get; set; }

    [JsonPropertyName("providerKey")]
    public string ProviderKey { get; set; } = string.Empty;

    [JsonPropertyName("linkType")]
    public int LinkType { get; set; }
}

/// <summary>Static per-provider presentation info (icon + series page URL template).</summary>
public sealed class ExternalProviderMetaDto
{
    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    /// <summary>Series detail URL template on this provider; {0} = external series ID.</summary>
    [JsonPropertyName("seriesUrlTemplate")]
    public string? SeriesUrlTemplate { get; set; }
}

/// <summary>Paginated response for the External Mappings list.</summary>
public sealed class ExternalMappingsPageDto
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("total")]
    public long Total { get; set; }

    /// <summary>Per-provider icon + series URL template, keyed by provider name (e.g. "MangaBaka").</summary>
    [JsonPropertyName("providerMeta")]
    public Dictionary<string, ExternalProviderMetaDto> ProviderMeta { get; set; } = [];

    /// <summary>Series scope: grouped by series (pagination counts series, not provider rows).</summary>
    [JsonPropertyName("series")]
    public List<ExternalSeriesGroupDto> Series { get; set; } = [];

    [JsonPropertyName("titles")]
    public List<ExternalTitleMappingDto> Titles { get; set; } = [];
}

/// <summary>Request body for ignore actions (temporary or forever).</summary>
public sealed class ExternalMappingIgnoreDto
{
    [JsonPropertyName("forever")]
    public bool Forever { get; set; } // false => ManualIgnore
}