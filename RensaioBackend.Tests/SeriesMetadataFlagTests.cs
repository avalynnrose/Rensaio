using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RensaioBackend.Data;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Services.Background;
using RensaioBackend.Services.Contributions;
using Xunit;
// Both Models.Database and Models.ContributionDatabase declare a SeriesEntity; import only the enum
// by alias so the contribution SeriesEntity (the one this test builds) is unambiguous.
using SeriesMappingStatus = RensaioBackend.Models.Database.SeriesMappingStatus;

namespace RensaioBackend.Tests;

/// <summary>
/// Focused tests for the derived <see cref="ContributionSeriesEntity.Metadata"/> flag that backs the
/// two-phase sweep:
///   * the JSON round-trip preserves BOTH true and false (a <c>false</c> must never be dropped);
///   * <see cref="ContributionGraphRepairService.RecomputeSeriesMetadataFlagsAsync"/> derives the
///     flag from the <c>SeriesEntries → Metadata</c> relationship;
///   * <see cref="ContributionGraphWriter.UpsertSeriesRecordAsync"/> writes false on INSERT and
///     leaves the flag untouched on a plain UPDATE.
///
/// <see cref="ContributionSeriesEntity.Metadata"/> is a DERIVED denormalization (true = the owning
/// series has at least one live metadata row; never authoritative, never "matched").
/// </summary>
public sealed class SeriesMetadataFlagTests
{
    private const string Package = "eu.kanade.tachiyomi.test";
    private const long SourceNumericId = 42L;

    // ── A3: JSON round-trip ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonRoundTrip_PreservesFlag(bool value)
    {
        var row = new ContributionSeriesEntity
        {
            Id = Guid.NewGuid(),
            SeriesId = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            Data = new ContributionRecordV2 { Url = "u", TitleId = Guid.NewGuid() },
            Metadata = value,
            Version = 0,
        };

        string json = JsonSerializer.Serialize(row);
        Assert.Contains("\"q\":", json, StringComparison.Ordinal);

        var back = JsonSerializer.Deserialize<ContributionSeriesEntity>(json)!;
        Assert.Equal(value, back.Metadata);
    }

    [Fact]
    public void JsonRoundTrip_FalseIsSerialized_NotOmitted()
    {
        var row = new ContributionSeriesEntity
        {
            Id = Guid.NewGuid(),
            SeriesId = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            Data = new ContributionRecordV2(),
            Metadata = false,
            Version = 0,
        };

        // The upload payload must carry an explicit false so the worker does not read a default.
        string json = JsonSerializer.Serialize(row);
        Assert.Contains("\"q\":false", json, StringComparison.Ordinal);
    }

    // ── B1: recompute from the graph ──

    [Fact]
    public async Task Recompute_TrueWhenLiveUnmatchedPlaceholderExists()
    {
        using var fx = new Fixture();
        var seriesId = await fx.AddEntryAsync(metadata: SeriesMappingStatus.Unmatched);

        await fx.Repair.RecomputeSeriesMetadataFlagsAsync(
            new[] { seriesId }, CancellationToken.None);

        Assert.True(await fx.GetFlagAsync(seriesId));
    }

    [Fact]
    public async Task Recompute_TrueWhenActiveLinkExists()
    {
        using var fx = new Fixture();
        var seriesId = await fx.AddEntryAsync(metadata: SeriesMappingStatus.AutoMatched, providerKey: "123");

        await fx.Repair.RecomputeSeriesMetadataFlagsAsync(
            new[] { seriesId }, CancellationToken.None);

        Assert.True(await fx.GetFlagAsync(seriesId));
    }

    [Fact]
    public async Task Recompute_FalseWhenNoMetadataRowExists()
    {
        using var fx = new Fixture();
        var seriesId = await fx.AddEntryAsync(metadata: null);

        await fx.Repair.RecomputeSeriesMetadataFlagsAsync(
            new[] { seriesId }, CancellationToken.None);

        Assert.False(await fx.GetFlagAsync(seriesId));
    }

