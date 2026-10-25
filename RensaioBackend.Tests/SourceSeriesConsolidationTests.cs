using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RensaioBackend.Data;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Services.Contributions;
using Xunit;

namespace RensaioBackend.Tests;

/// <summary>
/// Focused tests for the package-scoped series identity and the many-to-many <c>SourceSeries</c>
/// association:
///   * two sources of the SAME package exposing the SAME url collapse onto ONE series entry with
///     one association per source;
///   * identical urls from DIFFERENT packages remain distinct entries.
/// </summary>
public sealed class SourceSeriesConsolidationTests
{
    private const string Package = "eu.kanade.tachiyomi.test";
    private const string OtherPackage = "eu.kanade.tachiyomi.other";

    [Fact]
    public async Task SamePackageSameUrl_Consolidates_ToOneEntry_TwoAssociations()
    {
        using var fx = new Fixture();
        var seriesId = Guid.NewGuid();
        fx.Db.Series.Add(new SeriesEntity { Id = seriesId });
        await fx.Db.SaveChangesAsync();

        long sourceA = 1, sourceB = 2;
        var entryA = await fx.WriteEntryAsync(seriesId, Package, sourceA, "url-1");
        var entryB = await fx.WriteEntryAsync(seriesId, Package, sourceB, "url-1");

        Assert.Equal(entryA, entryB);

        fx.Db.ChangeTracker.Clear();
        Assert.Equal(1, await fx.Db.SeriesEntries.CountAsync());

        var associations = await fx.Db.SourceSeries.AsNoTracking().ToListAsync();
        Assert.Equal(2, associations.Count);
        Assert.Contains(associations, a => a.SourceId == ContributionSourceEntity.DeriveId(Package, sourceA));
        Assert.Contains(associations, a => a.SourceId == ContributionSourceEntity.DeriveId(Package, sourceB));
    }

    [Fact]
    public async Task DifferentPackageSameUrl_RemainsDistinct()
    {
        using var fx = new Fixture();

        var seriesId1 = Guid.NewGuid();
        var seriesId2 = Guid.NewGuid();
        fx.Db.Series.Add(new SeriesEntity { Id = seriesId1 });
        fx.Db.Series.Add(new SeriesEntity { Id = seriesId2 });
        await fx.Db.SaveChangesAsync();

        var entryA = await fx.WriteEntryAsync(seriesId1, Package, 1, "url-1");
        var entryB = await fx.WriteEntryAsync(seriesId2, OtherPackage, 1, "url-1");

        Assert.NotEqual(entryA, entryB);

        fx.Db.ChangeTracker.Clear();
        Assert.Equal(2, await fx.Db.SeriesEntries.CountAsync());
        Assert.Equal(2, await fx.Db.SourceSeries.CountAsync());
    }

    [Fact]
    public async Task DataMigrator_ConsolidatesLegacyRows_AndBackfillsPackageId()
    {
        using var fx = new Fixture();

        // Legacy shape: two sources of the same package, each with its OWN entry for the same url
        // (old id = package:sourceId:url), no associations, and empty PackageId everywhere.
        var sourceA = ContributionSourceEntity.DeriveId(Package, 1);
        var sourceB = ContributionSourceEntity.DeriveId(Package, 2);
        fx.Db.Sources.Add(new ContributionSourceEntity { Id = sourceA, Package = Package, SourceId = 1, SourceName = "A", Version = -2 });
        fx.Db.Sources.Add(new ContributionSourceEntity { Id = sourceB, Package = Package, SourceId = 2, SourceName = "B", Version = -2 });

        var seriesId1 = Guid.NewGuid();
        var seriesId2 = Guid.NewGuid();
        fx.Db.Series.Add(new SeriesEntity { Id = seriesId1 });
        fx.Db.Series.Add(new SeriesEntity { Id = seriesId2 });

        // Legacy ids deliberately mirror the OLD derivation (package:sourceId:url) so they differ.
        var legacyIdA = LegacyDeriveId(Package, 1, "url-1");
        var legacyIdB = LegacyDeriveId(Package, 2, "url-1");
        fx.Db.SeriesEntries.Add(new ContributionSeriesEntity
        {
            Id = legacyIdA, SeriesId = seriesId1, SourceId = sourceA,
            Data = new ContributionRecordV2 { Url = "url-1", TitleId = Guid.NewGuid() }, Version = -2,
        });
        fx.Db.SeriesEntries.Add(new ContributionSeriesEntity
        {
            Id = legacyIdB, SeriesId = seriesId2, SourceId = sourceB,
            Data = new ContributionRecordV2 { Url = "url-1", TitleId = Guid.NewGuid() }, Version = -2,
        });
        await fx.Db.SaveChangesAsync();

        await ContributionSourceSeriesMigrator.RunAsync(
            fx.Db, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);

        fx.Db.ChangeTracker.Clear();

        // The two same-package same-url entries collapse to ONE live entry (the other tombstoned).
        var live = await fx.Db.SeriesEntries
            .Where(e => e.Version != ContributionVersions.PendingDelete
                && e.Version != ContributionVersions.UploadedDelete)
            .ToListAsync();
        Assert.Single(live);
        Assert.NotEqual(Guid.Empty, live[0].PackageId);

        // One association per contributing source.
        var associations = await fx.Db.SourceSeries.AsNoTracking().ToListAsync();
        Assert.Equal(2, associations.Count);

        // Both sources carry a PackageId.
        var sources = await fx.Db.Sources.AsNoTracking().ToListAsync();
        Assert.All(sources, s => Assert.Equal(ContributionSourceEntity.DerivePackageId(Package), s.PackageId));
    }

