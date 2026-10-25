using Microsoft.EntityFrameworkCore;
using RensaioBackend.Models.Database;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// Provider-identity grouping reconciliation for the contribution graph (partial of
/// <see cref="ContributionMappingService"/>).
///
/// Series membership is a DERIVED equivalence relation over source entries
/// (<c>ContributionSeriesEntity</c>): two entries belong to the same series iff they share an
/// own-title id (<c>Data.TitleId</c>) or their series share a POSITIVE provider anchor
/// (a metadata row with MappingStatus in { AutoMatched, UserConfirmed } and a non-empty
/// ProviderKey). Blocked / ForeverIgnored / ManualIgnore / Unmatched rows are NEVER anchors,
/// so blocking the link that joined two series makes the next pass separate them again — no
/// persisted exclusion record is needed.
/// </summary>
public sealed partial class ContributionMappingService
{
    /// <summary>
    /// Recomputes series grouping from current state and applies merges/splits. A series whose
    /// entries fall into more than one component is split; a component spanning more than one
    /// series is merged. Idempotent; loops to a fixed point under the shared contributor.db gate.
    /// Returns the number of merge/split operations applied.
    /// </summary>
    public async Task<int> ReconcileSeriesGroupingAsync(CancellationToken token = default)
    {
        using var handle = await _gate.LockAsync(token).ConfigureAwait(false);

        // Detach whatever the preceding scan/action left tracked so reconciliation starts from a
        // clean identity map. See ScanAllMappingsAsync for why the shared scoped DbContext must not
        // carry tracked rows into the next unit of work (identity-map key collision on Attach/Remove).
        _contributorDb.ChangeTracker.Clear();

        // BATCH PERFORMANCE: reconciliation runs several passes over many rows against one scoped
        // context; suppress the per-call whole-tracker DetectChanges (SaveChanges forces one pass).
        using var autoDetect = _contributorDb.SuppressAutoDetectChanges();

        // C: collapse any pre-existing same-key duplicates first (Blocked exempt).
        await ConsolidateProviderRowsAsync(token).ConfigureAwait(false);

        int total = 0;
        // Cycle guard: a pass that reproduces a previously seen structural state is oscillating
        // (merge -> split -> merge ...). Stop instead of churning to the 8-pass cap; the invariant
        // above should prevent this, but a persisted edge case must never spin forever.
        var seenSignatures = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 8; i++)
        {
            int changed = await ReconcileSeriesGroupingOnceAsync(token).ConfigureAwait(false);
            total += changed;
            // Each pass persists its own merges/splits and re-queries everything on the next pass,
            // so detach the pass's rows: otherwise tracked metadata instances accumulate across up
            // to 8 passes and collide with the next pass's AsNoTracking snapshots.
            _contributorDb.ChangeTracker.Clear();
            if (changed == 0) break;

            var signature = await ComputeGroupingSignatureAsync(token).ConfigureAwait(false);
            if (!seenSignatures.Add(signature))
            {
                _logger.LogWarning(
                    "Contribution grouping reconciliation did not converge after {Pass} pass(es); " +
                    "stopped to avoid oscillating merges/splits on a non-progressing graph state.",
                    i + 1);
                break;
            }
        }

