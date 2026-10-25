using Microsoft.AspNetCore.Mvc;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Auth;
using RensaioBackend.Services.Contributions;
using RensaioBackend.Services.Images;
using RensaioBackend.Services.Metadata;
using RensaioBackend.Services.Scrobbling.Abstractions;
using RensaioBackend.Services.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RensaioBackend.Controllers;

/// <summary>
/// Contribution Mappings page (source scope). Reads/writes the local contribution database
/// (<c>contributor.db</c>) and cascades edits into the Rensaio DB for matching series.
///
/// Access: Owner only AND the app must have Contribution enabled (runtime gate). Provider
/// resolution/authentication/meta + provider-row projection are shared with the External Mappings
/// page via <see cref="ExternalMappingSupport"/>; the matcher is the shared
/// <see cref="MetadataMatchCore"/>; Rensaio-DB writes go through
/// <see cref="RensaioMappingCascadeService"/>. No backend logic is copy-pasted.
/// </summary>
[ApiController]
[Route("api/contribution-mappings")]
[RequireUserLevel(UserLevel.Owner)]
public class ContributionMappingsController : ControllerBase
{
    private readonly ContributionMappingService _mappingService;
    private readonly RensaioMappingCascadeService _cascade;
    private readonly ContributionTitleHeuristicsService _titleHeuristics;
    private readonly ExternalMappingSupport _support;
    private readonly SettingsService _settingsService;
    private readonly ThumbCacheService _thumbCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContributionMappingsController> _logger;

    public ContributionMappingsController(
        ContributionMappingService mappingService,
        RensaioMappingCascadeService cascade,
        ContributionTitleHeuristicsService titleHeuristics,
        ExternalMappingSupport support,
        SettingsService settingsService,
        ThumbCacheService thumbCache,
        IServiceScopeFactory scopeFactory,
        ILogger<ContributionMappingsController> logger)
    {
        _mappingService = mappingService;
        _cascade = cascade;
        _titleHeuristics = titleHeuristics;
        _support = support;
        _settingsService = settingsService;
        _thumbCache = thumbCache;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Rejects the request (404) when Contribution is disabled or the
    /// Contributor Id is not verified against the contribution database.</summary>
    private async Task<bool> ContributionDisabledAsync(CancellationToken token)
    {
        var settings = await _settingsService.GetSettingsAsync(token).ConfigureAwait(false);
        return !settings.ContributionEnabled || !settings.ContributionVerified;
    }

    private async Task<List<IExternalSeriesProvider>> EnabledProvidersAsync(CancellationToken token)
    {
        var user = HttpContext.Items["User"] as UserEntity;
        return await _support.GetEnabledProvidersAsync(user, token).ConfigureAwait(false);
    }

    /// <summary>
    /// GET /api/contribution-mappings?filter=all|unmatched|blocked|manualignored&verified=all|verified|unverified&name=&page=&pageSize=&provider=&status=
    /// Source-scope: grouped by mapping (automerge on normalized titles). `unmatched` keeps only
    /// mappings with at least one Unmatched provider row; `blocked` keeps only mappings with at
    /// least one Blocked provider row; `manualignored` keeps only mappings with at least one
    /// ManualIgnore provider row. `verified=unverified` keeps only mappings with at least one live
    /// entry whose VerifiedDateUTC is null; `verified=verified` keeps only mappings that have a
    /// live entry and NO live entry with a null VerifiedDateUTC. `name` is an additional GLOBAL
    /// filter (ANDed with `filter`) matching the mapping's titles / member sources.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ContributionMappingsPageDto>> List(
        [FromQuery] string filter = "unmatched",
        [FromQuery] int page = 0,
        [FromQuery] int pageSize = 50,
        [FromQuery] ExternalSeriesProvider? provider = null,
        [FromQuery] SeriesMappingStatus? status = null,
        [FromQuery] string? name = null,
        [FromQuery] string verified = "all",
        CancellationToken token = default)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();

        var providers = await EnabledProvidersAsync(token).ConfigureAwait(false);
        var result = await _mappingService.ListMappingsAsync(filter, page, pageSize, provider, status, name, verified, providers, token)
            .ConfigureAwait(false);

        // Rewrite source + provider covers through the image cache (same as External Mappings).
        await _thumbCache.PopulateThumbsAsync(result.Groups, "/api/image/", token).ConfigureAwait(false);
        await _thumbCache.PopulateThumbsAsync(result.Groups.SelectMany(g => g.Providers).Where(p => p != null), "/api/image/", token).ConfigureAwait(false);

