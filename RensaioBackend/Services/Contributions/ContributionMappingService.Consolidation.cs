using Microsoft.EntityFrameworkCore;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Enums;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// Enforces the provider-row invariant for the contribution graph (partial of
/// <see cref="ContributionMappingService"/>):
///
///   For each series there is at most ONE live <c>ContributionMetadataEntity</c> per
///   <c>(ProviderId, ProviderKey)</c>. <c>Blocked</c> rows are EXEMPT from the rule and may
///   coexist with that single live row (a block is an id-level / provider-level refusal that must
///   survive alongside a later link).
///
/// The "strongest" row wins a collision: highest <see cref="StatusPriority"/>, then most recent
/// LinkedDate, then smallest Id (deterministic/idempotent).
/// </summary>
public sealed partial class ContributionMappingService
{
    /// <summary>
    /// Collapses duplicate live provider rows for one series (Blocked rows excluded). Applies the
    /// full provider-row invariant in ONE pass:
    ///   1. exact duplicates on <c>(ProviderId, ProviderKey)</c> — strongest wins;
    ///   2. a keyed PRIMARY row (AutoMatched / UserConfirmed) suppresses the empty-key Unmatched
    ///      "Not matched" placeholder for the same provider (a Join merges metadata by key, so the
    ///      linked row and the sibling's Unmatched row would otherwise both survive);
    ///   3. at most ONE Unmatched row per provider (regardless of key) — MULTIPLE Unmatched rows
    ///      collapse to a single placeholder.
    /// Returns the number of rows tombstoned/removed. Idempotent; called after each scan/action.
    /// </summary>
    public async Task<int> ConsolidateProviderRowsForSeriesAsync(Guid seriesId, CancellationToken token = default)
    {
        var rows = await _contributorDb.Metadata
            .Where(m => m.SeriesId == seriesId
                && m.Version != VersionLogicalDelete && m.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        int removed = 0;
        var handled = new HashSet<Guid>();

        int Remove(ContributionMetadataEntity r)
        {
            if (!handled.Add(r.Id)) return 0;   // idempotent: never process a row twice
            return TombstoneOrRemove(r);
        }

        // (1) Exact (ProviderId, ProviderKey) duplicates — keep the strongest.
        foreach (var grp in rows
                     .Where(r => r.MappingStatus != SeriesMappingStatus.Blocked)
                     .GroupBy(r => (r.SeriesId, r.ProviderId, r.ProviderKey ?? string.Empty)))
        {
            if (grp.Count() <= 1) continue;
            var keep = PickStrongest(grp);
            foreach (var r in grp)
            {
                if (r.Id == keep.Id) continue;
                removed += Remove(r);
            }
        }

        // (2) + (3) Provider-level Unmatched handling.
        foreach (var grp in rows
                     .Where(r => r.MappingStatus != SeriesMappingStatus.Blocked)
                     .GroupBy(r => (r.SeriesId, r.ProviderId)))
        {
            var unmatched = grp
                .Where(r => r.MappingStatus == SeriesMappingStatus.Unmatched)
                .OrderBy(r => r.Id)
                .ToList();
            if (unmatched.Count == 0) continue;

            bool hasPrimary = grp.Any(r => r.MappingStatus is SeriesMappingStatus.AutoMatched
                or SeriesMappingStatus.UserConfirmed
                or SeriesMappingStatus.ManualIgnore
                or SeriesMappingStatus.ForeverIgnored);

            // A keyed link (Auto/User) supersedes the "Not matched" placeholder entirely; otherwise
            // keep exactly ONE Unmatched row per provider.
            if (hasPrimary)
            {
                foreach (var r in unmatched) removed += Remove(r);
            }
            else
            {
                foreach (var r in unmatched.Skip(1)) removed += Remove(r);
            }
        }

        if (removed > 0)
            await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
        return removed;
    }

    /// <summary>
    /// Returns the instance currently tracked for this row's id (if any), else the supplied
    /// detached instance. Joining/scanning leaves metadata rows tracked in the context, so a fresh
    /// <c>AsNoTracking</c> read must be re-bound to the tracked instance before Attach/Remove —
    /// otherwise EF throws "another instance with the same key value is already being tracked".
    /// </summary>
    private ContributionMetadataEntity ResolveTracked(ContributionMetadataEntity row)
    {
        if (_contributorDb.Entry(row).State != EntityState.Detached)
            return row;
        return _contributorDb.ChangeTracker.Entries<ContributionMetadataEntity>()
            .FirstOrDefault(e => e.Entity.Id == row.Id)?.Entity ?? row;
    }

    /// <summary>
    /// Retires a no-longer-wanted metadata row via the shared
    /// <see cref="ContributionRowLifecycle"/> rule: a row that exists in the cloud
    /// (<see cref="ContributionVersions.Uploaded"/>) is tombstoned so the delete propagates; a
    /// never-exported row is hard-removed (no cloud copy to delete); an uploaded tombstone is GC'd.
    /// Tracker-safe: <see cref="ResolveTracked"/> rebinds an AsNoTracking copy to the tracked instance.
    /// </summary>
    private int TombstoneOrRemove(ContributionMetadataEntity row)
    {
        ContributionRowLifecycle.Retire(_contributorDb, ResolveTracked(row));
        return 1;
    }

    /// <summary>
    /// Keeps a provider in a re-searchable "Not matched" state: when it has NO live
    /// AutoMatched / UserConfirmed link, exactly ONE empty-key Unmatched placeholder exists;
    /// when it does have a live link, no Unmatched placeholder remains. Used after a block so an
    /// id-scoped block still surfaces the provider as pending (searchable for a DIFFERENT id).
    /// Returns the number of rows changed.
    /// </summary>
    public async Task<int> EnsureUnmatchedPlaceholderAsync(
        Guid seriesId, ExternalSeriesProvider provider, CancellationToken token = default)
    {
        var rows = await _contributorDb.Metadata
            .Where(m => m.SeriesId == seriesId && m.ProviderId == (int)provider
                && m.Version != VersionLogicalDelete && m.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        bool hasLink = rows.Any(r => r.MappingStatus is SeriesMappingStatus.AutoMatched
            or SeriesMappingStatus.UserConfirmed);
        var unmatched = rows
            .Where(r => r.MappingStatus == SeriesMappingStatus.Unmatched)
            .OrderBy(r => r.Id)
            .ToList();

        int changed = 0;
        if (hasLink)
        {
            foreach (var r in unmatched) changed += TombstoneOrRemove(r);
        }
        else if (unmatched.Count == 0)
        {
            _contributorDb.Metadata.Add(new ContributionMetadataEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                ProviderId = (int)provider,
                ProviderKey = string.Empty,
                MappingStatus = SeriesMappingStatus.Unmatched,
                LinkedDate = null,
                Version = VersionAddOrUpdate
            });
            changed++;
        }
        else
        {
            foreach (var r in unmatched.Skip(1)) changed += TombstoneOrRemove(r);
        }

        if (changed > 0)
            await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    /// Collapses every live Unmatched row for one (series, provider) down to a SINGLE row
    /// (regardless of provider key), tombstoning the rest. Returns the number removed.
    /// </summary>
    public async Task<int> ConsolidateUnmatchedProviderAsync(
        Guid seriesId, ExternalSeriesProvider provider, CancellationToken token = default)
    {
        var rows = await _contributorDb.Metadata
            .Where(m => m.SeriesId == seriesId && m.ProviderId == (int)provider
                && m.MappingStatus == SeriesMappingStatus.Unmatched
                && m.Version != VersionLogicalDelete && m.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        int removed = 0;
        foreach (var r in rows.OrderBy(r => r.Id).Skip(1))
            removed += TombstoneOrRemove(r);
        if (removed > 0)
            await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
        return removed;
    }

    /// <summary>
    /// Collapses duplicate live provider rows across the whole graph (Blocked rows excluded).
    /// Returns the number of rows tombstoned. Idempotent.
    /// </summary>
    public async Task<int> ConsolidateProviderRowsAsync(CancellationToken token = default)
    {
        var rows = await _contributorDb.Metadata
            .Where(m => m.Version != VersionLogicalDelete && m.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        int removed = 0;
        var handled = new HashSet<Guid>();
        int Remove(ContributionMetadataEntity r)
        {
            if (!handled.Add(r.Id)) return 0;
            return TombstoneOrRemove(r);
        }

        // (1) Exact (SeriesId, ProviderId, ProviderKey) duplicates — strongest wins.
        foreach (var grp in rows
                     .Where(r => r.MappingStatus != SeriesMappingStatus.Blocked)
                     .GroupBy(r => (r.SeriesId, r.ProviderId, r.ProviderKey ?? string.Empty)))
        {
            if (grp.Count() <= 1) continue;
            var keep = PickStrongest(grp);
            foreach (var r in grp)
            {
                if (r.Id == keep.Id) continue;
                removed += Remove(r);
            }
        }

        // (2) Provider-level Unmatched handling per (series, provider).
        foreach (var grp in rows
                     .Where(r => r.MappingStatus != SeriesMappingStatus.Blocked)
                     .GroupBy(r => (r.SeriesId, r.ProviderId)))
        {
            var unmatched = grp
                .Where(r => r.MappingStatus == SeriesMappingStatus.Unmatched)
                .OrderBy(r => r.Id)
                .ToList();
            if (unmatched.Count == 0) continue;

            bool hasPrimary = grp.Any(r => r.MappingStatus is SeriesMappingStatus.AutoMatched
                or SeriesMappingStatus.UserConfirmed
                or SeriesMappingStatus.ManualIgnore
                or SeriesMappingStatus.ForeverIgnored);

            if (hasPrimary)
                foreach (var r in unmatched) removed += Remove(r);
            else
                foreach (var r in unmatched.Skip(1)) removed += Remove(r);
        }

        if (removed > 0)
        {
            await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
            _logger.LogInformation("Contribution provider-row consolidation tombstoned {Count} duplicate row(s).", removed);
        }
        return removed;
    }

    /// <summary>
    /// Read-side dedupe so a mapping renders the provider-row invariant IMMEDIATELY, even before
    /// the write-side consolidation runs (e.g. a series joined by an older build):
    ///   1. one row per <c>(ProviderId, ProviderKey)</c> (strongest wins);
    ///   2. Blocked rows kept untouched (outside the one-row rule);
    ///   3. at most ONE Unmatched placeholder per provider, and NONE when the provider already has
    ///      a primary row (AutoMatched / UserConfirmed / ManualIgnore / ForeverIgnored).
    /// </summary>
    internal static List<ContributionMetadataEntity> DedupeProviderRows(
        IEnumerable<ContributionMetadataEntity> rows)
    {
        var input = rows.ToList();
        var result = new List<ContributionMetadataEntity>();
        var byProviderKey = new Dictionary<(int ProviderId, string Key), ContributionMetadataEntity>();

        foreach (var r in input)
        {
            if (r.MappingStatus == SeriesMappingStatus.Blocked)
            {
                // Blocked lives outside the one-row rule.
                result.Add(r);
                continue;
            }

            var key = (r.ProviderId, r.ProviderKey ?? string.Empty);
            if (!byProviderKey.TryGetValue(key, out var incumbent) ||
                IsStronger(r, incumbent))
            {
                byProviderKey[key] = r;
            }
        }

        // Provider-level pass: drop the "Not matched" placeholder when the provider has a primary
        // row; otherwise keep exactly one placeholder (deterministic: smallest Id).
        foreach (var grp in byProviderKey.Values.GroupBy(r => r.ProviderId))
        {
            var primary = grp
                .Where(r => r.MappingStatus is SeriesMappingStatus.AutoMatched
                    or SeriesMappingStatus.UserConfirmed
                    or SeriesMappingStatus.ManualIgnore
                    or SeriesMappingStatus.ForeverIgnored)
                .ToList();
            if (primary.Count > 0)
            {
                foreach (var r in primary) result.Add(r);
                continue; // placeholder suppressed
            }

            var unmatched = grp
                .Where(r => r.MappingStatus == SeriesMappingStatus.Unmatched)
                .OrderBy(r => r.Id)
                .ToList();
            var others = grp
                .Where(r => r.MappingStatus != SeriesMappingStatus.Unmatched)
                .ToList();

            if (unmatched.Count > 0) result.Add(unmatched[0]);
            result.AddRange(others);
        }

        return result;
    }

    private static ContributionMetadataEntity PickStrongest(IEnumerable<ContributionMetadataEntity> rows)
    {
        ContributionMetadataEntity? best = null;
        foreach (var r in rows)
        {
            if (best == null || IsStronger(r, best))
                best = r;
        }
        return best!;
    }

    private static bool IsStronger(ContributionMetadataEntity candidate, ContributionMetadataEntity incumbent)
    {
        int cs = StatusPriority(candidate.MappingStatus);
        int isp = StatusPriority(incumbent.MappingStatus);
        if (cs != isp) return cs > isp;
        if (candidate.LinkedDate != incumbent.LinkedDate)
            return (candidate.LinkedDate ?? DateTime.MinValue) > (incumbent.LinkedDate ?? DateTime.MinValue);
        return candidate.Id.CompareTo(incumbent.Id) < 0;
    }
}
