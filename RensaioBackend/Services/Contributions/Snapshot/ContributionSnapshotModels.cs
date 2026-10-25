using RensaioBackend.Models.ContributionDatabase;
using System.Text.Json.Serialization;

namespace RensaioBackend.Services.Contributions.Snapshot;

// ── Minimal base types mirroring PR #76's ContributionSnapshotModels.cs ──
// These are provisional stand-ins so the External Mappings feature can be built
// before PR #76 merges. They use the same type names and wire field names as the
// PR, so when the PR lands these classes are replaced/absorbed without rework.
// Sorry, heavly modified :(



/// <summary>Aggregate of the (decoded) on-disk snapshot. Titles + metadata + source records.</summary>
public sealed class ContributionSnapshotV1
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("e")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    [JsonPropertyName("u")]
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("t")]
    public List<TitleEntity> Titles { get; set; } = [];
    [JsonPropertyName("m")]
    public List<SeriesTitleEntity> Mappings { get; set; } = [];
    [JsonPropertyName("s")]
    public List<ContributionSourceEntity> Sources { get; set; } = [];
    [JsonPropertyName("x")]
    public List<ContributionSourceSeriesEntity> SourceSeries { get; set; } = [];
    [JsonPropertyName("i")]
    public List<ContributionSeriesEntity> Series { get; set; } = [];
    [JsonPropertyName("d")]
    public List<ContributionMetadataEntity> Metadata { get; set; } = [];

    [JsonPropertyName("v")]
    public int Version { get; set; }

}
