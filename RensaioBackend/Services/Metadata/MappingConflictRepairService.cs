using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RensaioBackend.Data;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Scrobbling;
using RensaioBackend.Services.Series;

namespace RensaioBackend.Services.Metadata;

/// <summary>
/// Detects and repairs wrong auto-linkages between local series and metadata providers.
///
/// The problem (see plans/mapping-conflict-repair-plan.md): series A auto-links to provider
/// series B (wrong series, same/similar title). Because B's provider entry already declares
/// many LinkedSitesIds + titles, B then links back to A's ids and both series end up claiming
/// the same (provider, externalId) — the wrong link self-reinforces.
///
/// This service:
///  1. Builds a claims map: every SeriesMapping row (provider + external id) and every
///     declared cross-link (LinkedSitesIds) claims a (site, id) for its local series.
///  2. Union-finds conflict components: series sharing any claimed (site, id).
///  3. For each contested (site, id) decides the deterministic OWNER
///     (UserConfirmed > UserRole > best TitleMatcher score > earliest LinkedDate > smallest
///     SeriesId). User decisions (UserConfirmed / ForeverIgnored / hard Blocked) are never
///     auto-modified; a conflict between two UserConfirmed rows is reported only.
///  4. Auto-blocks every loser (Blocked row scoped to the contested id), clears the loser's
///     live mapping row + seeded columns so it stops propagating the wrong ids.
///  5. Recalculates links for every repaired series, restricted to the CONTESTED provider(s)
///     only (blocked ids are excluded by the matcher) via
///     <see cref="MetadataLinkEngine.LinkSeriesAsync"/> — the repair never re-scans the full
///     provider set, and unmatched non-conflict series are never touched (SRP: repair repairs).
///
/// Safe to run repeatedly: idempotent, best-effort, never throws into callers.
/// </summary>
public sealed class MappingConflictRepairService
{
    private readonly AppDbContext _db;
    private readonly MetadataLinkEngine _linkEngine;
    private readonly SeriesStateService _seriesStateService;
    private readonly ILogger<MappingConflictRepairService> _logger;

    public MappingConflictRepairService(
        AppDbContext db,
        MetadataLinkEngine linkEngine,
        SeriesStateService seriesStateService,
        ILogger<MappingConflictRepairService> logger)
    {
        _db = db;
        _linkEngine = linkEngine;
        _seriesStateService = seriesStateService;
        _logger = logger;
    }