        return Ok(result);
    }

    /// <summary>GET /api/contribution-mappings/mappings/{mappingId} — single mapping group.</summary>
    [HttpGet("mappings/{mappingId:guid}")]
    public async Task<ActionResult<ContributionMappingGroupDto>> GetMapping(Guid mappingId, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();

        var providers = await EnabledProvidersAsync(token).ConfigureAwait(false);
        var group = await _mappingService.GetMappingAsync(mappingId, providers, token).ConfigureAwait(false);
        if (group == null) return NotFound();

        await _thumbCache.PopulateThumbsAsync(group, "/api/image/", token).ConfigureAwait(false);
        await _thumbCache.PopulateThumbsAsync(group.Providers.Where(p => p != null), "/api/image/", token).ConfigureAwait(false);
        return Ok(group);
    }

    /// <summary>
    /// POST /api/contribution-mappings/mappings/{mappingId}/smart-search?provider=X — "Smart Search":
    /// searches <paramref name="provider"/> with EVERY title of the mapping (no user-typed query),
    /// scores the results with the shared title + image (PDQ) algorithm using the mapping's existing
    /// source/provider covers as image references, and returns only the best-ranked candidates.
    /// </summary>
    [HttpPost("mappings/{mappingId:guid}/smart-search")]
    public async Task<ActionResult<SmartSearchResultDto>> SmartSearch(
        Guid mappingId, [FromQuery] ExternalSeriesProvider provider, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();

        var providers = await EnabledProvidersAsync(token).ConfigureAwait(false);
        var target = providers.FirstOrDefault(p => p.ProviderType == provider);
        if (target == null) return BadRequest(new { message = "Provider is not enabled" });

        // Refresh OAuth tokens (bounded) before the per-title search, same as the scan path.
        await _support.EnsureProvidersAuthenticatedAsync(new[] { target }, token).ConfigureAwait(false);

        var result = await _mappingService.SmartSearchAsync(mappingId, provider, providers, token)
            .ConfigureAwait(false);

        // Rewrite result covers through the image cache (same as the normal search dialog).
        await _thumbCache.PopulateThumbsAsync(result.Results, "/api/image/", token).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>POST /api/contribution-mappings/mappings/{mappingId}/scan — run the shared matcher for one mapping.</summary>
    [HttpPost("mappings/{mappingId:guid}/scan")]
    public async Task<ActionResult> ScanMapping(Guid mappingId, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();

        var providers = await EnabledProvidersAsync(token).ConfigureAwait(false);
        await _mappingService.ScanMappingAsync(mappingId, providers, token).ConfigureAwait(false);
        await _mappingService.ReconcileSeriesGroupingAsync(token).ConfigureAwait(false);
        return Ok(new { message = "Scan completed" });
    }

    /// <summary>
    /// POST /api/contribution-mappings/mappings/{mappingId}/link — confirm a manual provider link
    /// (body carries the external id/title), then cascade into the Rensaio DB for matching series
    /// (same semantics as External Mappings).
    /// </summary>
    [HttpPost("mappings/{mappingId:guid}/link")]
    public async Task<ActionResult> LinkMapping(Guid mappingId, [FromQuery] ExternalSeriesProvider provider,
        [FromBody] ContributionMappingLinkDto dto, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();

        await _mappingService.ConfirmAsync(mappingId, provider, dto?.ExternalSeriesId ?? string.Empty,
            dto?.ExternalSeriesTitle, token).ConfigureAwait(false);

        // Automatch: the just-confirmed (provider, id) row is now a UserConfirmed seed. Re-running
        // the mapping scan searches ONLY the still-missing providers, detail-expands the confirmed
        // one, and links every OTHER provider it declares (LinkedSitesIds) — so a manual match
        // fills in the alternates exactly like an auto match would.
        var providers = await EnabledProvidersAsync(token).ConfigureAwait(false);
        await _mappingService.ScanMappingAsync(mappingId, providers, token).ConfigureAwait(false);
        await _mappingService.ReconcileSeriesGroupingAsync(token).ConfigureAwait(false);

        await CascadeAsync(mappingId, provider,
            apply: m => { m.ExternalSeriesId = dto?.ExternalSeriesId ?? string.Empty; m.ExternalSeriesTitle = dto?.ExternalSeriesTitle; m.MappingStatus = SeriesMappingStatus.UserConfirmed; m.LinkedDate = DateTime.UtcNow; },
            create: seriesId => new SeriesMappingEntity
            {
                Id = Guid.NewGuid(),
                SeriesId = seriesId,
                Provider = provider,
                ExternalSeriesId = dto?.ExternalSeriesId ?? string.Empty,
                ExternalSeriesTitle = dto?.ExternalSeriesTitle,
                MappingStatus = SeriesMappingStatus.UserConfirmed,
                LinkedDate = DateTime.UtcNow,
                UpdateDate = DateTime.UtcNow
            },
            automatch: true,
            token: token).ConfigureAwait(false);

        // Reassign: the previous match (now superseded by this user match) and any Blocked
        // provider must stop contributing their titles; reconcile re-derives the keep set from
        // the post-cascade Auto/User links.
        await ReconcileAsync(mappingId, token).ConfigureAwait(false);
        await _mappingService.ReconcileSeriesGroupingAsync(token).ConfigureAwait(false);
        return Ok(new { message = "Linked" });
    }

    /// <summary>
    /// POST /api/contribution-mappings/mappings/{mappingId}/{provider}/block?key={externalId} —
    /// block a SPECIFIC provider identity. The block is id-scoped (the provider stays searchable
    /// for a different id) and the key is required; a hard/keyless block is rejected.
    /// </summary>
    [HttpPost("mappings/{mappingId:guid}/{provider}/block")]
    public async Task<ActionResult> BlockMapping(Guid mappingId, string provider,
        [FromQuery] string? key = null, CancellationToken token = default)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();
        var providerEnum = ExternalMappingSupport.ParseProvider(provider);
        if (providerEnum == null) return BadRequest(new { message = "Invalid provider" });
        if (string.IsNullOrWhiteSpace(key)) return BadRequest(new { message = "A block requires a provider id to refuse." });

        try
        {
            await _mappingService.BlockAsync(mappingId, providerEnum.Value, key, token).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        await CascadeAsync(mappingId, providerEnum.Value,
            apply: m => m.MappingStatus = SeriesMappingStatus.Blocked,
            create: null, token: token).ConfigureAwait(false);
        // Remove every title / alternate the just-blocked association vouched for (unless another
        // live Auto/User provider still vouches for it). Reconcile is the only step that tombstones
        // stale title associations — without it a blocked provider keeps contributing its titles
        // until some later unrelated action (or the startup repair) happens to run.
        await ReconcileAsync(mappingId, token).ConfigureAwait(false);
        await _mappingService.ReconcileSeriesGroupingAsync(token).ConfigureAwait(false);
        return Ok(new { message = "Blocked" });
    }

    /// <summary>
    /// POST /api/contribution-mappings/mappings/{mappingId}/{provider}/unblock?key={externalId} —
    /// clear a block on a specific provider identity and return it to a single Unmatched row.
    /// </summary>
    [HttpPost("mappings/{mappingId:guid}/{provider}/unblock")]
    public async Task<ActionResult> UnblockMapping(Guid mappingId, string provider,
        [FromQuery] string? key = null, CancellationToken token = default)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();
        var providerEnum = ExternalMappingSupport.ParseProvider(provider);
        if (providerEnum == null) return BadRequest(new { message = "Invalid provider" });

        await _mappingService.UnblockAsync(mappingId, providerEnum.Value, key ?? string.Empty, token).ConfigureAwait(false);
        await CascadeAsync(mappingId, providerEnum.Value,
            apply: m => { m.MappingStatus = SeriesMappingStatus.AutoMatched; m.LinkedDate = DateTime.UtcNow; },
            create: null, token: token).ConfigureAwait(false);
        // Re-derive grouping after the decision flip so the un-blocked provider can rejoin.
        await _mappingService.ReconcileSeriesGroupingAsync(token).ConfigureAwait(false);
        return Ok(new { message = "Unblocked" });
    }

    /// <summary>POST /api/contribution-mappings/mappings/{mappingId}/{provider}/ignore — body {forever:bool}</summary>
    [HttpPost("mappings/{mappingId:guid}/{provider}/ignore")]
    public async Task<ActionResult> IgnoreMapping(Guid mappingId, string provider,
        [FromBody] ExternalMappingIgnoreDto dto, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();
        var providerEnum = ExternalMappingSupport.ParseProvider(provider);
        if (providerEnum == null) return BadRequest(new { message = "Invalid provider" });

        var forever = dto?.Forever ?? false;
        await _mappingService.IgnoreAsync(mappingId, providerEnum.Value, forever, token).ConfigureAwait(false);
        var status = forever ? SeriesMappingStatus.ForeverIgnored : SeriesMappingStatus.ManualIgnore;
        await CascadeAsync(mappingId, providerEnum.Value,
            apply: m => { m.MappingStatus = status; m.LinkedDate = DateTime.UtcNow; },
            create: null, token: token).ConfigureAwait(false);
        await ReconcileAsync(mappingId, token).ConfigureAwait(false);
        await _mappingService.ReconcileSeriesGroupingAsync(token).ConfigureAwait(false);
        return Ok(new { message = "Ignored" });
    }

    /// <summary>
    /// POST /api/contribution-mappings/mappings/{mappingId}/ignore-all — mark every Not Matched
    /// (Unmatched) provider of a contribution mapping as ManualIgnore ("Ignore All" = manual
    /// ignore), then cascade each ignored provider into the Rensaio DB for matching series —
    /// same semantics as the per-provider manual ignore action.
    /// </summary>
    [HttpPost("mappings/{mappingId:guid}/ignore-all")]
    public async Task<ActionResult> IgnoreAllUnmatchedMapping(Guid mappingId, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();

        var providers = await EnabledProvidersAsync(token).ConfigureAwait(false);
        var ignored = await _mappingService.IgnoreAllUnmatchedAsync(mappingId, providers, token).ConfigureAwait(false);

        foreach (var p in ignored)
        {
            await CascadeAsync(mappingId, p,
                apply: m => { m.MappingStatus = SeriesMappingStatus.ManualIgnore; m.LinkedDate = DateTime.UtcNow; },
                create: seriesId => new SeriesMappingEntity
                {
                    Id = Guid.NewGuid(),
                    SeriesId = seriesId,
                    Provider = p,
                    ExternalSeriesId = string.Empty,
                    MappingStatus = SeriesMappingStatus.ManualIgnore,
                    LinkedDate = DateTime.UtcNow,
                    UpdateDate = DateTime.UtcNow
                },
                token: token).ConfigureAwait(false);
        }

        await ReconcileAsync(mappingId, token).ConfigureAwait(false);
        await _mappingService.ReconcileSeriesGroupingAsync(token).ConfigureAwait(false);

        return Ok(new { message = ignored.Count > 0
            ? $"Ignored {ignored.Count} providers"
            : "No unmatched providers to ignore" });
    }

    /// <summary>DELETE /api/contribution-mappings/mappings/{mappingId}/{provider} — remove a mapping (tombstone).</summary>
    [HttpDelete("mappings/{mappingId:guid}/{provider}")]
    public async Task<ActionResult> UnlinkMapping(Guid mappingId, string provider, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();
        var providerEnum = ExternalMappingSupport.ParseProvider(provider);
        if (providerEnum == null) return BadRequest(new { message = "Invalid provider" });

        await _mappingService.UnlinkAsync(mappingId, providerEnum.Value, token).ConfigureAwait(false);
        await CascadeAsync(mappingId, providerEnum.Value, create: null, delete: true, token: token).ConfigureAwait(false);
        await ReconcileAsync(mappingId, token).ConfigureAwait(false);
        await _mappingService.ReconcileSeriesGroupingAsync(token).ConfigureAwait(false);
        return Ok(new { message = "Unlinked" });
    }

    /// <summary>
    /// POST /api/contribution-mappings/scan — kick off a full contribution mapping scan across all
    /// mappings in the background (SignalR progress, jobType = MetadataLink).
    /// </summary>
    [HttpPost("scan")]
    public async Task<ActionResult> ScanAll(CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();

        // Validated synchronously so the caller still gets a 404 when contribution is disabled.
        // The heavy scan runs in a BACKGROUND scope: the request scope (and its scoped
        // ContributionDbContext / ContributionMappingService) is disposed as soon as this method
        // returns, so capturing _mappingService or a request-scoped providers list would throw
        // ObjectDisposedException mid-scan. Mirrors MetadataBackgroundScanService.TriggerAsync.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var mappingService = scope.ServiceProvider.GetRequiredService<ContributionMappingService>();
                var support = scope.ServiceProvider.GetRequiredService<ExternalMappingSupport>();

                // The background context has NO HttpContext user, so we must NOT use
                // GetEnabledProvidersAsync(null, …): that returns only PUBLIC providers, which would
                // both fail to materialize Unmatched rows for OAuth providers (AniList/MyAnimeList/
                // Kitsu) and make step 6 treat those providers as "gone" and tombstone their live
                // Unmatched rows. Use the background rule instead: public + ANY authenticated config
                // (the same set the unattended worker uses).
                var enabledSet = await support.GetEnabledProviderSetAsync(CancellationToken.None).ConfigureAwait(false);
                var providers = support.GetMetadataProvidersInOrder()
                    .Where(p => enabledSet.Contains(p.ProviderType))
                    .ToList();
                await support.EnsureProvidersAuthenticatedAsync(providers, CancellationToken.None).ConfigureAwait(false);

                await mappingService.ScanAllMappingsAsync(providers, CancellationToken.None).ConfigureAwait(false);
                await mappingService.ReconcileSeriesGroupingAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Contribution scan-all background run failed");
            }
        });
        return Ok(new { message = "Scan started" });
    }

    /// <summary>
    /// POST /api/contribution-mappings/join — merge two or more contribution mappings into one
    /// series. Body carries the mapping (Series) ids to merge. Tombstones the absorbed
    /// series/title/metadata rows; no automatic matching is run.
    /// </summary>
    [HttpPost("join")]
    public async Task<ActionResult> Join([FromBody] ContributionMappingJoinDto dto, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();

        var ids = (dto?.MappingIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count < 2) return BadRequest(new { message = "Select at least two contributions to join." });

        try
        {
            var targetId = await _mappingService.JoinSeriesAsync(ids, token).ConfigureAwait(false);
            return Ok(new { message = "Joined", targetId });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Join of contribution mappings [{Ids}] failed", string.Join(", ", ids));
            return StatusCode(500, new { message = $"Join failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// POST /api/contribution-mappings/mappings/{mappingId}/split — split a multi-source
    /// contribution into one series per source entry. Tombstones the original series' title
    /// associations + metadata and re-parents each source entry under a new series id. No
    /// automatic matching is performed.
    /// </summary>
    [HttpPost("mappings/{mappingId:guid}/split")]
    public async Task<ActionResult> Split(Guid mappingId, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();

        try
        {
            var newIds = await _mappingService.SplitSeriesAsync(mappingId, token).ConfigureAwait(false);
            return Ok(new { message = "Split", seriesIds = newIds });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Split of contribution mapping {MappingId} failed", mappingId);
            return StatusCode(500, new { message = $"Split failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// POST /api/contribution-mappings/mappings/{mappingId}/verify — mark every live source entry
    /// of the mapping as Verified (VerifiedDateUTC = now UTC) and replicate the flag to the cloud.
    /// </summary>
    [HttpPost("mappings/{mappingId:guid}/verify")]
    public async Task<ActionResult> VerifyMapping(Guid mappingId, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();

        var count = await _mappingService.VerifyMappingAsync(mappingId, token).ConfigureAwait(false);
        return Ok(new { message = $"Verified {count} entries", count });
    }

    /// <summary>
    /// POST /api/contribution-mappings/mappings/{mappingId}/unverify — clear VerifiedDateUTC on
    /// every live source entry of the mapping. LOCAL-only: the cloud value is sticky and is not
    /// touched.
    /// </summary>
    [HttpPost("mappings/{mappingId:guid}/unverify")]
    public async Task<ActionResult> UnverifyMapping(Guid mappingId, CancellationToken token)
    {
        if (await ContributionDisabledAsync(token).ConfigureAwait(false)) return NotFound();

        var count = await _mappingService.UnverifyMappingAsync(mappingId, token).ConfigureAwait(false);
        return Ok(new { message = $"Un-verified {count} entries", count });
    }

    /// <summary>
    /// Best-effort title-association reconcile for a contribution mapping. Runs AFTER the
    /// Rensaio cascade so the provider-title index reflects the just-applied status change:
    /// a Blocked / removed / reassigned provider must stop contributing its titles to the
    /// series (unless another valid source still contributes them).
    /// </summary>
    private async Task ReconcileAsync(Guid mappingId, CancellationToken token)
    {
        try
        {
            await _titleHeuristics.ReconcileSeriesAsync(mappingId, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reconcile title associations for contribution mapping {MappingId}", mappingId);
        }
    }

    /// <summary>Best-effort cascade into the Rensaio DB for matching series.</summary>
    private async Task CascadeAsync(Guid mappingId, ExternalSeriesProvider provider,
        Action<SeriesMappingEntity>? apply = null, Func<Guid, SeriesMappingEntity>? create = null,
        bool delete = false, bool automatch = false, CancellationToken token = default)
    {
        try
        {
            await _cascade.CascadeAsync(mappingId, provider, apply, create, delete, automatch, token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cascade contribution mapping {MappingId} into Rensaio DB", mappingId);
        }
    }
}
