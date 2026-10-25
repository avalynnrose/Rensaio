using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Services.Contributions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System.Text.Json;
using RensaioBackend.Data.Converters;

namespace RensaioBackend.Data
{
    /// <summary>
    /// Design-time factory used by EF tooling (migration generation). Points at a local
    /// dev <c>contributor.db</c> so <c>dotnet ef migrate</c> targets the right database.
    /// </summary>
    public class ContributionDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ContributionDbContext>
    {
        public ContributionDbContext CreateDbContext(string[] args)
        {
            var options = new DbContextOptionsBuilder<ContributionDbContext>()
                .UseSqlite("Data Source=C:\\users\\mpiva\\appdata\\local\\rensaio\\contributor.db")
                .Options;
            return new ContributionDbContext(options);
        }
    }

    /// <summary>
    /// Entity Framework context for the local contributor database (contributor.db).
    /// Mirrors the Cloudflare contributor worker schema in normalized form with
    /// <c>Version</c> columns and a <c>SyncState</c> replication cursor.
    ///
    /// Physical table/column names are wired explicitly: the aggregate root is <c>Series</c>
    /// (CLR <see cref="SeriesEntity"/>), the per-source entries live in <c>SeriesEntries</c>
    /// (CLR <see cref="ContributionSeriesEntity"/>) and every FK column is named
    /// <c>series_id</c>. These names are LOCAL only — the cloud snapshot is serialized from
    /// the one-letter <c>[JsonPropertyName]</c> attributes, never from the physical names.
    ///
    /// All 16-byte identity columns are true binary <c>BLOB(16)</c> columns via
    /// <see cref="GuidToBlob16Converter"/> — NOT the SQLite TEXT representation.
    /// </summary>
    public class ContributionDbContext : DbContext
    {
        public ContributionDbContext(DbContextOptions<ContributionDbContext> options) : base(options)
        {
        }

        /// <summary>
        /// Any series entry still holding a legacy (<see cref="ContributionRecordV1"/>) payload is
        /// no longer managed: stamp it as a pending delete so it is tombstoned locally and in the
        /// cloud, then regenerated as a <see cref="ContributionRecordV2"/> by the normal sweep.
        /// Runs on every save, immediately before changes are persisted.
        /// </summary>
        private void TombstoneLegacySeriesEntries()
        {
            foreach (var entry in ChangeTracker.Entries<ContributionSeriesEntity>())
            {
                if (entry.Entity.Data is { IsLegacy: true }
                    && !ContributionVersions.IsDeleted(entry.Entity.Version))
                {
                    entry.Entity.Version = ContributionVersions.PendingDelete;
                    entry.Property(nameof(ContributionSeriesEntity.Version)).IsModified = true;
                }
            }
        }

        private int _verificationInvalidationSuppressors;

        /// <summary>
        /// Suppresses <see cref="InvalidateVerificationOnGraphChanges"/> for the lifetime of the
        /// returned scope. Used by the cloud-apply paths (<c>ContributionDownloadService</c> /
        /// <c>ContributionImportService</c>): a materially-changed record applied FROM the cloud must
        /// NOT clear a local verification. Nested scopes are supported (reference counted).
        /// </summary>
        public IDisposable SuppressVerificationInvalidation()
        {
            _verificationInvalidationSuppressors++;
            return new VerificationInvalidationSuppression(this);
        }

        private sealed class VerificationInvalidationSuppression : IDisposable
        {
            private readonly ContributionDbContext _context;
            private bool _disposed;

