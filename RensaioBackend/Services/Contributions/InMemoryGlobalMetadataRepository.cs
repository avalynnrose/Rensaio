using RensaioBackend.Data;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Contributions.Abstractions;
using RensaioBackend.Services.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// In-memory global metadata repository. Hydrated at startup from the app-carried global
/// <see cref="RensaioBackend.Models.Database.SeriesMappingEntity"/> rows (each series becomes a
/// <see cref="GlobalTitleEntity"/>, its LinkedSitesIds become provider associations). This is a
/// provisional implementation so the External Mappings page can run before PR #76 merges;
/// PR #76's snapshot-backed index can be adopted later without changing the
/// <see cref="IGlobalMetadataRepository"/> contract.
/// </summary>
public sealed class InMemoryGlobalMetadataRepository : IGlobalMetadataRepository
{
    private readonly AppDbContext _db;
    private readonly SeriesMetadataResolver _resolver;
    private readonly ILogger<InMemoryGlobalMetadataRepository> _logger;

    private volatile Dictionary<string, GlobalTitleEntity> _titles = new(StringComparer.Ordinal);
    private volatile List<GlobalAssociationEntity> _associations = [];

    public InMemoryGlobalMetadataRepository(AppDbContext db, SeriesMetadataResolver resolver,
        ILogger<InMemoryGlobalMetadataRepository> logger)
    {
        _db = db;
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>(Re)loads the in-memory index from global SeriesMappings + series rows.</summary>
    public async Task RefreshAsync(CancellationToken token = default)
    {
        var titles = new Dictionary<string, GlobalTitleEntity>(StringComparer.Ordinal);
        var associations = new List<GlobalAssociationEntity>();

        try
        {
            var allSeries = await _db.Series.ToListAsync(token).ConfigureAwait(false);
            var seriesById = new Dictionary<Guid, RensaioBackend.Models.Database.SeriesEntity>();
            foreach (var row in allSeries)
                seriesById[row.Id] = row;

            var mappings = await _db.SeriesMappings.ToListAsync(token).ConfigureAwait(false);
            foreach (var mapping in mappings)
            {
                if (mapping.Provider == null || mapping.SeriesId == null) continue;
                Guid seriesId = (Guid)mapping.SeriesId;

                string titleId = seriesId.ToString().Replace("-", "").ToLowerInvariant();
                var title = titles.GetValueOrDefault(titleId);
                if (title == null)
                {
                    RensaioBackend.Models.Database.SeriesEntity? series = seriesById[seriesId];
                    title = new GlobalTitleEntity
                    {
                        Id = titleId,
                        Title = series?.Title ?? string.Empty,
                        Type = series?.Type,
                        Genre = series?.Genre ?? []
                    };
                    titles[titleId] = title;
                }

                associations.Add(new GlobalAssociationEntity
                {
                    TitleId = titleId,
                    Provider = mapping.Provider,
                    ProviderKey = mapping.ExternalSeriesId,
                    LinkType = 0
                });

                foreach (var linked in mapping.LinkedSitesIds)
                {
                    var (site, id) = SplitSiteId(linked);
                    if (site == null || string.IsNullOrWhiteSpace(id)) continue;
                    associations.Add(new GlobalAssociationEntity
                    {
                        TitleId = titleId,
                        Provider = (ExternalSeriesProvider)site,
                        ProviderKey = id,
                        LinkType = 0
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh in-memory global metadata repository");
        }

        _titles = titles;
        _associations = associations;
        _logger.LogInformation("In-memory global metadata repository loaded: {Titles} titles, {Assoc} associations",
            _titles.Count, _associations.Count);
    }

    public List<GlobalTitleEntity> GetAllTitlesAsync(CancellationToken token = default)
    {
        return _titles.Values.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<GlobalTitleEntity?> GetTitleAsync(string titleId, CancellationToken token = default)
    {
        var found = titleId != null ? _titles.GetValueOrDefault(titleId) : null;
        return Task.FromResult(found);
    }

    public List<GlobalAssociationEntity> GetAssociationsAsync(string titleId, CancellationToken token = default)
    {
        if (titleId == null) return [];
        return _associations.Where(a => string.Equals(a.TitleId, titleId, StringComparison.Ordinal)).ToList();
    }

    public string? ResolveTitleIdAsync(ExternalSeriesProvider provider, string providerKey, CancellationToken token = default)
    {
        if (providerKey == null) return null;
        var slug = _resolver.GetSiteSlug(provider);

        foreach (var a in _associations)
        {
            if (a.Provider == provider && string.Equals(a.ProviderKey, providerKey, StringComparison.Ordinal))
                return a.TitleId;
        }
        var needle = $"{slug}:{providerKey}";
        foreach (var a in _associations)
        {
            if (string.Equals($"{_resolver.GetSiteSlug(a.Provider)}:{a.ProviderKey}", needle, StringComparison.OrdinalIgnoreCase))
                return a.TitleId;
        }
        return null;
    }

    /// <summary>True when the repository holds at least one title.</summary>
    public bool HasTitles
    {
        get => _titles.Count > 0;
    }

    private static (ExternalSeriesProvider? Site, string? Id) SplitSiteId(string linked)
    {
        var idx = linked.IndexOf(':');
        if (idx <= 0 || idx == linked.Length - 1)
            return (null, null);
        var slug = linked[..idx];
        var id = linked[(idx + 1)..];
        // Ignore placeholder ids (e.g. myanimelist:0) — they are not real provider associations.
        if (long.TryParse(id.Trim(), out long numeric) && numeric <= 0)
            return (null, null);
        var slugToProvider = new Dictionary<string, ExternalSeriesProvider>(StringComparer.OrdinalIgnoreCase)
        {
            ["myanimelist"] = ExternalSeriesProvider.MyAnimeList,
            ["anilist"] = ExternalSeriesProvider.AniList,
            ["comicvine"] = ExternalSeriesProvider.ComicVine,
            ["kitsu"] = ExternalSeriesProvider.Kitsu,
            ["mangadex"] = ExternalSeriesProvider.MangaDex,
            ["mangabaka"] = ExternalSeriesProvider.MangaBaka,
            ["bgm"] = ExternalSeriesProvider.Bangumi,
            ["mangaupdates"] = ExternalSeriesProvider.MangaUpdates,
            ["gcd"] = ExternalSeriesProvider.GrandComicsDatabase,
            ["metron"] = ExternalSeriesProvider.Metron
        };
        var parsed = slugToProvider.GetValueOrDefault(slug);
        return (parsed, id);
    }
}