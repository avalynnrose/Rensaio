using RensaioBackend.Models.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace RensaioBackend.Models.Database;

/// <summary>
/// Stores the global mapping between a local series and its ID on an external scrobbling service.
/// Unlike UserSeriesMappingEntity, this is shared across all users with role-based overwrite protection.
/// </summary>
public class SeriesMappingEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The local series ID. Nullable to support mappings without a linked local
    /// series (e.g. a provider decision scoped to an in-memory global title).
    /// </summary>
    public Guid? SeriesId { get; set; }

    /// <summary>
    /// The scrobbling provider (MyAnimeList, AniList, etc.).
    /// </summary>
    [Required]
    public ExternalSeriesProvider Provider { get; set; }

    /// <summary>
    /// The external series ID on the scrobbling service.
    /// </summary>
    [Required]
    public string ExternalSeriesId { get; set; } = string.Empty;


    /// <summary>
    /// Cached external title from the scrobbler.
    /// </summary>
    public string? ExternalSeriesTitle { get; set; }

    /// <summary>
    /// Cover image URL for the series as reported by the provider (last fetched via
    /// <see cref="IExternalSeriesProvider.FetchSeriesMetadataAsync"/> on approval).
    /// </summary>
    public string? SeriesCoverUrl { get; set; }

    /// <summary>
    /// Provider-native metadata stored as JSON (verbatim detail-call payload, e.g. the
    /// MangaBaka/Bangumi/MangaUpdates series object). Respects the provider's original structure.
    /// </summary>
    public string? MetaData { get; set; }

    /// <summary>
    /// Canonical "site:id" pairs of the same series on other sites.
    /// Example: ["anilist:30002", "myanimelist:2", "mangaupdates:njeqwry"].
    /// Stored as a comma-separated TEXT column via EF value conversion.
    /// </summary>
    public List<string> LinkedSitesIds { get; set; } = [];

    /// <summary>
    /// All alternate titles including the main title.
    /// Stored as a JSON TEXT column via EF value conversion.
    /// </summary>
    public List<string> AlternativeTitles { get; set; } = [];

    /// <summary>
    /// The user who last created or updated this mapping.
    /// </summary>
    public Guid? UserUid { get; set; }

    /// <summary>
    /// The user-level (role) at the time of update, used for overwrite priority.
    /// Higher-level users can overwrite mappings set by lower-level users.
    /// </summary>
    public UserLevel UserRole { get; set; }

    /// <summary>
    /// Timestamp of when this mapping was last created or updated.
    /// </summary>
    public DateTime UpdateDate { get; set; }

    /// <summary>
    /// App-wide link/decision state. Defaults to <see cref="SeriesMappingStatus.AutoMatched"/>
    /// when the row represents an active link (has an <see cref="ExternalSeriesId"/>). Use
    /// <see cref="SeriesMappingStatus.Blocked"/> / <see cref="SeriesMappingStatus.ManualIgnore"/> /
    /// <see cref="SeriesMappingStatus.ForeverIgnored"/> for refusals. The background scan reads
    /// this global value (never the per-user status).
    /// </summary>
    public SeriesMappingStatus MappingStatus { get; set; } = SeriesMappingStatus.AutoMatched;

    /// <summary>
    /// UTC timestamp when this global link/decision was made or confirmed. For a
    /// <see cref="SeriesMappingStatus.ManualIgnore"/> row it records when the manual ignore was
    /// triggered (informational — a manual ignore has no expiration and is never auto re-evaluated).
    ///
    /// Sentinel overload: when <see cref="MappingStatus"/> is <see cref="SeriesMappingStatus.Blocked"/>
    /// for a repair-created (auto-sealed) block, <see cref="LinkedDate"/> carries
    /// <see cref="AutoSealedSentinel"/> (a fixed value far in the past that can never collide with a
    /// real timestamp). This lets the scanner distinguish a MANUAL id-block (re-scan allowed — a
    /// different id may still match) from a REPAIR-SEALED block (settled — the whole provider is off
    /// for this series until the user unblocks). Used by <c>SeriesNeedsAttention</c>.
    /// </summary>
    public DateTime? LinkedDate { get; set; }

    /// <summary>
    /// Sentinel value stamped on <see cref="LinkedDate"/> for repair-created (auto-sealed) Blocked
    /// rows. Chosen far in the past (year 2000) so real UTC dates never collide.
    /// </summary>
    public static readonly DateTime AutoSealedSentinel =
        new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// True when this row is a REPAIR-SEALED id-block: <see cref="MappingStatus"/> is
    /// <see cref="SeriesMappingStatus.Blocked"/> and <see cref="LinkedDate"/> equals
    /// <see cref="AutoSealedSentinel"/>. Manual id-blocks (created via the UI/API Block action or the
    /// scrobbler) carry a real LinkedDate, so they return false and remain re-scannable.
    /// </summary>
    public bool IsAutoSealedBlock
        => MappingStatus == SeriesMappingStatus.Blocked
            && LinkedDate.HasValue
            && ValueCloseTo(LinkedDate.Value, AutoSealedSentinel);

    /// <summary>Roundtrip-safe comparison (SQLite TEXT stores 7-digit precision).</summary>
    private static bool ValueCloseTo(DateTime a, DateTime b)
        => Math.Abs((a - b).TotalSeconds) < 1.0;

    [ForeignKey(nameof(SeriesId))]
    public SeriesEntity? Series { get; set; }
}