    [Fact]
    public async Task DataMigrator_ReKeysLoneLegacyRow_AndBuildsAssociation()
    {
        using var fx = new Fixture();

        // A single legacy row (old id = package:sourceId:url) with no association and empty
        // PackageId — the guard must still re-key it onto the canonical package:url id.
        var sourceA = ContributionSourceEntity.DeriveId(Package, 5);
        fx.Db.Sources.Add(new ContributionSourceEntity { Id = sourceA, Package = Package, SourceId = 5, SourceName = "A", Version = -2 });
        var seriesId = Guid.NewGuid();
        fx.Db.Series.Add(new SeriesEntity { Id = seriesId });
        var legacyId = LegacyDeriveId(Package, 5, "url-9");
        fx.Db.SeriesEntries.Add(new ContributionSeriesEntity
        {
            Id = legacyId, SeriesId = seriesId, SourceId = sourceA,
            Data = new ContributionRecordV2 { Url = "url-9", TitleId = Guid.NewGuid() }, Version = -2,
        });
        await fx.Db.SaveChangesAsync();

        await ContributionSourceSeriesMigrator.RunAsync(
            fx.Db, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        fx.Db.ChangeTracker.Clear();

        var canonicalId = ContributionSeriesEntity.DeriveId(Package, "url-9");
        var live = await fx.Db.SeriesEntries
            .Where(e => e.Version != ContributionVersions.PendingDelete
                && e.Version != ContributionVersions.UploadedDelete)
            .ToListAsync();
        Assert.Single(live);
        Assert.Equal(canonicalId, live[0].Id);

        var assoc = await fx.Db.SourceSeries.AsNoTracking().SingleAsync();
        Assert.Equal(sourceA, assoc.SourceId);
        Assert.Equal(canonicalId, assoc.SeriesEntityId);

        // The legacy row is tombstoned so the delete propagates to the cloud.
        Assert.True(await fx.Db.SeriesEntries.AsNoTracking().AnyAsync(e => e.Id == legacyId
            && (e.Version == ContributionVersions.PendingDelete || e.Version == ContributionVersions.UploadedDelete)));
    }

    private static Guid LegacyDeriveId(string package, long sourceId, string url)
    {
        var identity = package + ":" + sourceId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + url.ToLowerInvariant();
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        return new Guid(hash);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection _connection;

        public ContributionDbContext Db { get; }
        public ContributionGraphWriter Writer { get; }

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
        }

        public async Task<Guid> WriteEntryAsync(Guid seriesId, string package, long sourceNumericId, string url)
        {
            await Writer.EnsureSourceAsync(package, sourceNumericId, "Src", "en", null, null, CancellationToken.None);
            var sourceEntityId = ContributionSourceEntity.DeriveId(package, sourceNumericId);
            await Writer.UpsertSeriesRecordAsync(
                seriesId, sourceEntityId, package, url, NewRecord(url), CancellationToken.None);
            await Writer.SaveAsync(CancellationToken.None);
            Db.ChangeTracker.Clear();
            return ContributionSeriesEntity.DeriveId(package, url);
        }

        private static ContributionRecordV2 NewRecord(string url) => new()
        {
            SchemaVersion = ContributionRecordV2.CurrentSchemaVersion,
            Url = url,
            TitleId = Guid.NewGuid(),
            Status = 1,
        };

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }
}
