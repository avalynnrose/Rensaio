using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RensaioBackend.Data;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Metadata;
using RensaioBackend.Services.Series;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// Cascades a contribution-mapping edit into the Rensaio database, but only for series that are
/// actually present in the Rensaio DB. After a mapping mutation on the Contribution Mappings page,
/// we resolve every Rensaio series whose title collides (normalized) with any of the mapping's
/// titles and apply the same provider status/key change to its global <see cref="SeriesMappingEntity"/>
/// rows — exactly the semantics the External Mappings page uses — then sync rensaio.json.
///
/// The contribution DB remains the source of truth of the change; this is a best-effort cascade
/// that never creates new Rensaio series and never throws into the caller.
/// </summary>
public sealed class RensaioMappingCascadeService
{
    private readonly AppDbContext _db;
    private readonly ContributionDbContext _contributorDb;
    private readonly SeriesStateService _seriesStateService;
    private readonly MetadataLinkEngine _linkEngine;
    private readonly ILogger<RensaioMappingCascadeService> _logger;

    public RensaioMappingCascadeService(
        AppDbContext db,
        ContributionDbContext contributorDb,
        SeriesStateService seriesStateService,
        MetadataLinkEngine linkEngine,
        ILogger<RensaioMappingCascadeService> logger)
    {
        _db = db;
        _contributorDb = contributorDb;
        _seriesStateService = seriesStateService;
        _linkEngine = linkEngine;
        _logger = logger;
    }

    /// <summary>
    /// Applies a provider change to every Rensaio series whose normalized title matches any of the
    /// contribution mapping's titles. <paramref name="apply"/> receives the Rensaio mapping row (may
    /// be null when none exists yet) and must mutate/flag it; <paramref name="create"/> supplies the
    /// row to create when none exists (return null to skip creation); when <paramref name="delete"/>
    /// is true an existing row is removed (tombstone) instead of mutated.
    ///
    /// When <paramref name="automatch"/> is true the metadata link engine is re-run for each
    /// collided series after the change is persisted, so the freshly-confirmed provider seeds a
    /// link pass that propagates to every OTHER provider (alternates) the seeded provider declares
    /// — mirroring <c>SeriesMatchingService.ConfirmMatchAsync</c>'s behaviour on the Rensaio side.
    /// </summary>
    public async Task CascadeAsync(Guid mappingId, ExternalSeriesProvider provider,
        Action<SeriesMappingEntity>? apply = null, Func<Guid, SeriesMappingEntity>? create = null,
        bool delete = false, bool automatch = false, CancellationToken token = default)
    {
        try
        {
            // 1. Resolve the mapping's normalized titles (the automerge identity set).
            var mappingTitles = await _contributorDb.SeriesTitles
                .Include(mt => mt.Title)
                .Where(mt => mt.SeriesId == mappingId)
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);

            var normalizedTitles = mappingTitles
                .Where(mt => mt.Title != null && !string.IsNullOrWhiteSpace(mt.Title.Title))
                .Select(mt => TitleEntity.Normalize(mt.Title!.Title!))
                .Where(t => t.Length > 0)
                .ToHashSet(StringComparer.Ordinal);
            if (normalizedTitles.Count == 0) return;

            // 2. Find Rensaio series whose main title or a source title collides with the mapping titles.
            var seriesWithSources = await _db.Series
                .Include(s => s.Sources)
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);

            var matchedSeries = seriesWithSources
                .Where(s => normalizedTitles.Contains(TitleEntity.Normalize(s.Title))
                    || s.Sources.Any(src => !string.IsNullOrWhiteSpace(src.Title)
                        && normalizedTitles.Contains(TitleEntity.Normalize(src.Title))))
                .ToList();

            if (matchedSeries.Count == 0) return;
            if (matchedSeries.Count > 1)
            {
                _logger.LogWarning("Contribution mapping {MappingId}: cascading to {Count} Rensaio series "
                    + "(title collisions) for provider {Provider}", mappingId, matchedSeries.Count, provider);
            }

            // 3. Apply the change to each series' global mapping row.
            var now = DateTime.UtcNow;
            foreach (var series in matchedSeries)
            {
                var mapping = await _db.SeriesMappings
                    .FirstOrDefaultAsync(m => m.SeriesId == series.Id && m.Provider == provider, token)
                    .ConfigureAwait(false);

                if (mapping != null)
                {
                    if (delete)
                    {
                        _db.SeriesMappings.Remove(mapping);
                    }
                    else
                    {
                        apply?.Invoke(mapping);
                        mapping.UpdateDate = now;
                    }
                }
                else if (create != null)
                {
                    var created = create(series.Id);
                    if (created != null)
                    {
                        _db.SeriesMappings.Add(created);
                    }
                }
                else
                {
                    continue; // no row and caller chose not to create → nothing to persist
                }

                await _db.SaveChangesAsync(token).ConfigureAwait(false);
                await _seriesStateService.SyncToRensaioJsonAsync(series.Id, token).ConfigureAwait(false);

                // Automatch: re-run the cross-provider engine for this series so the confirmed
                // provider seeds a pass that links the remaining (alternate) providers too.
                if (automatch && !delete)
                {
                    try
                    {
                        await _linkEngine.LinkSeriesAsync(series.Id, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Automatch after cascade failed for Rensaio series {SeriesId}", series.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Best-effort cascade — never break the contribution-mapping edit because of it.
            _logger.LogWarning(ex, "Failed to cascade contribution mapping {MappingId} into Rensaio DB", mappingId);
        }
    }
}
