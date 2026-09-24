using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RensaioBackend.Data;
using RensaioBackend.Models;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Jobs;
using RensaioBackend.Services.Metadata;
using RensaioBackend.Services.Scrobbling.Abstractions;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// Read/write service for the Contribution Mappings page (source scope). Reads and mutates the
/// local contribution database (<c>contributor.db</c>) as the source of truth.
///
/// Auto-merge semantics: sources whose normalized titles collide (they resolve to the same
/// <see cref="TitleEntity"/>) are grouped under one <see cref="MappingEntity"/>. All writes stamp
/// <c>Version = 0</c> (add/update) or <c>-1</c> (tombstone) so the existing
/// <see cref="ContributionUploadService"/> picks them up for cloud sync unchanged.
///
/// The cross-provider search/score/propagation algorithm is delegated to
/// <see cref="MetadataMatchCore"/> (shared with the Rensaio-DB External Mappings flow) — nothing
/// is copy-pasted.
/// </summary>
public sealed class ContributionMappingService
{
    private const int VersionAddOrUpdate = 0;
    private const int VersionLogicalDelete = -1;
    private const int VersionUploadedDelete = -3;

    // SignalR progress ids — distinct from the External Mappings engine's "metadata-link-*".
    public const string LinkAllJobId = "contribution-link-all";

    private readonly ContributionDbContext _contributorDb;
    private readonly AppDbContext _db;
    private readonly MetadataMatchCore _matchCore;
    private readonly JobHubReportService _hubReport;
    private readonly ILogger<ContributionMappingService> _logger;

    public ContributionMappingService(
        ContributionDbContext contributorDb,
        AppDbContext db,
        MetadataMatchCore matchCore,
        JobHubReportService hubReport,
        ILogger<ContributionMappingService> logger)
    {
        _contributorDb = contributorDb;
        _db = db;
        _matchCore = matchCore;
        _hubReport = hubReport;
        _logger = logger;
    }

