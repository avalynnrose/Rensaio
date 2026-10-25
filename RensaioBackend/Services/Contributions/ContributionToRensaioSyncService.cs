using RensaioBackend.Data;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace RensaioBackend.Services.Contributions
{
    /// <summary>
    /// Step 3 of the import pipeline: after a cloud snapshot is applied to the local
    /// Contribution DB, propagate confirmed/auto matches into rensaio.db SeriesMappings.
    ///
    /// For every local contribution metadata row carrying a real provider key and a
    /// match state (AutoMatched or UserConfirmed), we resolve the Rensaio series by
    /// normalized-title overlap (reusing <see cref="RensaioMappingCascadeService"/>) and
    /// write the provider key + status into the matching SeriesMapping rows — unless the
    /// existing Rensaio row is a stronger decision (UserConfirmed / Blocked), which is
    /// never downgraded.
    ///
    /// Cloud-cascaded BLOCKED rows are id-scoped decisions of the shared community dataset,
    /// NOT a local user's deliberate refusal, so they are written with the auto-sealed
    /// sentinel (<see cref="SeriesMappingEntity.AutoSealedSentinel"/>) — the same marker the
    /// conflict repair and the link-engine ownership guard use. This keeps a stale cloud
    /// block (e.g. a block belonging to a series that was later deleted locally and then
    /// re-cascaded onto a same-title twin) from being mistaken for a manual user block:
    /// an explicit user confirm of the same id (or a different id) always overrides it.
    /// </summary>
    public sealed class ContributionToRensaioSyncService
    {
        private const int VersionUploaded = -2; // clean/imported rows

        private readonly AppDbContext _db;
        private readonly ContributionDbContext _contributorDb;
        private readonly RensaioMappingCascadeService _cascade;
        private readonly ILogger<ContributionToRensaioSyncService> _logger;

        public ContributionToRensaioSyncService(
            AppDbContext db,
            ContributionDbContext contributorDb,
            RensaioMappingCascadeService cascade,
            ILogger<ContributionToRensaioSyncService> logger)
        {
            _db = db;
            _contributorDb = contributorDb;
            _cascade = cascade;
            _logger = logger;
        }

        /// <summary>
        /// Walks all local contribution metadata rows and cascades Auto/User matches into
        /// rensaio.db SeriesMappings. Best-effort, never throws.
        /// </summary>
        public async Task SyncAsync(CancellationToken token = default)
        {
            try
            {
                // Auto / User-matched rows cascade as active links; Blocked rows carrying a real
                // provider key cascade as a Blocked decision so a wrong auto-link repaired in the
                // cloud is also cleared in rensaio.db on every client. Id-level blocks (real key)
                // only — hard blocks (empty key) are refusals for providers we don't track here.
                var matches = await _contributorDb.Metadata
                    .Include(m => m.Series)
                    .Where(m => m.Version == VersionUploaded
                        && (m.MappingStatus == SeriesMappingStatus.AutoMatched
                            || m.MappingStatus == SeriesMappingStatus.UserConfirmed
                            || m.MappingStatus == SeriesMappingStatus.Blocked)
                        && m.ProviderKey != null
                        && m.ProviderKey != string.Empty)
                    .AsNoTracking()
                    .ToListAsync(token).ConfigureAwait(false);
                if (matches.Count == 0)
                    return;

                foreach (var match in matches)
                {
                    var provider = (ExternalSeriesProvider)match.ProviderId;
                    var isBlocked = match.MappingStatus == SeriesMappingStatus.Blocked;
                    // A cloud-cascaded block is a community-dataset id decision, not a deliberate
                    // local user refusal — write it with the auto-sealed sentinel so the scanner
                    // treats it as settled but an explicit user confirm of the same id always wins
                    // (see ConfirmMatchAsync's IsAutoSealedBlock override).
                    var linkedDate = isBlocked ? SeriesMappingEntity.AutoSealedSentinel : DateTime.UtcNow;
                    await _cascade.CascadeAsync(match.SeriesId, provider,
                        apply: m =>
                        {
                            // Never downgrade a stronger Rensaio decision.
                            if (m.MappingStatus is SeriesMappingStatus.UserConfirmed or SeriesMappingStatus.Blocked)
                                return;
                            m.ExternalSeriesId = match.ProviderKey!;
                            m.MappingStatus = isBlocked
                                ? SeriesMappingStatus.Blocked
                                : SeriesMappingStatus.AutoMatched;
                            m.LinkedDate = linkedDate;
                        },
                        create: _ => new SeriesMappingEntity
                        {
                            Id = Guid.NewGuid(),
                            Provider = provider,
                            ExternalSeriesId = match.ProviderKey!,
                            MappingStatus = isBlocked
                                ? SeriesMappingStatus.Blocked
                                : SeriesMappingStatus.AutoMatched,
                            LinkedDate = linkedDate,
                        },
                        token: token).ConfigureAwait(false);
                    await _db.SaveChangesAsync(token).ConfigureAwait(false);
                }

                _logger.LogInformation("Contribution→Rensaio sync applied {Count} matches/blocks", matches.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Contribution→Rensaio sync failed");
            }
        }
    }
}