    [Fact]
    public async Task Recompute_FalseWhenMetadataRowIsTombstoned()
    {
        using var fx = new Fixture();
        var seriesId = await fx.AddEntryAsync(metadata: null);

        // A tombstoned (-1) metadata row must NOT count as coverage.
        fx.Db.Metadata.Add(new ContributionMetadataEntity
        {
            Id = Guid.NewGuid(),
            SeriesId = seriesId,
            ProviderId = 0,
            ProviderKey = string.Empty,
            MappingStatus = SeriesMappingStatus.Unmatched,
            Version = -1,
        });
        await fx.Db.SaveChangesAsync();

        await fx.Repair.RecomputeSeriesMetadataFlagsAsync(
            new[] { seriesId }, CancellationToken.None);

        Assert.False(await fx.GetFlagAsync(seriesId));
    }

    // ── C4: writer behaviour ──

    [Fact]
    public async Task Upsert_Insert_ReportsNew_AndWritesMetadataFalse()
    {
        using var fx = new Fixture();
        var seriesId = Guid.NewGuid();
        fx.Db.Series.Add(new SeriesEntity { Id = seriesId });
        await fx.Db.SaveChangesAsync();

        await fx.Writer.EnsureSourceAsync(
            Package, SourceNumericId, "Src", "en", null, null, CancellationToken.None);
        bool isNew = await fx.Writer.UpsertSeriesRecordAsync(
            seriesId, ContributionSourceEntity.DeriveId(Package, SourceNumericId),
            Package, "url-1", fx.NewRecord("url-1"), CancellationToken.None);
        await fx.Writer.SaveAsync(CancellationToken.None);

        // A brand-new row must report "new" and default the derived flag to false.
        Assert.True(isNew);
        var entry = await fx.Db.SeriesEntries.AsNoTracking().SingleAsync();
        Assert.False(entry.Metadata);
    }

    [Fact]
    public async Task Upsert_ReSweep_ReportsExisting_NotNew()
    {
        using var fx = new Fixture();
        var seriesId = Guid.NewGuid();
        fx.Db.Series.Add(new SeriesEntity { Id = seriesId });
        await fx.Db.SaveChangesAsync();

        var sourceEntityId = ContributionSourceEntity.DeriveId(Package, SourceNumericId);
        await fx.Writer.EnsureSourceAsync(
            Package, SourceNumericId, "Src", "en", null, null, CancellationToken.None);

        bool firstIsNew = await fx.Writer.UpsertSeriesRecordAsync(
            seriesId, sourceEntityId, Package, "url-1",
            fx.NewRecord("url-1"), CancellationToken.None);
        await fx.Writer.SaveAsync(CancellationToken.None);

        // Re-sweeping the SAME url with an unchanged payload must NOT report it as new — this is
        // what the per-page "new" log used to get wrong (an already-covered page looked 100% new).
        fx.Db.ChangeTracker.Clear();
        bool secondIsNew = await fx.Writer.UpsertSeriesRecordAsync(
            seriesId, sourceEntityId, Package, "url-1",
            fx.NewRecord("url-1"), CancellationToken.None);
        await fx.Writer.SaveAsync(CancellationToken.None);

        Assert.True(firstIsNew);
        Assert.False(secondIsNew);
    }

    [Fact]
    public async Task Upsert_RevivedTombstone_ReportsExisting_NotNew()
    {
        using var fx = new Fixture();
        var seriesId = await fx.AddEntryAsync(metadata: null);
        var sourceEntityId = ContributionSourceEntity.DeriveId(Package, SourceNumericId);

        // Tombstone the entry, then re-sweep it: the row already exists in contributor.db, so it is
        // an update (revival), never a new insert.
        var entry = await fx.Db.SeriesEntries.SingleAsync();
        entry.Version = -1;
        await fx.Db.SaveChangesAsync();
        fx.Db.ChangeTracker.Clear();

        bool isNew = await fx.Writer.UpsertSeriesRecordAsync(
            seriesId, sourceEntityId, Package, "url-1",
            fx.NewRecord("url-1"), CancellationToken.None);

        Assert.False(isNew);
    }