    /// <summary>
    /// Runs one full repair pass. Detect conflicts, decide owners, auto-block losers, clear
    /// loser mappings and recalculate links for every affected series. Never throws into the
    /// caller (errors are logged; best-effort).
    /// </summary>
    public async Task<MappingRepairResultDto> RepairAsync(CancellationToken token = default)
    {
        var result = new MappingRepairResultDto();
        try
        {
            // 1. Load everything once.
            var seriesList = await _db.Series
                .Include(s => s.Sources)
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);
            var allMappings = await _db.SeriesMappings
                .Where(m => m.SeriesId != null)
                .AsNoTracking()
                .ToListAsync(token).ConfigureAwait(false);
            var seriesById = seriesList.ToDictionary(s => s.Id);

            if (seriesList.Count == 0 || allMappings.Count == 0) return result;

            // 1.5 Startup cleanup of BOGUS hard-blocks: a SeriesMapping row with status Blocked but
            //     an EMPTY (or "0") external id is a meaningless block — there is no id to refuse,
            //     it just hard-disables the provider for this series. Those rows are downgraded to
            //     Unmatched and their series are re-matched afterwards, so a stale empty-key block
            //     (e.g. created by the Block endpoint before an id was ever chosen) never silently
            //     hard-blocks a provider forever. A real id-scoped block (or a repair-sealed block)
            //     is left untouched.
            var bogusBlockSeries = new HashSet<Guid>();
            var bogusBlockRows = allMappings
                .Where(m => m.MappingStatus == SeriesMappingStatus.Blocked
                    && (string.IsNullOrWhiteSpace(m.ExternalSeriesId) || m.ExternalSeriesId == "0"))
                .ToList();
            if (bogusBlockRows.Count > 0)
            {
                foreach (var row in bogusBlockRows)
                {
                    var tracked = await _db.SeriesMappings
                        .FirstOrDefaultAsync(m => m.Id == row.Id, token)
                        .ConfigureAwait(false);
                    if (tracked == null) continue;
                    tracked.MappingStatus = SeriesMappingStatus.Unmatched;
                    tracked.ExternalSeriesId = string.Empty;
                    tracked.LinkedDate = null;
                    tracked.UpdateDate = DateTime.UtcNow;
                    if (row.SeriesId is Guid sid) bogusBlockSeries.Add(sid);
                }
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
                _logger.LogInformation("Mapping repair: downgraded {Count} empty/0-key Blocked rows to Unmatched (re-evaluating {Series} series).",
                    bogusBlockRows.Count, bogusBlockSeries.Count);
            }

            // 2. Claims map: (site, id) -> set of series ids claiming it. Ids are normalized to
            //    lower-case so claims join case-insensitively (the matcher's SiteEdgeComparer).
            var claimMap = new Dictionary<(ExternalSeriesProvider, string), HashSet<Guid>>();
            var bySeries = allMappings
                .Where(m => m.SeriesId != null && !string.IsNullOrWhiteSpace(m.ExternalSeriesId))
                .GroupBy(m => m.SeriesId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var (seriesId, mappings) in bySeries)
            {
                foreach (var m in mappings)
                {
                    if (m.MappingStatus is not (SeriesMappingStatus.AutoMatched or SeriesMappingStatus.UserConfirmed))
                        continue; // refusals don't claim
                    AddClaim(claimMap, (m.Provider, m.ExternalSeriesId.ToLowerInvariant()), seriesId);
                    foreach (var linked in m.LinkedSitesIds)
                    {
                        var (site, id) = SplitSiteId(linked);
                        if (site.HasValue && !string.IsNullOrWhiteSpace(id))
                            AddClaim(claimMap, (site.Value, id.ToLowerInvariant()), seriesId);
                    }
                }
            }

            // Category-aware exception: two series that share a (site,id) are NOT a conflict when
            // they are the same/similar title AND live in DIFFERENT first path segments (different
            // categories — categorized folders on). They legitimately coexist as distinct series.
            // Remove those cross-category duplicate series from the claim maps so they neither
            // union into the same component nor get auto-blocked.
            var titleNorm = new Dictionary<Guid, string?>();
            foreach (var s in seriesList)
                titleNorm[s.Id] = NormalizeTitle(s.Title);
            var toRemove = new HashSet<(ExternalSeriesProvider, string)>();
            foreach (var (key, claimants) in claimMap)
            {
                var list = claimants.ToList();
                for (int i = 0; i < list.Count; i++)
                {
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        Guid a = list[i], b = list[j];
                        if (AreSameTitleDifferentCategory(a, b, seriesById, titleNorm))
                        {
                            // The two series are legitimately distinct — drop this claim for both.
                            toRemove.Add(key);
                        }
                    }
                }
            }
            foreach (var key in toRemove)
                claimMap.Remove(key);

            // 3. Union-find components over series sharing a claim.
            var uf = new DisjointSet(seriesList.Select(s => s.Id).ToList());
            foreach (var (_, claimants) in claimMap)
            {
                var list = claimants.ToList();
                for (int i = 1; i < list.Count; i++)
                    uf.Union(list[0], list[i]);
            }
            var components = new Dictionary<Guid, List<Guid>>();
            foreach (var id in seriesList.Select(s => s.Id))
            {
                var root = uf.Find(id);
                if (!components.TryGetValue(root, out var list))
                    components[root] = list = [];
                list.Add(id);
            }