            public VerificationInvalidationSuppression(ContributionDbContext context) => _context = context;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_context._verificationInvalidationSuppressors > 0)
                    _context._verificationInvalidationSuppressors--;
            }
        }

        private int _autoDetectChangesSuppressors;

        /// <summary>
        /// Reference-counted suppression of <see cref="ChangeTracker.AutoDetectChangesEnabled"/> for a
        /// BATCH unit of work. Every LINQ query and every <c>Add</c>/<c>Attach</c> on a tracking context
        /// otherwise triggers a full <c>DetectChanges</c> that walks EVERY tracked entity — O(tracked).
        /// A long sweep/scan that performs many writes per item against ONE scoped context pays that
        /// cost on every call, so per-item time grows with the number of items already processed.
        /// Inside the returned scope the automatic pass is off; <see cref="SaveChanges(bool)"/> /
        /// <see cref="SaveChangesAsync"/> force a single explicit <c>DetectChanges</c>, and the write
        /// paths already set <c>IsModified</c> explicitly, so change tracking stays exact. Nested scopes
        /// are reference-counted so a composed batch never re-enables detection early.
        /// </summary>
        public IDisposable SuppressAutoDetectChanges()
        {
            if (_autoDetectChangesSuppressors++ == 0)
                ChangeTracker.AutoDetectChangesEnabled = false;
            return new AutoDetectChangesSuppression(this);
        }

        private sealed class AutoDetectChangesSuppression : IDisposable
        {
            private readonly ContributionDbContext _context;
            private bool _disposed;

            public AutoDetectChangesSuppression(ContributionDbContext context) => _context = context;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (_context._autoDetectChangesSuppressors > 0 && --_context._autoDetectChangesSuppressors == 0)
                    _context.ChangeTracker.AutoDetectChangesEnabled = true;
            }
        }

        /// <summary>
        /// Clears <c>VerifiedDateUTC</c> on every live source entry of any series whose source-entry
        /// or metadata graph MATERIALLY changed in this unit of work. This forces the user to
        /// re-verify a series after an add/delete or a material edit (from a scan or a manual
        /// intervention), while routine scan noise and local-only backfill never invalidate.
        ///
        /// Material source-entry changes: add/delete, or a change to <see cref="ContributionRecordV2"/>
        /// non-exempt fields (SchemaVersion, Url, TitleId, ThumbnailUrl, Memo). The exempt scan fields
        /// (Status, SeenInPopular, SeenInLatest, LastChapter, LastUpdateUTC) never invalidate.
        ///
        /// Material metadata changes: add/delete, or a change to a replicated field (ProviderId,
        /// ProviderKey, MappingStatus, LinkedDate). The local-only CoverUrl / ProviderTitleIds never
        /// invalidate.
        ///
        /// The clear is LOCAL-only: <c>Version</c> is deliberately NOT touched (the cloud value is
        /// sticky; a materially-changed row is already stamped pending by its own writer).
        /// </summary>
        private void InvalidateVerificationOnGraphChanges()
        {
            if (_verificationInvalidationSuppressors > 0)
                return;

            var dirtySeries = new HashSet<Guid>();

            // ── Source entries ──
            foreach (var entry in ChangeTracker.Entries<ContributionSeriesEntity>())
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                    case EntityState.Deleted:
                        dirtySeries.Add(entry.Entity.SeriesId);
                        break;
                    case EntityState.Modified:
                        var originalSeriesId = entry.OriginalValues
                            .GetValue<Guid>(nameof(ContributionSeriesEntity.SeriesId));
                        bool reparented = entry.Entity.SeriesId != originalSeriesId;
                        var originalData = entry.OriginalValues
                            .GetValue<ContributionRecordV2?>(nameof(ContributionSeriesEntity.Data));
                        if (reparented || ContributionRecordV2.HasMaterialChange(entry.Entity.Data, originalData))
                        {
                            dirtySeries.Add(entry.Entity.SeriesId);
                            if (reparented) dirtySeries.Add(originalSeriesId);
                        }
                        break;
                }
            }

            // ── Metadata rows ──
            foreach (var entry in ChangeTracker.Entries<ContributionMetadataEntity>())
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                    case EntityState.Deleted:
                        dirtySeries.Add(entry.Entity.SeriesId);
                        break;
                    case EntityState.Modified:
                        var originalSeriesId = entry.OriginalValues
                            .GetValue<Guid>(nameof(ContributionMetadataEntity.SeriesId));
                        bool reparented = entry.Entity.SeriesId != originalSeriesId;
                        bool replicatedChanged =
                            entry.Property(m => m.ProviderId).IsModified
                            || entry.Property(m => m.ProviderKey).IsModified
                            || entry.Property(m => m.MappingStatus).IsModified
                            || entry.Property(m => m.LinkedDate).IsModified;
                        if (reparented || replicatedChanged)
                        {
                            dirtySeries.Add(entry.Entity.SeriesId);
                            if (reparented) dirtySeries.Add(originalSeriesId);
                        }
                        break;
                }
            }

            if (dirtySeries.Count == 0)
                return;

            var dirtyIds = dirtySeries.ToList();
            var handled = new HashSet<Guid>();

            void Clear(ContributionSeriesEntity row)
            {
                if (!handled.Add(row.Id)) return;
                if (row.VerifiedDateUTC == null) return;
                row.VerifiedDateUTC = null;
                Entry(row).Property(s => s.VerifiedDateUTC).IsModified = true;
            }

            // Live entries already persisted for a dirty series that still carry a verification.
            var persisted = SeriesEntries
                .Where(s => dirtyIds.Contains(s.SeriesId)
                    && s.VerifiedDateUTC != null
                    && s.Version != ContributionVersions.PendingDelete
                    && s.Version != ContributionVersions.UploadedDelete)
                .ToList();
            foreach (var row in persisted) Clear(row);

            // Entries added/tracked in THIS unit of work (invisible to the query above).
            foreach (var tracked in ChangeTracker.Entries<ContributionSeriesEntity>()
                .Where(e => dirtySeries.Contains(e.Entity.SeriesId)
                    && ContributionVersions.IsLive(e.Entity.Version))
                .Select(e => e.Entity))
            {
                Clear(tracked);
            }
        }

        public override int SaveChanges(bool acceptAllChangesOnSuccess)
        {
            // Force detection up front: inside a SuppressAutoDetectChanges scope the automatic pass is
            // off, so the two graph hooks below (and the base save) must see the CURRENT entity states.
            ChangeTracker.DetectChanges();
            TombstoneLegacySeriesEntries();
            InvalidateVerificationOnGraphChanges();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        public override Task<int> SaveChangesAsync(
            bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            ChangeTracker.DetectChanges();
            TombstoneLegacySeriesEntries();
            InvalidateVerificationOnGraphChanges();
            return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        public DbSet<TitleEntity> Titles { get; set; }
        public DbSet<SeriesEntity> Series { get; set; }
        public DbSet<SeriesTitleEntity> SeriesTitles { get; set; }
        public DbSet<ContributionSeriesEntity> SeriesEntries { get; set; }
        public DbSet<ContributionSourceEntity> Sources { get; set; }
        public DbSet<ContributionSourceSeriesEntity> SourceSeries { get; set; }
        public DbSet<ContributionMetadataEntity> Metadata { get; set; }
        public DbSet<SyncStateEntity> SyncStates { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<TitleEntity>(entity =>
            {
                entity.ToTable("Titles");
                entity.HasKey(t => t.Id);
                entity.Property(t => t.Id).HasGuidToBlob16().HasColumnName("Id").HasColumnType("BLOB").IsRequired();
                entity.Property(t => t.Title).HasColumnName("Title").HasColumnType("TEXT").UseCollation("BINARY").IsRequired();
                entity.Property(t => t.Version).HasColumnName("Version").HasColumnType("INTEGER").IsRequired();
            });

            modelBuilder.Entity<SeriesEntity>(entity =>
            {
                entity.ToTable("Series");
                entity.HasKey(m => m.Id);
                entity.Property(m => m.Id).HasGuidToBlob16().HasColumnName("Id").HasColumnType("BLOB").IsRequired();
            });

            modelBuilder.Entity<SeriesTitleEntity>(entity =>
            {
                entity.ToTable("SeriesTitles");
                entity.HasKey(mt => new { mt.SeriesId, mt.TitleId });
                entity.Property(mt => mt.SeriesId).HasGuidToBlob16().HasColumnName("series_id").HasColumnType("BLOB").IsRequired();
                entity.Property(mt => mt.TitleId).HasGuidToBlob16().HasColumnName("title_id").HasColumnType("BLOB").IsRequired();
                entity.Property(mt => mt.Version).HasColumnName("version").HasColumnType("INTEGER").IsRequired();
                entity.HasOne(mt => mt.Series).WithMany().HasForeignKey(mt => mt.SeriesId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(mt => mt.Title).WithMany().HasForeignKey(mt => mt.TitleId).OnDelete(DeleteBehavior.Cascade);
                // Per-series reads (reconcile / mapping detail / merge / split) filter the LIVE
                // version set (`series_id = ? AND version NOT IN (-1,-3)`). The series_id-led
                // composite seeks straight past the tombstoned rows that dominate the table.
                entity.HasIndex(mt => new { mt.SeriesId, mt.Version }).HasDatabaseName("IX_SeriesTitles_series_id_version");
                // ReconcileAllAsync enumerates DISTINCT live series; a Version-led covering index
                // makes that a pure index scan and supersedes the old single-column IX_SeriesTitles_version.
                entity.HasIndex(mt => new { mt.Version, mt.SeriesId }).HasDatabaseName("IX_SeriesTitles_version_series_id");
                // Sweep / propagation / metadata-link title resolution resolves `title_id IN (...)`
                // filtered on the LIVE version. The title_id-led covering composite replaces the
                // convention FK index on title_id alone so the lookup never touches the table row.
                entity.HasIndex(mt => new { mt.TitleId, mt.SeriesId, mt.Version }).HasDatabaseName("IX_SeriesTitles_title_id_series_id_version");
            });

            modelBuilder.Entity<ContributionSourceEntity>(entity =>
            {
                entity.ToTable("Sources");
                entity.HasKey(s => s.Id);
                entity.Property(s => s.Id).HasGuidToBlob16().HasColumnName("Id").HasColumnType("BLOB").IsRequired();
                entity.Property(s => s.Package).HasColumnName("Package").HasColumnType("TEXT").UseCollation("BINARY").IsRequired();
                entity.Property(s => s.PackageId).HasGuidToBlob16().HasColumnName("PackageId").HasColumnType("BLOB");
                entity.Property(s => s.SourceId).HasColumnName("SourceId").HasColumnType("INTEGER").IsRequired();
                entity.Property(s => s.SourceName).HasColumnName("SourceName").HasColumnType("TEXT").UseCollation("BINARY").IsRequired();
                entity.Property(s => s.SourceLanguage).HasColumnName("SourceLanguage").HasColumnType("TEXT").UseCollation("BINARY").IsRequired();
                entity.Property(s => s.LastBatchExecutionUTC).HasColumnName("LastBatchExecutionUTC").HasColumnType("TEXT");
                entity.Property(s => s.LatestIds).HasJsonConversion<List<string>>().HasColumnName("LatestIds").HasColumnType("TEXT");
                entity.Property(s => s.Version).HasColumnName("Version").HasColumnType("INTEGER").IsRequired();
                entity.HasIndex(s => new { s.Package, s.SourceId }).IsUnique().HasDatabaseName("IX_Sources_Package_SourceId");
            });

            modelBuilder.Entity<ContributionSeriesEntity>(entity =>
            {
                entity.ToTable("SeriesEntries");
                entity.HasKey(s => s.Id);
                entity.Property(s => s.Id).HasGuidToBlob16().HasColumnName("Id").HasColumnType("BLOB").IsRequired();
                entity.Property(s => s.SeriesId).HasGuidToBlob16().HasColumnName("series_id").HasColumnType("BLOB").IsRequired();
                entity.Property(s => s.PackageId).HasGuidToBlob16().HasColumnName("PackageId").HasColumnType("BLOB");
                // LEGACY / representative source: nullable so the final migration can drop it once
                // the SourceSeries association table is authoritative.
                entity.Property(s => s.SourceId).HasGuidToBlob16().HasColumnName("SourceId").HasColumnType("BLOB");
                entity.Property(s => s.Data).HasJsonConversion<ContributionRecordV2>().HasColumnName("Data").HasColumnType("TEXT").UseCollation("BINARY");
                entity.Property(s => s.VerifiedDateUTC).HasColumnName("VerifiedDateUTC").HasColumnType("TEXT");
                // Derived "has a live metadata row" denormalization (default false). Not authoritative:
                // recomputed by ContributionGraphRepairService.RecomputeSeriesMetadataFlagsAsync.
                entity.Property(s => s.Metadata).HasColumnName("Metadata").HasColumnType("INTEGER").IsRequired();
                entity.Property(s => s.Version).HasColumnName("Version").HasColumnType("INTEGER").IsRequired();
                entity.HasOne(s => s.Series).WithMany().HasForeignKey(s => s.SeriesId).OnDelete(DeleteBehavior.Cascade);
                entity.HasOne(s => s.Source).WithMany().HasForeignKey(s => s.SourceId).OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(s => s.SeriesId).HasDatabaseName("IX_SeriesEntries_series_id");
                // Package-scoped identity: series entries are grouped by (PackageId, url) during the
                // migration/consolidation pass.
                entity.HasIndex(s => s.PackageId).HasDatabaseName("IX_SeriesEntries_PackageId");
                // The two-phase sweep's hot query enumerates series entries with Metadata = 0 (still
                // needing a metadata pass). This led index keeps that scan from touching the table row.
                entity.HasIndex(s => s.Metadata).HasDatabaseName("IX_SeriesEntries_Metadata");
                // The metadata-flag recompute derives the "covered" / "needs flip" series from
                // `Metadata = ? AND Version live`; the composite evaluates both predicates inside
                // the index and enumerates the DISTINCT series_id from it, so the (graph-wide)
                // recompute never touches the (large) table rows.
                entity.HasIndex(s => new { s.Metadata, s.Version }).HasDatabaseName("IX_SeriesEntries_Metadata_Version");
                // The upload sweep, the grouping reconcile and the propagation pass all filter the
                // whole table on SeriesId and/or the LIVE version set; the composite keeps the
                // per-series lookups covering. SourceId is already indexed by its FK.
                entity.HasIndex(s => new { s.SeriesId, s.Version }).HasDatabaseName("IX_SeriesEntries_series_id_Version");
                // Verified / Unverified views: the page derives a mapping's state from its live
                // entries (Verified = every live entry dated, Unverified = any null). The composite
                // keeps the per-mapping ANY/NOT-ANY checks covering; the single-column index supports
                // the graph-wide verified/unverified scans.
                entity.HasIndex(s => new { s.SeriesId, s.VerifiedDateUTC }).HasDatabaseName("IX_SeriesEntries_series_id_VerifiedDateUTC");
                entity.HasIndex(s => s.VerifiedDateUTC).HasDatabaseName("IX_SeriesEntries_VerifiedDateUTC");
                // The grouping reconcile and its cycle-guard signature scan the WHOLE table on the
                // LIVE version predicate (`Version NOT IN (-1,-3)`) with no series_id bound. Without
                // a Version index that is a bare full table SCAN; this keeps it from degrading as
                // the table grows.
                entity.HasIndex(s => s.Version).HasDatabaseName("IX_SeriesEntries_Version");
            });

            modelBuilder.Entity<ContributionSourceSeriesEntity>(entity =>
            {
                entity.ToTable("SourceSeries");
                entity.HasKey(ss => new { ss.SourceId, ss.SeriesEntityId });
                entity.Property(ss => ss.SourceId).HasGuidToBlob16().HasColumnName("source_id").HasColumnType("BLOB").IsRequired();
                entity.Property(ss => ss.SeriesEntityId).HasGuidToBlob16().HasColumnName("series_entity_id").HasColumnType("BLOB").IsRequired();
                entity.Property(ss => ss.Version).HasColumnName("version").HasColumnType("INTEGER").IsRequired();
                entity.HasOne(ss => ss.Source).WithMany().HasForeignKey(ss => ss.SourceId).OnDelete(DeleteBehavior.Cascade);
                // Cascade from the series entry so consolidating/removing an entry drops its
                // associations. Backing the FK directly is what the mapping/reconcile passes join on.
                entity.HasOne(ss => ss.SeriesEntity).WithMany(s => s.Sources).HasForeignKey(ss => ss.SeriesEntityId).OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(ss => new { ss.SeriesEntityId, ss.Version }).HasDatabaseName("IX_SourceSeries_series_entity_id_Version");
                entity.HasIndex(ss => new { ss.SourceId, ss.Version }).HasDatabaseName("IX_SourceSeries_source_id_Version");
                entity.HasIndex(ss => ss.Version).HasDatabaseName("IX_SourceSeries_Version");
            });

            modelBuilder.Entity<ContributionMetadataEntity>(entity =>
            {
                entity.ToTable("Metadata");
                entity.HasKey(m => m.Id);
                entity.Property(m => m.Id).HasGuidToBlob16().HasColumnName("Id").HasColumnType("BLOB").IsRequired();
                entity.Property(m => m.SeriesId).HasGuidToBlob16().HasColumnName("series_id").HasColumnType("BLOB").IsRequired();
                entity.Property(m => m.ProviderId).HasColumnName("ProviderId").HasColumnType("INTEGER").IsRequired();
                entity.Property(m => m.ProviderKey).HasColumnName("ProviderKey").HasColumnType("TEXT").IsRequired();
                entity.Property(m => m.MappingStatus).HasColumnName("MappingStatus").HasColumnType("INTEGER").IsRequired().HasConversion<int>();
                entity.Property(m => m.CoverUrl).HasColumnName("cover_url").HasColumnType("TEXT");
                // Local-only: the provider-reported title ids authoritatively kept alive by the
                // reconcile pass. [JsonIgnore] and not part of the protobuf export.
                entity.Property(m => m.ProviderTitleIds).HasJsonConversion<List<Guid>>().HasColumnName("provider_title_ids").HasColumnType("TEXT");
                entity.Property(m => m.Version).HasColumnName("Version").HasColumnType("INTEGER").IsRequired();
                entity.HasOne(m => m.Series).WithMany().HasForeignKey(m => m.SeriesId).OnDelete(DeleteBehavior.Cascade);
                entity.HasIndex(m => m.SeriesId).HasDatabaseName("IX_Metadata_series_id");
                entity.HasIndex(m => new { m.ProviderId, m.ProviderKey }).HasDatabaseName("IX_Metadata_ProviderId_ProviderKey");
                entity.HasIndex(m => m.MappingStatus).HasDatabaseName("IX_Metadata_MappingType");
                // The Contribution page's Unmatched / Blocked / ManualIgnored combos filter a
                // mapping by the presence of a live metadata row of that status; the composite
                // (series_id, MappingStatus, Version) makes those per-mapping EXISTS checks fully
                // covering (the live-Version predicate is evaluated in the index).
                entity.HasIndex(m => new { m.SeriesId, m.MappingStatus, m.Version }).HasDatabaseName("IX_Metadata_series_id_MappingStatus_Version");
                // The graph-wide passes (grouping anchors, cover backfill, covered-provider scan)
                // filter on status and the LIVE version set with no series_id bound; a
                // MappingStatus-led composite keeps those from re-reading the table row.
                entity.HasIndex(m => new { m.MappingStatus, m.Version }).HasDatabaseName("IX_Metadata_MappingStatus_Version");
                // The Contribution page and every per-series read/mutation filter
                // `series_id = ? AND Version NOT IN (-1,-3)`; the composite makes those covering.
                entity.HasIndex(m => new { m.SeriesId, m.Version }).HasDatabaseName("IX_Metadata_series_id_Version");
                // The upload sweep and the graph-wide consolidate/reconcile passes scan Metadata
                // filtered only on the replication Version; the metadata-flag recompute enumerates
                // DISTINCT live series_id over the whole Metadata table (`Version live`). The
                // Version-led composite serves both and supersedes a single-column (Version) index.
                entity.HasIndex(m => new { m.Version, m.SeriesId }).HasDatabaseName("IX_Metadata_Version_series_id");
            });

            modelBuilder.Entity<SyncStateEntity>(entity =>
            {
                entity.ToTable("SyncStates");
                entity.HasKey(s => s.Id);
                entity.Property(s => s.Id).HasColumnType("INTEGER").IsRequired();
                entity.Property(s => s.LastServerVersion).HasColumnType("INTEGER").IsRequired();
            });
        }
    }
}