    /// <summary>
    /// Lists contribution mappings grouped by mapping (source scope), paginated over mappings.
    /// filter=all|unmatched|blocked: `unmatched` keeps only mappings with at least one Unmatched
    /// (or expired TemporaryIgnored) provider row; `blocked` keeps only mappings with at least one
    /// Blocked provider row.
    /// </summary>
    public async Task<ContributionMappingsPageDto> ListMappingsAsync(
        string filter, int page, int pageSize, ExternalSeriesProvider? provider, SeriesMappingStatus? status,
        List<IExternalSeriesProvider> enabledProviders, CancellationToken token)
    {
        var isUnmatchedFilter = string.Equals(filter, "unmatched", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(filter);
        var isBlockedFilter = string.Equals(filter, "blocked", StringComparison.OrdinalIgnoreCase);
        var nowMinusOneMonth = DateTime.UtcNow.AddMonths(-1);

        var result = new ContributionMappingsPageDto
        {
            Page = page,
            PageSize = pageSize,
            Total = 0,
            ProviderMeta = ExternalMappingSupport.BuildProviderMeta(enabledProviders)
        };

        // Load the whole (small) contribution graph once and group in memory.
        var mappingTitles = await _contributorDb.MappingTitles
            .Include(mt => mt.Title)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        // Project LIVE rows only. Every positive expectation here was wrong: rows are stamped
        // -2 (Uploaded) once the upload path flips them, so a `Version >= 0` filter dropped the
        // series rows — and with them the group cover (Data.ThumbnailUrl) — for every mapping
        // after its first export. Tombstones are -1 (PendingDelete) and -3 (UploadedDelete);
        // everything else (-2 Uploaded, 0 pending) is a live row.
        var seriesRows = await _contributorDb.Series
            .Include(s => s.Source)
            .Where(s => s.Version != VersionLogicalDelete && s.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        var metadataRows = await _contributorDb.Metadata
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        var groupsByMapping = mappingTitles
            .GroupBy(mt => mt.MappingId)
            .OrderBy(g => g.FirstOrDefault(mt => mt.Title != null)?.Title?.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var groups = new List<ContributionMappingGroupDto>();

        foreach (var grp in groupsByMapping)
        {
            var mappingId = grp.Key;
            var titles = grp
                .Where(mt => mt.Title != null && !string.IsNullOrWhiteSpace(mt.Title.Title))
                .Select(mt => mt.Title!.Title!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sources = seriesRows
                .Where(s => s.MappingId == mappingId)
                .Select(s => ToSourceDto(s))
                .Where(s => s != null)
                .Cast<ContributionMappingSourceDto>()
                .ToList();

            var providerRows = new List<ExternalMappingsSeriesProviderDto>();
            var groupHasUnmatched = false;
            var groupHasBlocked = false;
            foreach (var m in metadataRows.Where(x => x.MappingId == mappingId))
            {
                if (provider.HasValue && (ExternalSeriesProvider)m.ProviderId != provider.Value) continue;

                var row = new ExternalMappingsSeriesProviderDto
                {
                    ProviderCoverUrl = null, // contribution metadata rows carry no cover in V1 schema
                    Provider = (ExternalSeriesProvider)m.ProviderId,
                    ExternalSeriesId = m.ProviderKey ?? string.Empty,
                    ExternalSeriesTitle = null,
                    MappingStatus = m.MappingStatus,
                    LinkedDate = m.LinkedDate,
                    LinkedSitesIds = [],
                    AlternativeTitles = []
                };

                // Expired TemporaryIgnored → treat as Unmatched again (same rule as External Mappings).
                if (row.MappingStatus == SeriesMappingStatus.TemporaryIgnored &&
                    row.LinkedDate != null &&
                    row.LinkedDate <= nowMinusOneMonth)
                {
                    row.MappingStatus = SeriesMappingStatus.Unmatched;
                }

                if (status.HasValue && row.MappingStatus != status.Value) continue;
                if (!status.HasValue && !IsActiveStatus(row.MappingStatus)) continue;

                if (row.MappingStatus == SeriesMappingStatus.Unmatched) groupHasUnmatched = true;
                if (row.MappingStatus == SeriesMappingStatus.Blocked) groupHasBlocked = true;
                providerRows.Add(row);
            }

            if (providerRows.Count == 0) continue;
            if (isUnmatchedFilter && !groupHasUnmatched) continue;
            if (isBlockedFilter && !groupHasBlocked) continue;

            groups.Add(new ContributionMappingGroupDto
            {
                MappingId = mappingId,
                DisplayTitle = titles.FirstOrDefault() ?? "Untitled",
                CoverUrl = sources.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.ThumbnailUrl))?.ThumbnailUrl,
                Titles = titles,
                Sources = sources,
                Providers = providerRows
            });
        }

        groups.Sort((a, b) => (a.DisplayTitle ?? string.Empty).CompareTo(b.DisplayTitle ?? string.Empty));

        result.Total = groups.Count;
        result.Groups = groups
            .Skip(page * pageSize).Take(pageSize)
            .ToList();
        return result;
    }

    /// <summary>Single contribution mapping group (all provider rows + merged sources + titles).</summary>
    public async Task<ContributionMappingGroupDto?> GetMappingAsync(
        Guid mappingId, List<IExternalSeriesProvider> enabledProviders, CancellationToken token)
    {
        var mappingTitles = await _contributorDb.MappingTitles
            .Include(mt => mt.Title)
            .Where(mt => mt.MappingId == mappingId)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        if (mappingTitles.Count == 0) return null;

        var titles = mappingTitles
            .Where(mt => mt.Title != null && !string.IsNullOrWhiteSpace(mt.Title.Title))
            .Select(mt => mt.Title!.Title!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var seriesRows = await _contributorDb.Series
            .Include(s => s.Source)
            .Where(s => s.MappingId == mappingId
                && s.Version != VersionLogicalDelete
                && s.Version != VersionUploadedDelete)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        var sources = seriesRows.Select(ToSourceDto).Where(s => s != null).Cast<ContributionMappingSourceDto>().ToList();

        var metadataRows = await _contributorDb.Metadata
            .Where(m => m.MappingId == mappingId)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        var providerRows = metadataRows.Select(m => new ExternalMappingsSeriesProviderDto
        {
            ProviderCoverUrl = null,
            Provider = (ExternalSeriesProvider)m.ProviderId,
            ExternalSeriesId = m.ProviderKey ?? string.Empty,
            ExternalSeriesTitle = null,
            MappingStatus = m.MappingStatus,
            LinkedDate = m.LinkedDate,
            LinkedSitesIds = [],
            AlternativeTitles = []
        }).ToList();

        return new ContributionMappingGroupDto
        {
            MappingId = mappingId,
            DisplayTitle = titles.FirstOrDefault() ?? "Untitled",
            CoverUrl = sources.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.ThumbnailUrl))?.ThumbnailUrl,
            Titles = titles,
            Sources = sources,
            Providers = providerRows
        };
    }

    /// <summary>
    /// Resolves the titles of a mapping, builds seeds/rules from its metadata rows, runs the shared
    /// matcher and persists the linked results + materialized Unmatched rows into the contribution
    /// DB. Mirrors <c>MetadataLinkEngine.LinkSeriesAsync</c> but against <c>ContributionDbContext</c>.
    /// </summary>
    public async Task<Metadata.MetadataMatchResult> ScanMappingAsync(
        Guid mappingId, List<IExternalSeriesProvider> providers, CancellationToken token)
    {
        // 1. Gather the mapping's title candidates (the automerge identity set).
        var mappingTitles = await _contributorDb.MappingTitles
            .Include(mt => mt.Title)
            .Where(mt => mt.MappingId == mappingId)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);
        var titleCandidates = mappingTitles
            .Where(mt => mt.Title != null && !string.IsNullOrWhiteSpace(mt.Title.Title))
            .Select(mt => mt.Title!.Title!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (titleCandidates.Count == 0) return new Metadata.MetadataMatchResult();

        // 2. Derive seeds, blocked ids and skipped providers from the existing metadata rows.
        var existing = await _contributorDb.Metadata
            .Where(m => m.MappingId == mappingId)
            .AsNoTracking()
            .ToListAsync(token).ConfigureAwait(false);

        var seeds = new List<MetadataLinkSeed>();
        var blockedIds = new Dictionary<ExternalSeriesProvider, HashSet<string>>();
        var skippedProviders = new HashSet<ExternalSeriesProvider>();
        var now = DateTime.UtcNow;
        var ignoreThreshold = now.AddMonths(-1);
        var reEvaluated = new List<ContributionMetadataEntity>();

        foreach (var m in existing)
        {
            var p = (ExternalSeriesProvider)m.ProviderId;
            switch (m.MappingStatus)
            {
                case SeriesMappingStatus.Blocked:
                    if (!string.IsNullOrWhiteSpace(m.ProviderKey))
                    {
                        if (!blockedIds.TryGetValue(p, out var set))
                            blockedIds[p] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        set.Add(m.ProviderKey);
                    }
                    break;
                case SeriesMappingStatus.ForeverIgnored:
                    skippedProviders.Add(p);
                    break;
                case SeriesMappingStatus.TemporaryIgnored:
                    if (m.LinkedDate != null && m.LinkedDate <= ignoreThreshold)
                        reEvaluated.Add(m); // expired → re-evaluate (cleared below)
                    else
                        skippedProviders.Add(p);
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(m.ProviderKey))
                    {
                        seeds.Add(new MetadataLinkSeed
                        {
                            Provider = p,
                            ExternalSeriesId = m.ProviderKey,
                            Confidence = 1.0
                        });
                    }
                    break;
            }
        }

        // 2.5 CanSearchSeries gate — BEFORE searching any provider for this mapping, ask the
        //     provider whether it can search this kind of series at all. Genres come from the
        //     matching local SeriesEntity.Genre and the category from SeriesEntity.Type (the
        //     contribution schema stores neither). When a provider cannot search the series, the
        //     (mapping, provider) relation is settled as "ignoredAlways" (ForeverIgnored) so it
        //     is never searched again.
        var (genres, category) = await ResolveSeriesGenreAsync(titleCandidates, token).ConfigureAwait(false);
        foreach (var provider in providers)
        {
            if (!provider.CanSearchSeries(genres, category))
            {
                skippedProviders.Add(provider.ProviderType);
                await EnsureProviderIgnoredAsync(mappingId, provider.ProviderType, now, token).ConfigureAwait(false);
            }
        }

        // The rows loaded above are AsNoTracking snapshots, but EnsureProviderIgnoredAsync (just
        // above) re-queried them WITH tracking, so the context may now hold a DIFFERENT instance
        // for the same primary key. Re-bind every snapshot to the tracked instance:
        //   * without this, the Attach calls below throw "another instance with the same key
        //     value for {'Id'} is already being tracked"; and
        //   * the decision checks would evaluate against stale state — e.g. step 6 would tombstone
        //     a row that this pass just settled as ForeverIgnored, because the snapshot still
        //     shows the previous Unmatched status.
        var trackedMetadataById = _contributorDb.ChangeTracker.Entries<ContributionMetadataEntity>()
            .ToDictionary(e => e.Entity.Id, e => e.Entity);
        if (trackedMetadataById.Count > 0)
        {
            for (int i = 0; i < existing.Count; i++)
                if (trackedMetadataById.TryGetValue(existing[i].Id, out var trackedExisting))
                    existing[i] = trackedExisting;

            for (int i = 0; i < reEvaluated.Count; i++)
                if (trackedMetadataById.TryGetValue(reEvaluated[i].Id, out var trackedReEvaluated))
                    reEvaluated[i] = trackedReEvaluated;
        }

        // 3. Contribution-DB first match is intrinsic here: existing active links (AutoMatched /
        //    UserConfirmed rows with a provider key) are seeded into the matcher and never
        //    re-searched — HIT = no provider search, only metadata fetch when available.
        //
        //    SEARCH SCOPE: only providers with PENDING work are searched and seeded. A provider is
        //    SETTLED — and must never be re-searched, seeded or (re)linked — when it has an active
        //    link (AutoMatched/UserConfirmed), a refusal (Blocked), a permanent ignore
        //    (ForeverIgnored), or an ignore that has NOT yet expired (TemporaryIgnored within one
        //    month). PENDING providers are: Unmatched (searched before, no match — re-search),
        //    an EXPIRED TemporaryIgnored (re-evaluate), and a provider with NO row at all (never
        //    searched). This mirrors the rensaio.db engine's GetPendingProviders so both scanners
        //    re-scan exactly the same set.
        bool IsPendingRow(ContributionMetadataEntity m) => m.MappingStatus switch
        {
            SeriesMappingStatus.Unmatched => true,
            SeriesMappingStatus.TemporaryIgnored => m.LinkedDate != null && m.LinkedDate <= ignoreThreshold,
            SeriesMappingStatus.AutoMatched => false,
            SeriesMappingStatus.UserConfirmed => false,
            SeriesMappingStatus.Blocked => false,
            SeriesMappingStatus.ForeverIgnored => false,
            _ => true,
        };

        var pendingProviders = existing.Where(IsPendingRow)
            .Select(m => (ExternalSeriesProvider)m.ProviderId)
            .ToHashSet();

        // A provider is settled when a row exists for it and that row is not pending. A provider
        // with no row at all has never been searched and stays pending.
        bool IsSettled(ExternalSeriesProvider provider)
            => existing.Any(m => m.ProviderId == (int)provider)
                && !pendingProviders.Contains(provider);

        var enabled = providers.Select(p => p.ProviderType).ToHashSet();
        var searchProviders = providers
            .Where(p => !skippedProviders.Contains(p.ProviderType) && !IsSettled(p.ProviderType))
            .ToList();

        Metadata.MetadataMatchResult match = searchProviders.Count > 0
            ? await _matchCore.MatchAsync(titleCandidates, searchProviders, seeds, blockedIds,
                skippedProviders, enabled, token).ConfigureAwait(false)
            : new Metadata.MetadataMatchResult();

        // 4. Persist linked nodes as ContributionMetadata rows.
        var desired = new HashSet<(int ProviderId, string ProviderKey)>();
        var byIdentity = existing.ToDictionary(m => (m.ProviderId, m.ProviderKey ?? string.Empty));

        foreach (var node in match.Links)
        {
            // Never (re)link a settled provider. The matcher only searches pending providers, but a
            // seed-derived node can still surface on a provider that already holds a decision — an
            // active link, Blocked, ForeverIgnored or a non-expired TemporaryIgnored refusal.
            if (IsSettled(node.Provider))
                continue;

            var identity = ((int)node.Provider, node.ExternalSeriesId);
            desired.Add(identity);
            if (byIdentity.TryGetValue(identity, out var row))
            {
                var changed = row.MappingStatus != SeriesMappingStatus.AutoMatched
                    || row.Version == VersionLogicalDelete
                    || row.LinkedDate == null;
                if (changed)
                {
                    _contributorDb.Metadata.Attach(row);
                    row.MappingStatus = SeriesMappingStatus.AutoMatched;
                    row.LinkedDate = now;
                    row.Version = VersionAddOrUpdate;
                    _contributorDb.Entry(row).Property(x => x.MappingStatus).IsModified = true;
                    _contributorDb.Entry(row).Property(x => x.LinkedDate).IsModified = true;
                    _contributorDb.Entry(row).Property(x => x.Version).IsModified = true;
                }
            }
            else
            {
                _contributorDb.Metadata.Add(new ContributionMetadataEntity
                {
                    Id = Guid.NewGuid(),
                    MappingId = mappingId,
                    ProviderId = (int)node.Provider,
                    ProviderKey = node.ExternalSeriesId,
                    MappingStatus = SeriesMappingStatus.AutoMatched,
                    LinkedDate = now,
                    Version = VersionAddOrUpdate
                });
            }
        }

        // 5. Materialize Unmatched rows for enabled providers that ended with no link and no decision.
        //    A provider already having ANY active metadata row (matched or decided) must never get
        //    a second (provider, "") Unmatched row — the "Not matched" duplicate from the bug report.
        foreach (var p in providers)
        {
            // Provider declared it cannot search this series → settled as ignoredAlways by the
            // search gate; never materialize a competing Unmatched row for it.
            if (skippedProviders.Contains(p.ProviderType)) continue;

            var identity = ((int)p.ProviderType, string.Empty);
            if (desired.Contains(identity) || byIdentity.ContainsKey(identity)) continue;
            var hasAnyActive = existing.Any(m => m.ProviderId == (int)p.ProviderType
                && m.Version != VersionLogicalDelete);
            if (hasAnyActive) continue;
            var hasDecision = existing.Any(m => m.ProviderId == (int)p.ProviderType
                && m.MappingStatus is SeriesMappingStatus.Blocked
                    or SeriesMappingStatus.TemporaryIgnored
                    or SeriesMappingStatus.ForeverIgnored
                    or SeriesMappingStatus.UserConfirmed
                    or SeriesMappingStatus.AutoMatched);
            if (hasDecision || match.Links.Any(l => l.Provider == p.ProviderType)) continue;

            _contributorDb.Metadata.Add(new ContributionMetadataEntity
            {
                Id = Guid.NewGuid(),
                MappingId = mappingId,
                ProviderId = (int)p.ProviderType,
                ProviderKey = string.Empty,
                MappingStatus = SeriesMappingStatus.Unmatched,
                LinkedDate = null,
                Version = VersionAddOrUpdate
            });
        }

        // 6. Tombstone rows whose (provider,key) is no longer desired and not a preserved decision.
        //    AutoMatched rows with a real provider key are preserved too: a link that was matched
        //    by a previous scan (or promoted from the External Mappings page) must not be dropped
        //    just because this pass didn't re-confirm it — otherwise the match would silently
        //    revert to Unmatched on the Contribution Mappings page.
        foreach (var row in existing)
        {
            var identity = (row.ProviderId, row.ProviderKey ?? string.Empty);
            var isDecision = row.MappingStatus is SeriesMappingStatus.Blocked
                or SeriesMappingStatus.TemporaryIgnored
                or SeriesMappingStatus.ForeverIgnored
                or SeriesMappingStatus.UserConfirmed
                or SeriesMappingStatus.AutoMatched;
            if (!desired.Contains(identity) && !isDecision && row.Version != VersionLogicalDelete)
            {
                _contributorDb.Metadata.Attach(row);
                row.Version = VersionLogicalDelete;
                _contributorDb.Entry(row).Property(x => x.Version).IsModified = true;
            }
        }

        // 7. Clear expired TemporaryIgnored (re-evaluated this pass to Unmatched).
        foreach (var row in reEvaluated)
        {
            _contributorDb.Metadata.Attach(row);
            row.MappingStatus = SeriesMappingStatus.Unmatched;
            row.ProviderKey = string.Empty;
            row.LinkedDate = null;
            row.Version = VersionAddOrUpdate;
            _contributorDb.Entry(row).Property(x => x.MappingStatus).IsModified = true;
            _contributorDb.Entry(row).Property(x => x.ProviderKey).IsModified = true;
            _contributorDb.Entry(row).Property(x => x.LinkedDate).IsModified = true;
            _contributorDb.Entry(row).Property(x => x.Version).IsModified = true;
        }

        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
        return match;
    }