            // 4. Per component: decide owners of every contested (site, id); auto-block losers.
            //    repairedProviders tracks, per repaired series, ONLY the contested provider(s) so
            //    the rematch step stays surgical (a conflict on one site never re-scans the rest).
            var repairedSeries = new HashSet<Guid>();
            var repairedProviders = new Dictionary<Guid, HashSet<ExternalSeriesProvider>>();
            var conflictRows = new List<MappingRepairConflictRowDto>();

            foreach (var component in components.Values.Where(c => c.Count >= 2))
            {
                // 4a. Score each series' local titles once (per component) — used for owner decisions.
                var scores = new Dictionary<Guid, List<string>>();
                foreach (var sid in component)
                {
                    if (seriesById.TryGetValue(sid, out var s))
                        scores[sid] = BuildTitleCandidates(s);
                }

                var contested = new HashSet<(ExternalSeriesProvider Site, string Id)>();
                foreach (var sid in component)
                {
                    if (!bySeries.TryGetValue(sid, out var mappings)) continue;
                    foreach (var m in mappings)
                    {
                        if (m.MappingStatus is not (SeriesMappingStatus.AutoMatched or SeriesMappingStatus.UserConfirmed))
                            continue;
                        var key = (m.Provider, m.ExternalSeriesId.ToLowerInvariant());
                        if (HasMultipleClaimants(claimMap, key, component))
                            contested.Add(key);
                        foreach (var linked in m.LinkedSitesIds)
                        {
                            var (site, id) = SplitSiteId(linked);
                            if (site.HasValue && !string.IsNullOrWhiteSpace(id))
                            {
                                var k2 = (site.Value, id.ToLowerInvariant());
                                if (HasMultipleClaimants(claimMap, k2, component))
                                    contested.Add(k2);
                            }
                        }
                    }
                }

                foreach (var key in contested)
                {
                    var (provider, externalId) = key;
                    var claimants = claimMap.TryGetValue(key, out var set)
                        ? set.Where(component.Contains).ToList()
                        : [];
                    if (claimants.Count < 2) continue;

                    var externalTitle = claimants
                        .Select(sid => bySeries.GetValueOrDefault(sid)?.FirstOrDefault(m => m.Provider == provider
                            && string.Equals(m.ExternalSeriesId, externalId, StringComparison.OrdinalIgnoreCase))?.ExternalSeriesTitle)
                        .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

                    // The deterministic owner.
                    var ownerId = PickOwner(claimants, key, bySeries, scores, externalTitle);
                    if (!ownerId.HasValue)
                    {
                        // Conflict not auto-resolvable (e.g. both UserConfirmed): report and skip.
                        result.SkippedUserConfirmedConflicts = true;
                        conflictRows.Add(new MappingRepairConflictRowDto
                        {
                            Provider = provider,
                            ExternalSeriesId = externalId,
                            ExternalSeriesTitle = externalTitle,
                            OwnerSeriesId = claimants.OrderBy(id => id).First(),
                            OwnerSeriesTitle = seriesById.GetValueOrDefault(claimants.OrderBy(id => id).First())?.Title,
                            BlockedSeriesIds = []
                        });
                        continue;
                    }

                    var losers = claimants.Where(id => id != ownerId.Value).ToList();
                    var anyLoserChanged = false;
                    foreach (var loserId in losers)
                    {
                        if (await AutoBlockAndClearAsync(loserId, provider, externalId, token).ConfigureAwait(false))
                        {
                            result.BlocksCreated++;
                            result.MappingsCleared++;
                            AddRepaired(repairedSeries, repairedProviders, loserId, provider); // only re-evaluate when the row actually changed
                            anyLoserChanged = true;
                        }
                        // else: user-owned loser (UserConfirmed / ForeverIgnored / existing Blocked)
                        // — left untouched and NOT re-scanned; it will not converge automatically.
                    }
                    // Only re-evaluate the owner when this pass actually modified a loser. If every
                    // loser was already user-decided (nothing changed), skip the expensive re-scan —
                    // the next run with no modifications is a cheap no-op.
                    if (anyLoserChanged)
                        AddRepaired(repairedSeries, repairedProviders, ownerId.Value, provider);

                    conflictRows.Add(new MappingRepairConflictRowDto
                    {
                        Provider = provider,
                        ExternalSeriesId = externalId,
                        ExternalSeriesTitle = externalTitle,
                        OwnerSeriesId = ownerId.Value,
                        OwnerSeriesTitle = seriesById.GetValueOrDefault(ownerId.Value)?.Title,
                        BlockedSeriesIds = losers
                    });
                }

                result.ConflictsDetected += contested.Count;
            }

