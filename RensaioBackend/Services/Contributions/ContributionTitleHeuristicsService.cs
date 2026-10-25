using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RensaioBackend.Data;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Enums;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// Reconciles the title associations (<see cref="SeriesTitleEntity"/>) of a contribution series
/// against the titles that actually belong to it, tombstoning associations that are NOT backed by
/// any live contributor with the series.
///
/// contributor.db is the AUTHORITATIVE source for the mapping/title graph. The keep set is
/// therefore computed ENTIRELY from contributor.db — no rensaio.db lookup:
///   1. KEEP every title contributed by a LIVE source entry of the series
///      (<see cref="ContributionSeriesEntity.Data"/>.<c>TitleId</c>).
///   2. KEEP every title explicitly vouched for by an AutoMatched / UserConfirmed metadata row:
///      its <see cref="ContributionMetadataEntity.ProviderTitleIds"/> — the deterministic title
///      ids the provider reported for that link (canonical + alternative titles). This makes the
///      keep set work for contribution-only / sweep-created mappings that have no rensaio.db
///      SeriesMappings counterpart. Blocked / ignored rows never vouch for a title.
///   3. Tombstone every other LIVE title association (Version = -1). Titles
///      (<see cref="TitleEntity"/>) are never deleted — only the association is removed, so it
///      can be resurrected if the title is legitimately contributed again.
///
/// Idempotent: re-running produces no further changes. Safe to call after any block or after any
/// write that changes a series' metadata links.
/// </summary>
public sealed class ContributionTitleHeuristicsService
{
    private const int VersionLogicalDelete = ContributionVersions.PendingDelete;
    private const int VersionAddOrUpdate = ContributionVersions.AddOrUpdate;

    private readonly ContributionDbContext _contributorDb;
    private readonly ILogger<ContributionTitleHeuristicsService> _logger;

    public ContributionTitleHeuristicsService(
        ContributionDbContext contributorDb,
        ILogger<ContributionTitleHeuristicsService> logger)
    {
        _contributorDb = contributorDb;
        _logger = logger;
    }

    /// <summary>
    /// Derives the deterministic title ids for a provider link from its canonical title and its
    /// alternative titles. Used to persist <see cref="ContributionMetadataEntity.ProviderTitleIds"/>
    /// so the reconcile can vouch for exactly those titles.
    /// </summary>
    public static List<Guid> DeriveProviderTitleIds(string? title, IEnumerable<string>? alternateTitles)
    {
        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var id = TitleEntity.DeriveId(value!);
            if (seen.Add(id)) ids.Add(id);
        }

        Add(title);
        if (alternateTitles != null)
        {
            foreach (var alt in alternateTitles)
                Add(alt);
        }