    [Fact]
    public async Task Upsert_Update_LeavesMetadataUnchanged()
    {
        using var fx = new Fixture();
        var seriesId = Guid.NewGuid();
        fx.Db.Series.Add(new SeriesEntity { Id = seriesId });
        await fx.Db.SaveChangesAsync();

        var sourceEntityId = ContributionSourceEntity.DeriveId(Package, SourceNumericId);
        await fx.Writer.EnsureSourceAsync(
            Package, SourceNumericId, "Src", "en", null, null, CancellationToken.None);
        await fx.Writer.UpsertSeriesRecordAsync(
            seriesId, sourceEntityId, Package, "url-1",
            fx.NewRecord("url-1"), CancellationToken.None);
        await fx.Writer.SaveAsync(CancellationToken.None);

        // Simulate the metadata pass having covered the series.
        fx.Db.ChangeTracker.Clear();
        var tracked = await fx.Db.SeriesEntries.SingleAsync();
        tracked.Metadata = true;
        await fx.Db.SaveChangesAsync();
        fx.Db.ChangeTracker.Clear();

        // A plain payload update must NOT touch the derived flag.
        var changed = fx.NewRecord("url-1");
        changed.ThumbnailUrl = "https://cdn/new.jpg";
        bool isNew = await fx.Writer.UpsertSeriesRecordAsync(
            seriesId, sourceEntityId, Package, "url-1",
            changed, CancellationToken.None);
        await fx.Writer.SaveAsync(CancellationToken.None);

        Assert.False(isNew);
        Assert.True(await fx.GetFlagAsync(seriesId));
    }

    // ── C1: flow toggle ──

    [Fact]
    public void TwoPhaseSweep_IsDefaultFlow()
    {
        // Guards against an accidental flip: the NEW flow is the default. The legacy per-page
        // path stays compiled behind this compile-time constant for rollback.
        Assert.True(ExtensionSweepService.UseTwoPhaseSweep);
    }

    // ── Fixture ──

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection _connection;

        public ContributionDbContext Db { get; }
        public ContributionGraphWriter Writer { get; }
        public ContributionGraphRepairService Repair { get; }

        public Fixture()
        {
            _connection = new SqliteConnection("DataSource=:memory:");
            _connection.Open();

            var options = new DbContextOptionsBuilder<ContributionDbContext>()
                .UseSqlite(_connection)
                .Options;

            Db = new ContributionDbContext(options);
            Db.Database.EnsureCreated();

            Writer = new ContributionGraphWriter(Db);
            var heuristics = new ContributionTitleHeuristicsService(
                Db, NullLogger<ContributionTitleHeuristicsService>.Instance);
            Repair = new ContributionGraphRepairService(
                Db, heuristics, NullLogger<ContributionGraphRepairService>.Instance);
        }

        public ContributionRecordV2 NewRecord(string url) => new()
        {
            SchemaVersion = ContributionRecordV2.CurrentSchemaVersion,
            Url = url,
            TitleId = Guid.NewGuid(),
            Status = 1,
        };

        /// <summary>Adds a source, a series and one entry; optionally a live metadata row.</summary>
        public async Task<Guid> AddEntryAsync(SeriesMappingStatus? metadata, string? providerKey = "")
        {
            var seriesId = Guid.NewGuid();
            var sourceEntityId = ContributionSourceEntity.DeriveId(Package, SourceNumericId);

            Db.Sources.Add(new ContributionSourceEntity
            {
                Id = sourceEntityId,
                Package = Package,
                PackageId = ContributionSourceEntity.DerivePackageId(Package),
                SourceId = SourceNumericId,
                SourceName = "Src",
                SourceLanguage = "en",
                Version = 0,
            });
            Db.Series.Add(new SeriesEntity { Id = seriesId });
            var entryId = ContributionSeriesEntity.DeriveId(Package, "url-1");
            Db.SeriesEntries.Add(new ContributionSeriesEntity
            {
                Id = entryId,
                SeriesId = seriesId,
                PackageId = ContributionSeriesEntity.DerivePackageId(Package),
                SourceId = sourceEntityId,
                Data = NewRecord("url-1"),
                Metadata = false,
                Version = 0,
            });
            Db.SourceSeries.Add(new ContributionSourceSeriesEntity
            {
                SourceId = sourceEntityId,
                SeriesEntityId = entryId,
                Version = 0,
            });

            if (metadata is not null)
            {
                Db.Metadata.Add(new ContributionMetadataEntity
                {
                    Id = Guid.NewGuid(),
                    SeriesId = seriesId,
                    ProviderId = 0,
                    ProviderKey = providerKey ?? string.Empty,
                    MappingStatus = metadata.Value,
                    Version = 0,
                });
            }

            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return seriesId;
        }

        public async Task<bool> GetFlagAsync(Guid seriesId)
        {
            Db.ChangeTracker.Clear();
            return await Db.SeriesEntries
                .Where(e => e.SeriesId == seriesId)
                .Select(e => e.Metadata)
                .FirstAsync();
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }
}