            // 4b. PERSIST the blocks + clearings BEFORE recalculation — the link engine reads
            //     the current row state from the DB to build seeds and blockedIds.
            if (result.BlocksCreated > 0 || result.MappingsCleared > 0)
            {
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
            }

            // 5. Rematch ONLY the contested provider(s) for the repaired series (blocked ids are
            //    excluded by the matcher). SRP: the repair fixes conflicts; it never re-scans the
            //    full provider set for a series, and unmatched non-conflict series are untouched.
            //    With no conflicts the loop body is empty → RepairAsync is a genuine no-op.
            foreach (var sid in repairedSeries)
            {
                if (token.IsCancellationRequested) break;
                if (!repairedProviders.TryGetValue(sid, out var contested) || contested.Count == 0) continue;
                try
                {
                    await _linkEngine.LinkSeriesAsync(sid, token, contested).ConfigureAwait(false);
                    await _seriesStateService.SyncToRensaioJsonAsync(sid, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Mapping repair: recalc failed for series {SeriesId}", sid);
                }
            }

            // 5.5 Re-match series whose empty/0-key Blocked rows were just downgraded to Unmatched.
            //     Those providers had NO real id to refuse, so they are back in play — scan the
            //     full enabled set so a proper link (or ForeverIgnored via CanSearchSeries) can form.
            foreach (var sid in bogusBlockSeries)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    var enabled = await _linkEngine.GetEnabledProviderSetAsync(token).ConfigureAwait(false);
                    await _linkEngine.LinkSeriesAsync(sid, token, enabled).ConfigureAwait(false);
                    await _seriesStateService.SyncToRensaioJsonAsync(sid, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Mapping repair: re-match after empty-key block downgrade failed for series {SeriesId}", sid);
                }
            }

            result.Conflicts = conflictRows;
            result.RepairedSeriesIds = repairedSeries.OrderBy(id => id).ToList();
            result.SeriesRepaired = repairedSeries.Count;

            if (result.ConflictsDetected > 0)
            {
                // "Series" reports how many series were actually REPAIRED (auto-blocked + rematched),
                // not how many series touched a contested claim. Conflicts that could not be
                // auto-resolved (e.g. both sides UserConfirmed, or every loser is already a user
                // decision) are still detected and reported but repair 0 series — that scenario is
                // normal and expected, so the log line says so instead of printing a confusing
                // "N conflicts across 0 series".
                if (result.SeriesRepaired > 0)
                {
                    _logger.LogInformation(
                        "Mapping repair: {Conflicts} conflicts across {Series} repaired series, {Blocks} blocks created",
                        result.ConflictsDetected, result.SeriesRepaired, result.BlocksCreated);
                }
                else
                {
                    _logger.LogInformation(
                        "Mapping repair: {Conflicts} conflicts detected, 0 series repaired (every conflict was " +
                        "already user-decided or not auto-resolvable), 0 blocks created",
                        result.ConflictsDetected);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mapping conflict repair failed");
        }
        return result;
    }