        return ids;
    }

    /// <summary>
    /// Reconciles one contribution series. Returns the number of title associations tombstoned.
    /// </summary>
    public async Task<int> ReconcileSeriesAsync(Guid seriesId, CancellationToken token = default)
    {
        // 1. Live title associations of the series.
        var links = await _contributorDb.SeriesTitles
            .Where(mt => mt.SeriesId == seriesId && mt.Version != VersionLogicalDelete && mt.Version != ContributionVersions.UploadedDelete)
            .ToListAsync(token).ConfigureAwait(false);
        if (links.Count == 0) return 0;

        // 2. KEEP set from local source entries (their contributed title id).
        var keepTitleIds = new HashSet<Guid>();
        var sourceTitleIds = await _contributorDb.SeriesEntries
            .Where(s => s.SeriesId == seriesId
                && s.Version != VersionLogicalDelete
                && s.Version != ContributionVersions.UploadedDelete)
            .Select(s => s.Data)
            .ToListAsync(token).ConfigureAwait(false);
        foreach (var data in sourceTitleIds)
        {
            if (data != null && data.TitleId != Guid.Empty)
                keepTitleIds.Add(data.TitleId);
        }

        // 3. KEEP set from the Auto/User metadata rows' own persisted provider titles.
        //    contributor.db is authoritative: the provider's reported title ids are stored on the
        //    metadata row itself, so no rensaio.db resolution is needed and contribution-only
        //    mappings are covered too.
        var activeMetadata = await _contributorDb.Metadata
            .Where(m => m.SeriesId == seriesId
                && (m.MappingStatus == SeriesMappingStatus.AutoMatched || m.MappingStatus == SeriesMappingStatus.UserConfirmed)
                && m.Version != VersionLogicalDelete
                && m.Version != ContributionVersions.UploadedDelete)
            .AsNoTracking()
            .Select(m => m.ProviderTitleIds)
            .ToListAsync(token).ConfigureAwait(false);

        bool hasUnscopedAutoUserRow = false;
        foreach (var providerTitleIds in activeMetadata)
        {
            if (providerTitleIds == null || providerTitleIds.Count == 0)
            {
                // Legacy / cloud-imported row that predates the persisted provider-title set.
                hasUnscopedAutoUserRow = true;
                continue;
            }
            foreach (var id in providerTitleIds)
            {
                if (id != Guid.Empty)
                    keepTitleIds.Add(id);
            }
        }

        // A Blocked (or tombstoned) association on this series means the user explicitly refused a
        // provider / superseded a previous link. In that case the blanket "keep the whole title set"
        // fallback below must NOT run: it would protect the refused provider's titles from removal,
        // defeating the required "blocked/overwritten titles are removed" rule. Only the
        // independently-backed titles (live source entries + populated Auto/User sets) are kept.
        var hasRefusedRow = await _contributorDb.Metadata
            .Where(m => m.SeriesId == seriesId
                && m.MappingStatus == SeriesMappingStatus.Blocked)
            .AnyAsync(token).ConfigureAwait(false);

        if (hasUnscopedAutoUserRow && !hasRefusedRow)
        {
            // Contributor.db is authoritative: an Auto/User link WITHOUT a persisted provider
            // title set vouches for the series' whole current (non-source) title set, so
            // cloud-imported / legacy associations are never tombstoned. Precise trimming resumes
            // once a scan / propagation / confirm populates ProviderTitleIds for those rows.
            // Skipped entirely when a block exists (see hasRefusedRow above).
            foreach (var link in links)
                keepTitleIds.Add(link.TitleId);
        }

        // 4. Retire associations not backed by any kept title. A row that exists in the cloud
        //    (Version -2) is TOMBSTONED so the delete propagates to the worker; a never-exported
        //    row (0) — and any already-queued/exported tombstone (-1/-3) — is HARD-DELETED,
        //    because there is no cloud copy to inform and a residual tombstone would only bloat
        //    the local graph (the historical 61%-dead SeriesTitles).
        int removed = 0;
        int tombstoned = 0;
        foreach (var link in links)
        {
            if (keepTitleIds.Contains(link.TitleId)) continue;
            if (ContributionRowLifecycle.Retire(_contributorDb, link)
                == ContributionRowLifecycle.RetirementAction.Tombstone)
            {
                tombstoned++;
            }
            removed++;
        }

        if (removed > 0)
        {
            await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);

            // NOTE: contributor.db is authoritative, so "unbacked" does NOT imply "blocked". A
            // retired association simply has no live source entry and no Auto/User metadata row
            // that vouches for its title. Only cloud-backed associations are tombstoned; the rest
            // are deleted locally.
            _logger.LogInformation(
                "Contribution title reconciliation retired {Count} unbacked title association(s) for series {SeriesId} " +
                "({Tombstoned} tombstoned for cloud propagation, {Deleted} deleted locally; no live source entry or " +
                "Auto/User metadata vouches for the title; not necessarily blocked).",
                removed, seriesId, tombstoned, removed - tombstoned);
        }

        return removed;
    }

    /// <summary>
    /// Reconciles every series present in the contribution database. Returns the total number of
    /// title associations tombstoned.
    /// </summary>
    public async Task<int> ReconcileAllAsync(CancellationToken token = default)
    {
        var seriesIds = await _contributorDb.SeriesTitles
            .Where(mt => mt.Version != VersionLogicalDelete && mt.Version != ContributionVersions.UploadedDelete)
            .Select(mt => mt.SeriesId)
            .Distinct()
            .ToListAsync(token).ConfigureAwait(false);

        int total = 0;
        int seriesTouched = 0;
        foreach (var seriesId in seriesIds)
        {
            if (token.IsCancellationRequested) break;
            var removed = await ReconcileSeriesAsync(seriesId, token).ConfigureAwait(false);
            total += removed;
            if (removed > 0) seriesTouched++;
        }

        if (total > 0)
            _logger.LogInformation(
                "Contribution title reconciliation tombstoned {Count} unbacked title association(s) across {Series} series " +
                "(no live source entry or Auto/User metadata vouches for the title; not necessarily blocked).",
                total, seriesTouched);

        return total;
    }
}