    /// <summary>
    /// Confirms a manual mapping for a contribution metadata row (upsert, Version = 0).
    /// Because the pre-existing Unmatched row (ProviderKey = "") for the same provider must not
    /// survive as an orphan, any OTHER row for this (mapping, provider) is tombstoned, so the
    /// confirmed (provider, externalId) row is the only active one.
    /// </summary>
    public async Task ConfirmAsync(Guid mappingId, ExternalSeriesProvider provider,
        string externalSeriesId, string? externalSeriesTitle = null, CancellationToken token = default)
    {
        var now = DateTime.UtcNow;
        var allForProvider = await _contributorDb.Metadata
            .Where(m => m.MappingId == mappingId && m.ProviderId == (int)provider)
            .ToListAsync(token);

        // The row we will turn into the confirmed match: prefer an existing row with the
        // SAME external id (idempotent re-confirm), else any active row (rename), else create.
        var existing = allForProvider.FirstOrDefault(m => m.ProviderKey == externalSeriesId)
            ?? allForProvider.FirstOrDefault(m => m.Version != VersionLogicalDelete);

        if (existing != null)
        {
            // Blocked is id-level: matching a DIFFERENT id is allowed.
            if (existing.MappingStatus == SeriesMappingStatus.Blocked &&
                string.Equals(existing.ProviderKey, externalSeriesId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Cannot match mapping {mappingId} to the blocked id '{externalSeriesId}' on {provider}.");
            }
            existing.ProviderKey = externalSeriesId;
            existing.MappingStatus = SeriesMappingStatus.UserConfirmed;
            existing.LinkedDate = now;
            existing.Version = VersionAddOrUpdate;
            _contributorDb.Entry(existing).Property(x => x.ProviderKey).IsModified = true;
            _contributorDb.Entry(existing).Property(x => x.MappingStatus).IsModified = true;
            _contributorDb.Entry(existing).Property(x => x.LinkedDate).IsModified = true;
            _contributorDb.Entry(existing).Property(x => x.Version).IsModified = true;
        }
        else
        {
            existing = new ContributionMetadataEntity
            {
                Id = Guid.NewGuid(),
                MappingId = mappingId,
                ProviderId = (int)provider,
                ProviderKey = externalSeriesId,
                MappingStatus = SeriesMappingStatus.UserConfirmed,
                LinkedDate = now,
                Version = VersionAddOrUpdate
            };
            _contributorDb.Metadata.Add(existing);
        }

        // Tombstone any OTHER rows for this provider (most commonly the Unmatched (ProviderKey="")
        // row materialized by a scan) so only the confirmed row stays active.
        foreach (var row in allForProvider)
        {
            if (row == existing || row.Version == VersionLogicalDelete)
                continue;
            _contributorDb.Metadata.Attach(row);
            row.Version = VersionLogicalDelete;
            _contributorDb.Entry(row).Property(x => x.Version).IsModified = true;
        }

        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
    }

    /// <summary>Sets a contribution metadata row to Blocked (empty key = hard block; id block retains the key).</summary>
    public async Task BlockAsync(Guid mappingId, ExternalSeriesProvider provider, CancellationToken token = default)
    {
        var existing = await GetRowAsync(mappingId, provider, token);
        if (existing != null)
        {
            existing.MappingStatus = SeriesMappingStatus.Blocked;
            existing.LinkedDate ??= DateTime.UtcNow;
            existing.Version = VersionAddOrUpdate;
        }
        else
        {
            _contributorDb.Metadata.Add(new ContributionMetadataEntity
            {
                Id = Guid.NewGuid(),
                MappingId = mappingId,
                ProviderId = (int)provider,
                ProviderKey = string.Empty,
                MappingStatus = SeriesMappingStatus.Blocked,
                LinkedDate = DateTime.UtcNow,
                Version = VersionAddOrUpdate
            });
        }
        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
    }

    /// <summary>Clears a block / ignore on a contribution metadata row (back to Unmatched).</summary>
    public async Task UnblockAsync(Guid mappingId, ExternalSeriesProvider provider, CancellationToken token = default)
    {
        var existing = await GetRowAsync(mappingId, provider, token);
        if (existing == null) return;
        existing.MappingStatus = SeriesMappingStatus.Unmatched;
        existing.LinkedDate = DateTime.UtcNow;
        existing.Version = VersionAddOrUpdate;
        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
    }

    /// <summary>Applies TemporaryIgnored or ForeverIgnored to a contribution metadata row.</summary>
    public async Task IgnoreAsync(Guid mappingId, ExternalSeriesProvider provider, bool forever, CancellationToken token = default)
    {
        var status = forever ? SeriesMappingStatus.ForeverIgnored : SeriesMappingStatus.TemporaryIgnored;
        var existing = await GetRowAsync(mappingId, provider, token);
        if (existing != null)
        {
            existing.MappingStatus = status;
            existing.LinkedDate = DateTime.UtcNow;
            existing.Version = VersionAddOrUpdate;
        }
        else
        {
            _contributorDb.Metadata.Add(new ContributionMetadataEntity
            {
                Id = Guid.NewGuid(),
                MappingId = mappingId,
                ProviderId = (int)provider,
                ProviderKey = string.Empty,
                MappingStatus = status,
                LinkedDate = DateTime.UtcNow,
                Version = VersionAddOrUpdate
            });
        }
        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Marks every Not Matched (Unmatched) provider of a contribution mapping as ForeverIgnored
    /// ("Ignore always"). Providers already carrying a decision (auto/user matched, blocked, active
    /// TemporaryIgnored, or already ForeverIgnored) are left untouched; providers with no metadata
    /// row are materialized as ForeverIgnored — same semantics as the per-provider ignore action.
    /// Returns the list of providers that were actually ignored (empty when none).
    /// </summary>
    public async Task<List<ExternalSeriesProvider>> IgnoreAllUnmatchedAsync(
        Guid mappingId, List<IExternalSeriesProvider> providers, CancellationToken token = default)
    {
        var existing = await _contributorDb.Metadata
            .Where(m => m.MappingId == mappingId)
            .ToListAsync(token).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var expiryThreshold = now.AddMonths(-1); // expired TemporaryIgnored re-opens as Unmatched
        var ignored = new List<ExternalSeriesProvider>();

        foreach (var p in providers)
        {
            var row = existing.FirstOrDefault(m => m.ProviderId == (int)p.ProviderType);

            if (row == null)
            {
                _contributorDb.Metadata.Add(new ContributionMetadataEntity
                {
                    Id = Guid.NewGuid(),
                    MappingId = mappingId,
                    ProviderId = (int)p.ProviderType,
                    ProviderKey = string.Empty,
                    MappingStatus = SeriesMappingStatus.ForeverIgnored,
                    LinkedDate = now,
                    Version = VersionAddOrUpdate
                });
                ignored.Add(p.ProviderType);
                continue;
            }

            // Only Not Matched rows are ignored: Unmatched, or a TemporaryIgnored whose month has
            // elapsed (the page treats it as Unmatched again — re-evaluation due). Tombstoned rows
            // are excluded.
            var isUnmatched = row.Version != VersionLogicalDelete
                && (row.MappingStatus == SeriesMappingStatus.Unmatched
                    || (row.MappingStatus == SeriesMappingStatus.TemporaryIgnored
                        && row.LinkedDate != null
                        && row.LinkedDate <= expiryThreshold));
            if (!isUnmatched) continue;

            row.MappingStatus = SeriesMappingStatus.ForeverIgnored;
            row.LinkedDate = now;
            row.Version = VersionAddOrUpdate;
            ignored.Add(p.ProviderType);
        }

        if (ignored.Count > 0)
        {
            await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
        }
        return ignored;
    }

    /// <summary>Removes a contribution metadata row (tombstoned for replication).</summary>
    public async Task UnlinkAsync(Guid mappingId, ExternalSeriesProvider provider, CancellationToken token = default)
    {
        var existing = await GetRowAsync(mappingId, provider, token);
        if (existing == null) return;
        existing.Version = VersionLogicalDelete;
        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the contribution mapping ids that own any of the supplied titles.
    /// Used by the seed sweep to scope matching to just the series it collected,
    /// instead of rescanning the whole contribution graph.
    /// </summary>
    public async Task<List<Guid>> FindMappingIdsByTitlesAsync(IEnumerable<string> titles, CancellationToken token = default)
    {
        var wanted = titles
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count == 0)
            return [];

        return await _contributorDb.MappingTitles
            .Include(mt => mt.Title)
            .Where(mt => mt.Title != null && wanted.Contains(mt.Title.Title!))
            .Select(mt => mt.MappingId)
            .Distinct()
            .ToListAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Scoped variant of <see cref="ScanAllMappingsAsync"/>: matches only the supplied
    /// mapping ids. Progress is still published under the same aggregate job id so the
    /// UI shows a coherent run.
    /// </summary>
    public async Task ScanMappingsAsync(
        IReadOnlyCollection<Guid> mappingIds, List<IExternalSeriesProvider> providers, CancellationToken token = default)
    {
        var total = mappingIds.Count;
        if (total == 0)
            return;

        if (_hubReport != null)
        {
            await _hubReport.ReportProgressAsync(new ProgressState
            {
                Id = LinkAllJobId,
                JobType = JobType.MetadataLink,
                ProgressStatus = ProgressStatus.Started,
                Percentage = 0,
                Message = $"Linking 0/{total} mappings"
            });
        }

        var processed = 0;
        foreach (var id in mappingIds)
        {
            if (token.IsCancellationRequested) break;

            await ScanMappingAsync(id, providers, token).ConfigureAwait(false);
            processed++;

            if (_hubReport != null)
            {
                var pct = processed * 100 / total;
                await _hubReport.ReportProgressAsync(new ProgressState
                {
                    Id = LinkAllJobId,
                    JobType = JobType.MetadataLink,
                    ProgressStatus = ProgressStatus.InProgress,
                    Percentage = pct,
                    Message = $"Linking {processed}/{total} mappings"
                });
            }
        }

        if (_hubReport != null)
        {
            await _hubReport.ReportProgressAsync(new ProgressState
            {
                Id = LinkAllJobId,
                JobType = JobType.MetadataLink,
                ProgressStatus = ProgressStatus.Completed,
                Percentage = 100,
                Message = $"Linked {processed}/{total} mappings"
            });
        }
    }

    /// <summary>Background scan across all mappings, publishing per-mapping + aggregate SignalR progress.</summary>
    public async Task ScanAllMappingsAsync(List<IExternalSeriesProvider> providers, CancellationToken token = default)
    {
        var mappingIds = await _contributorDb.MappingTitles
            .Select(mt => mt.MappingId)
            .Distinct()
            .ToListAsync(token).ConfigureAwait(false);
        var total = mappingIds.Count;

        if (_hubReport != null)
        {
            await _hubReport.ReportProgressAsync(new ProgressState
            {
                Id = LinkAllJobId,
                JobType = JobType.MetadataLink,
                ProgressStatus = ProgressStatus.Started,
                Percentage = 0,
                Message = $"Linking 0/{total} mappings"
            });
        }

        var processed = 0;
        foreach (var id in mappingIds)
        {
            if (token.IsCancellationRequested) break;
            if (_hubReport != null)
            {
                await _hubReport.ReportProgressAsync(new ProgressState
                {
                    Id = $"contribution-link-{id}",
                    JobType = JobType.MetadataLink,
                    ProgressStatus = ProgressStatus.Started,
                    Percentage = 0,
                    Message = "Scanning contribution mapping…"
                });
            }

            var match = await ScanMappingAsync(id, providers, token).ConfigureAwait(false);
            processed++;

            if (_hubReport != null)
            {
                var pct = total > 0 ? (processed * 100 / total) : 100;
                await _hubReport.ReportProgressAsync(new ProgressState
                {
                    Id = $"contribution-link-{id}",
                    JobType = JobType.MetadataLink,
                    ProgressStatus = ProgressStatus.Completed,
                    Percentage = 100,
                    Message = $"{match.Links.Count} linked, {match.Suggestions.Count} suggested"
                });
                await _hubReport.ReportProgressAsync(new ProgressState
                {
                    Id = LinkAllJobId,
                    JobType = JobType.MetadataLink,
                    ProgressStatus = ProgressStatus.InProgress,
                    Percentage = pct,
                    Message = $"Linking {processed}/{total} mappings"
                });
            }
        }

        if (_hubReport != null)
        {
            await _hubReport.ReportProgressAsync(new ProgressState
            {
                Id = LinkAllJobId,
                JobType = JobType.MetadataLink,
                ProgressStatus = ProgressStatus.Completed,
                Percentage = 100,
                Message = $"Linked {processed}/{total} mappings"
            });
        }
    }

    // ── Internals ──

    private async Task<ContributionMetadataEntity?> GetRowAsync(Guid mappingId, ExternalSeriesProvider provider, CancellationToken token)
        => await _contributorDb.Metadata
            .FirstOrDefaultAsync(m => m.MappingId == mappingId && m.ProviderId == (int)provider, token).ConfigureAwait(false);

    /// <summary>
    /// Resolves the genre list + category of the local (Rensaio) series that best matches this
    /// contribution mapping's titles, so <see cref="CanSearchSeries"/> can be evaluated. The
    /// contribution schema stores neither genre nor type — the local series is the only source.
    /// Returns ([], null) when no local series matches.
    /// </summary>
    private async Task<(List<string> Genres, string? Category)> ResolveSeriesGenreAsync(
        List<string> titleCandidates, CancellationToken token)
    {
        try
        {
            var candidates = titleCandidates.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (candidates.Count == 0) return ([], null);

            var matching = await _db.Series
                .Include(s => s.Sources)
                .AsNoTracking()
                .Where(s => candidates.Contains(s.Title) || s.Sources.Any(src => candidates.Contains(src.Title)))
                .ToListAsync(token).ConfigureAwait(false);
            if (matching.Count == 0) return ([], null);

            // Prefer the series whose PRIMARY title matches, then any partial source-title match.
            var best = matching
                .OrderByDescending(s => candidates.Contains(s.Title))
                .First();
            return (best.Genre ?? [], best.Type);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve genre/category for contribution mapping scan");
            return ([], null);
        }
    }

    /// <summary>
    /// Settles a (mapping, provider) relation as ForeverIgnored because the provider declared it
    /// cannot search this series (CanSearchSeries == false). Never downgrades a stronger decision
    /// (UserConfirmed / Blocked / ForeverIgnored).
    /// </summary>
    private async Task EnsureProviderIgnoredAsync(Guid mappingId, ExternalSeriesProvider provider,
        DateTime now, CancellationToken token)
    {
        var row = await GetRowAsync(mappingId, provider, token).ConfigureAwait(false);
        if (row != null)
        {
            if (row.MappingStatus is SeriesMappingStatus.UserConfirmed
                or SeriesMappingStatus.Blocked
                or SeriesMappingStatus.ForeverIgnored)
                return; // stronger decision stands
            _contributorDb.Metadata.Attach(row);
            row.ProviderKey = string.Empty;
            row.MappingStatus = SeriesMappingStatus.ForeverIgnored;
            row.LinkedDate = now;
            row.Version = VersionAddOrUpdate;
            _contributorDb.Entry(row).Property(x => x.ProviderKey).IsModified = true;
            _contributorDb.Entry(row).Property(x => x.MappingStatus).IsModified = true;
            _contributorDb.Entry(row).Property(x => x.LinkedDate).IsModified = true;
            _contributorDb.Entry(row).Property(x => x.Version).IsModified = true;
        }
        else
        {
            _contributorDb.Metadata.Add(new ContributionMetadataEntity
            {
                Id = Guid.NewGuid(),
                MappingId = mappingId,
                ProviderId = (int)provider,
                ProviderKey = string.Empty,
                MappingStatus = SeriesMappingStatus.ForeverIgnored,
                LinkedDate = now,
                Version = VersionAddOrUpdate
            });
        }
        await _contributorDb.SaveChangesAsync(token).ConfigureAwait(false);
    }

    private static ContributionMappingSourceDto? ToSourceDto(ContributionSeriesEntity? s)
    {
        if (s == null || s.Source == null) return null;
        var data = s.Data;
        return new ContributionMappingSourceDto
        {
            SourceId = s.SourceId,
            SourceKey = $"{s.Source.Package}:{s.Source.SourceId}",
            Package = s.Source.Package,
            SourceName = s.Source.SourceName,
            SourceLanguage = s.Source.SourceLanguage,
            // Per-source title is derived from the mapping's canonical TitleEntity on the group level;
            // a mirror of the source record lives in the serialized Data payload (not surfaced here).
            Title = null,
            ThumbnailUrl = data?.ThumbnailUrl
        };
    }

    private static bool IsActiveStatus(SeriesMappingStatus status)
        => status is SeriesMappingStatus.Unmatched
            or SeriesMappingStatus.AutoMatched
            or SeriesMappingStatus.UserConfirmed
            or SeriesMappingStatus.Blocked
            or SeriesMappingStatus.TemporaryIgnored
            or SeriesMappingStatus.ForeverIgnored;
}