    // ── Private ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts the loser series' live mapping row for (seriesId, provider) into an id-scoped
    /// Blocked row (and clears the seeded columns), or creates one when no row exists. Returns
    /// <c>true</c> when a write was staged; <c>false</c> when a user decision (UserConfirmed /
    /// ForeverIgnored / existing Blocked) was left untouched.
    /// </summary>
    private async Task<bool> AutoBlockAndClearAsync(Guid seriesId, ExternalSeriesProvider provider,
        string externalId, CancellationToken token)
    {
        // Guard against the bogus-key bug: never create/convert a Blocked row for a meaningless id
        // ("0"/empty) — that would hard-disable the provider with nothing to refuse.
        if (string.IsNullOrWhiteSpace(externalId) || externalId == "0")
            return false;

        var now = DateTime.UtcNow;
        var row = await _db.SeriesMappings
            .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == provider, token)
            .ConfigureAwait(false);

        // User decisions are sacred: this pass never downgrades a UserConfirmed / ForeverIgnored,
        // and never overwrites an existing Blocked row (which can only carry ONE id per
        // (series, provider) — the unique index IX_SeriesMapping_SeriesId_Provider). The repair
        // found this series contested; the user's existing decision stands.
        if (row != null && (row.MappingStatus == SeriesMappingStatus.Blocked
            || row.MappingStatus == SeriesMappingStatus.UserConfirmed
            || row.MappingStatus == SeriesMappingStatus.ForeverIgnored))
        {
            return false;
        }