        if (total > 0)
            _logger.LogInformation("Contribution grouping reconciliation applied {Count} merge/split operation(s).", total);
        return total;
    }

    /// <summary>
    /// Structural fingerprint of the current grouping state: the (series -> entry) assignment plus
    /// the live positive anchors. Used by the reconcile's cycle guard to detect a merge/split that
    /// keeps flip-flopping rather than converging.
    /// </summary>
    private async Task<string> ComputeGroupingSignatureAsync(CancellationToken token)
    {
        var entries = await _contributorDb.SeriesEntries
            .Where(s => s.Version != VersionLogicalDelete && s.Version != VersionUploadedDelete)
            .Select(s => new { s.SeriesId, s.Id })
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        var anchors = await _contributorDb.Metadata
            .Where(m => m.Version != VersionLogicalDelete && m.Version != VersionUploadedDelete
                && (m.MappingStatus == SeriesMappingStatus.AutoMatched
                    || m.MappingStatus == SeriesMappingStatus.UserConfirmed)
                && m.ProviderKey != null && m.ProviderKey != "")
            .Select(m => new { m.ProviderId, m.ProviderKey, m.SeriesId })
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        var sb = new System.Text.StringBuilder();
        foreach (var e in entries.OrderBy(x => x.SeriesId).ThenBy(x => x.Id))
            sb.Append(e.SeriesId).Append(':').Append(e.Id).Append(';');
        sb.Append('|');
        foreach (var a in anchors
                     .Select(x => (x.ProviderId, Key: x.ProviderKey!.Trim().ToLowerInvariant(), x.SeriesId))
                     .OrderBy(x => x.ProviderId).ThenBy(x => x.Key).ThenBy(x => x.SeriesId))
            sb.Append(a.ProviderId).Append('|').Append(a.Key).Append('|').Append(a.SeriesId).Append(';');
        return sb.ToString();
    }

    private async Task<int> ReconcileSeriesGroupingOnceAsync(CancellationToken token)
    {
        var entries = await _contributorDb.SeriesEntries
            .Where(s => s.Version != VersionLogicalDelete && s.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        if (entries.Count <= 1) return 0;

        // POSITIVE anchors only: AutoMatched/UserConfirmed, non-empty key. Blocked/ignored never link.
        var anchors = await _contributorDb.Metadata
            .Where(m => m.Version != VersionLogicalDelete && m.Version != VersionUploadedDelete
                && (m.MappingStatus == SeriesMappingStatus.AutoMatched
                    || m.MappingStatus == SeriesMappingStatus.UserConfirmed)
                && m.ProviderKey != null && m.ProviderKey != "")
            .AsNoTracking()
            .Select(m => new { m.ProviderId, m.ProviderKey, m.SeriesId })
            .ToListAsync(token).ConfigureAwait(false);

        var uf = new EntryUnionFind(entries.Select(e => e.Id));
        var entriesBySeries = entries.GroupBy(e => e.SeriesId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Id).ToList());

        // 1. Union by own-title identity (the source's own title).
        foreach (var grp in entries
                     .Where(e => e.Data != null && e.Data.TitleId != Guid.Empty)
                     .GroupBy(e => e.Data.TitleId))
        {
            uf.UnionAll(grp.Select(e => e.Id));
        }

        // 2. Union the entries of every series that carries the same positive anchor. An anchor
        //    held by a SINGLE series must ALSO unify that series' own entries: MergeSeriesAsync
        //    folds anchor-sharing series into one, after which the anchor is carried by ONE series.
        //    Without the self-union the survivor's entries fall into separate components and the
        //    very next pass splits them apart again — the merge/split oscillation behind the
        //    "Split contribution series ..." storm. Sharing an anchor means "same series" (K=1),
        //    so the escape hatch for a wrong anchor is to BLOCK it (a Blocked row is not an anchor).
        var anchorToSeries = new Dictionary<(int ProviderId, string Key), HashSet<Guid>>();
        foreach (var a in anchors)
        {
            var key = (a.ProviderId, a.ProviderKey!.Trim().ToLowerInvariant());
            if (!anchorToSeries.TryGetValue(key, out var set))
                anchorToSeries[key] = set = new HashSet<Guid>();
            set.Add(a.SeriesId);
        }

        foreach (var kv in anchorToSeries)
        {
            var ids = kv.Value.Where(entriesBySeries.ContainsKey)
                .SelectMany(sid => entriesBySeries[sid]).ToList();
            if (ids.Count > 1) uf.UnionAll(ids);
        }

        // The set of components each series' entries fall into.
        var seriesComponents = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var e in entries)
        {
            if (!seriesComponents.TryGetValue(e.SeriesId, out var set))
                seriesComponents[e.SeriesId] = set = new HashSet<Guid>();
            set.Add(uf.Find(e.Id));
        }

        // 3a. SPLIT: a series whose entries fall into more than one component.
        int changes = 0;
        foreach (var (seriesId, roots) in seriesComponents)
        {
            if (roots.Count <= 1) continue;
            if (!entriesBySeries.TryGetValue(seriesId, out var sEntries)) continue;
            var groups = sEntries.GroupBy(id => uf.Find(id)).Select(g => g.ToList()).ToList();
            if (groups.Count <= 1) continue;

            await SplitSeriesByGroupsAsync(seriesId, groups, token).ConfigureAwait(false);
            changes++;
        }

        // Topology changed; re-query on the next iteration before considering merges.
        if (changes > 0) return changes;

        // 3b. MERGE: a component spanning more than one series.
        var componentSeries = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var e in entries)
        {
            var root = uf.Find(e.Id);
            if (!componentSeries.TryGetValue(root, out var set))
                componentSeries[root] = set = new HashSet<Guid>();
            set.Add(e.SeriesId);
        }

        var mergeGroups = new List<(Guid Survivor, List<Guid> Members)>();
        foreach (var (_, seriesIds) in componentSeries)
        {
            if (seriesIds.Count <= 1) continue;

            // Deterministic survivor: most live entries, then smallest id (idempotent).
            var survivor = seriesIds
                .OrderByDescending(sid => entriesBySeries.TryGetValue(sid, out var l) ? l.Count : 0)
                .ThenBy(sid => sid)
                .First();

            mergeGroups.Add((survivor, seriesIds.ToList()));
        }

        if (mergeGroups.Count == 0)
            return changes;

        // BATCH: the merges in a pass target DISJOINT components, so their changes can accumulate
        // in the tracker and be committed in CHUNKS instead of one commit (fsync) per merge — the
        // dominant cost of a large reconcile (thousands of merges → thousands of commits). The
        // chunk size stays well under the SQLite bound-parameter limit, since SaveChanges's
        // verification-invalidation hook expands the dirty series ids into an IN(...) clause. The
        // provider-row consolidation + derived-flag recompute are graph-wide and run ONCE after
        // the pass, over the now-consistent graph.
        const int mergesPerCommit = 200;
        int pending = 0;
        foreach (var (survivor, members) in mergeGroups)
        {
            await MergeSeriesAsync(survivor, members, token, deferSettle: true).ConfigureAwait(false);
            changes++;

            if (++pending >= mergesPerCommit)
            {
                await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
                _contributorDb.ChangeTracker.Clear();
                pending = 0;
            }
        }

        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
        _contributorDb.ChangeTracker.Clear();

        await ConsolidateProviderRowsAsync(token).ConfigureAwait(false);
        await _graphRepair.RecomputeSeriesMetadataFlagsAsync(token).ConfigureAwait(false);

        return changes;
    }

    /// <summary>Minimal union-find over source-entry ids for the grouping reconciliation.</summary>
    private sealed class EntryUnionFind
    {
        private readonly Dictionary<Guid, Guid> _parent;

        public EntryUnionFind(IEnumerable<Guid> ids)
        {
            _parent = ids.ToDictionary(id => id, id => id);
        }

        public Guid Find(Guid id)
        {
            if (!_parent.TryGetValue(id, out var parent)) return id;
            if (parent == id) return id;
            var root = Find(parent);
            _parent[id] = root;
            return root;
        }

        public void UnionAll(IEnumerable<Guid> ids)
        {
            Guid? first = null;
            foreach (var id in ids)
            {
                if (first == null) { first = id; continue; }
                var ra = Find(first.Value);
                var rb = Find(id);
                if (ra != rb) _parent[rb] = ra;
            }
        }
    }
}