        // Sealed sentinel: a REPAIR-created block is recorded with this LinkedDate so the scanner
        // treats the whole provider as settled for this series (no more re-scanning every pass).
        // The user can still unblock later — unblock rewrites LinkedDate to a real timestamp.
        if (row == null)
        {
            _db.SeriesMappings.Add(new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                Provider = provider,
                ExternalSeriesId = externalId,      // id-scoped block: this exact id never links again
                MappingStatus = SeriesMappingStatus.Blocked,
                LinkedDate = SeriesMappingEntity.AutoSealedSentinel,
                UpdateDate = now,
                UserRole = UserLevel.User
            });
            return true;
        }

        // row is AutoMatched / Unmatched / ManualIgnore → convert the LIVE row to a
        // Blocked id-scoped row and clear the seeded columns so it stops propagating.
        row.ExternalSeriesId = externalId;
        row.MappingStatus = SeriesMappingStatus.Blocked;
        row.LinkedDate = SeriesMappingEntity.AutoSealedSentinel;
        row.UpdateDate = now;
        row.LinkedSitesIds = [];
        row.AlternativeTitles = [];
        row.SeriesCoverUrl = null;
        row.MetaData = null;
        row.ExternalSeriesTitle = null;
        return true;
    }

    private Guid? PickOwner(
        List<Guid> claimants,
        (ExternalSeriesProvider Site, string Id) key,
        Dictionary<Guid, List<SeriesMappingEntity>> bySeries,
        Dictionary<Guid, List<string>> scores,
        string? externalTitle)
    {
        // 1. UserConfirmed always wins.
        var confirmed = claimants.Where(sid => bySeries.GetValueOrDefault(sid)?.Any(m =>
            m.Provider == key.Site
            && string.Equals(m.ExternalSeriesId, key.Id, StringComparison.OrdinalIgnoreCase)
            && m.MappingStatus == SeriesMappingStatus.UserConfirmed) == true).ToList();
        var pool = confirmed.Count > 0 ? confirmed : claimants;
        if (confirmed.Count > 1)
        {
            // Two UserConfirmed rows for the same id: never auto-resolve — higher role wins;
            // if roles tie, report (return null → skipped).
            var topRole = pool.Max(sid => RoleOf(bySeries, sid, key));
            var top = pool.Where(sid => RoleOf(bySeries, sid, key) == topRole).ToList();
            return top.Count == 1 ? top[0] : null;
        }

        // 2. Higher UserRole wins.
        var bestRole = pool.Max(sid => RoleOf(bySeries, sid, key));
        var byRole = pool.Where(sid => RoleOf(bySeries, sid, key) == bestRole).ToList();
        if (byRole.Count == 1) return byRole[0];

        // 3. Best title score against the external title (when available).
        if (!string.IsNullOrWhiteSpace(externalTitle))
        {
            var scored = byRole
                .Select(sid => (Sid: sid, Score: BestScore(scores.GetValueOrDefault(sid), externalTitle)))
                .OrderByDescending(x => x.Score)
                .ToList();
            if (scored.Count > 1 && scored[0].Score > scored[1].Score) return scored[0].Sid;
            if (scored.Count == 1) return scored[0].Sid;
        }

        // 4. Earliest LinkedDate (first-claimed wins).
        var earliest = byRole
            .Select(sid => (Sid: sid, Date: bySeries.GetValueOrDefault(sid)
                ?.FirstOrDefault(m => m.Provider == key.Site
                    && string.Equals(m.ExternalSeriesId, key.Id, StringComparison.OrdinalIgnoreCase))?.LinkedDate))
            .OrderBy(x => x.Date)
            .ToList();
        if (earliest.Count > 1 && earliest[0].Date != earliest[1].Date) return earliest[0].Sid;

        // 5. Deterministic tie-break: smallest series id.
        return byRole.OrderBy(sid => sid).First();
    }

    private static int RoleOf(Dictionary<Guid, List<SeriesMappingEntity>> bySeries, Guid sid,
        (ExternalSeriesProvider Site, string Id) key)
        => bySeries.GetValueOrDefault(sid)
            ?.FirstOrDefault(m => m.Provider == key.Site
                && string.Equals(m.ExternalSeriesId, key.Id, StringComparison.OrdinalIgnoreCase))?.UserRole is { } role
            ? (int)role
            : 0;

    private int BestScore(List<string>? localTitles, string? externalTitle)
    {
        if (localTitles is not { Count: > 0 } || string.IsNullOrWhiteSpace(externalTitle)) return 0;
        var scored = TitleMatcher.MatchTitles(localTitles, new[] { (externalTitle, (string)("__ext__")) }, 0);
        return scored.Length > 0 ? scored[0].Percentage : 0;
    }

    private static List<string> BuildTitleCandidates(SeriesEntity series)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(series.Title)) list.Add(series.Title);
        foreach (var s in series.Sources)
            if (!string.IsNullOrWhiteSpace(s.Title)
                && !list.Contains(s.Title, StringComparer.OrdinalIgnoreCase))
                list.Add(s.Title);
        return list;
    }

    private static void AddClaim(Dictionary<(ExternalSeriesProvider, string), HashSet<Guid>> map,
        (ExternalSeriesProvider Site, string Id) claim, Guid seriesId)
    {
        if (!map.TryGetValue(claim, out var set))
            map[claim] = set = [];
        set.Add(seriesId);
    }

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> are treated as the SAME series
    /// across different categories: same normalized title AND different first storage-path segment
    /// (the category when categorized folders are enabled). In that case a shared (site,id) claim
    /// is legitimate, not a conflict.
    /// </summary>
    private static bool AreSameTitleDifferentCategory(
        Guid a, Guid b,
        Dictionary<Guid, SeriesEntity> seriesById,
        Dictionary<Guid, string?> titleNorm)
    {
        string? ta = titleNorm.GetValueOrDefault(a);
        string? tb = titleNorm.GetValueOrDefault(b);
        if (string.IsNullOrWhiteSpace(ta) || string.IsNullOrWhiteSpace(tb))
            return false;
        if (!string.Equals(ta, tb, StringComparison.OrdinalIgnoreCase))
            return false;

        string? sa = seriesById.GetValueOrDefault(a)?.StoragePath;
        string? sb = seriesById.GetValueOrDefault(b)?.StoragePath;
        if (string.IsNullOrWhiteSpace(sa) || string.IsNullOrWhiteSpace(sb))
            return false;

        string ca = FirstPathSegment(sa);
        string cb = FirstPathSegment(sb);
        return !string.IsNullOrWhiteSpace(ca)
            && !string.IsNullOrWhiteSpace(cb)
            && !string.Equals(ca, cb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>First path segment of a normalized (relative) storage path, or "" when absent.</summary>
    private static string FirstPathSegment(string path)
    {
        string p = path.Replace('\\', '/').Trim('/');
        int idx = p.IndexOf('/');
        return idx > 0 ? p[..idx] : p;
    }

    /// <summary>Stable normalized form of a title used for comparison (lowercase, collapsed whitespace).</summary>
    private static string? NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        return string.Join(' ', title.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }

    private static bool HasMultipleClaimants(
        Dictionary<(ExternalSeriesProvider, string), HashSet<Guid>> map,
        (ExternalSeriesProvider, string) key,
        List<Guid> component)
        => map.TryGetValue(key, out var set) && set.Count(id => component.Contains(id)) >= 2;

    /// <summary>
    /// Registers a repaired series and the provider whose contested claim caused the repair.
    /// The rematch phase uses ONLY these providers, so a conflict on one site never triggers
    /// searches on the other metadata sites for the same series (SRP: repair, not scan).
    /// </summary>
    private static void AddRepaired(HashSet<Guid> repairedSeries,
        Dictionary<Guid, HashSet<ExternalSeriesProvider>> repairedProviders,
        Guid sid, ExternalSeriesProvider provider)
    {
        repairedSeries.Add(sid);
        if (!repairedProviders.TryGetValue(sid, out var set))
            repairedProviders[sid] = set = [];
        set.Add(provider);
    }

    private static (ExternalSeriesProvider? Site, string? Id) SplitSiteId(string linked)
    {
        var idx = linked.IndexOf(':');
        if (idx <= 0 || idx == linked.Length - 1) return (null, null);
        var slug = linked[..idx];
        var id = linked[(idx + 1)..];
        // Placeholder ids (e.g. AniList idMal=0 -> "myanimelist:0") are not real links and
        // must not create a claim, otherwise unrelated series get grouped into one conflict.
        if (long.TryParse(id.Trim(), out long numeric) && numeric <= 0) return (null, null);
        if (!Enum.TryParse<ExternalSeriesProvider>(slug, true, out var parsed))
        {
            parsed = slug.ToLowerInvariant() switch
            {
                "myanimelist" => ExternalSeriesProvider.MyAnimeList,
                "anilist" => ExternalSeriesProvider.AniList,
                "comicvine" => ExternalSeriesProvider.ComicVine,
                "kitsu" => ExternalSeriesProvider.Kitsu,
                "mangadex" => ExternalSeriesProvider.MangaDex,
                "mangabaka" => ExternalSeriesProvider.MangaBaka,
                "bgm" => ExternalSeriesProvider.Bangumi,
                "mangaupdates" => ExternalSeriesProvider.MangaUpdates,
                "gcd" => ExternalSeriesProvider.GrandComicsDatabase,
                "metron" => ExternalSeriesProvider.Metron,
                _ => (ExternalSeriesProvider)int.MaxValue
            };
            if (parsed > ExternalSeriesProvider.Metron) return (null, null);
        }
        return (parsed, id);
    }

    /// <summary>Minimal union-find over series ids.</summary>
    private sealed class DisjointSet
    {
        private readonly Dictionary<Guid, Guid> _parent;

        public DisjointSet(IEnumerable<Guid> ids)
        {
            _parent = ids.ToDictionary(id => id, id => id);
        }

        public Guid Find(Guid x)
        {
            Guid root = x;
            while (_parent.TryGetValue(root, out var p) && p != root)
                root = p;
            // path compression
            Guid cur = x;
            while (_parent.TryGetValue(cur, out var p) && p != cur)
            {
                _parent[cur] = root;
                cur = p;
            }
            return root;
        }

        public void Union(Guid a, Guid b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) _parent[rb] = ra;
        }
    }
}