using RensaioBackend.Data;
using RensaioBackend.Extensions;
using RensaioBackend.Migration;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Models.Enums;
using RensaioBackend.Services.Scrobbling;
using RensaioBackend.Services.Bridge;
using RensaioBackend.Services.Helpers;
using RensaioBackend.Services.Jobs;
using RensaioBackend.Services.Providers;
using RensaioBackend.Services.ReadState;
using RensaioBackend.Services.Contributions;
using RensaioBackend.Services.Settings;
using RensaioBackend.Utils;
using Microsoft.EntityFrameworkCore;
using Mihon.ExtensionsBridge.Models.Abstractions;
using System.ComponentModel;

namespace RensaioBackend.Services.Background
{
    public class StartupHostedService : IHostedService, IDisposable
    {
        private readonly NouisanceFixer20ExtraLarge _fixes;
        private readonly ILogger<StartupHostedService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _config;
        private readonly List<Task> _workerTasks = new();
        private CancellationTokenSource? _workerCts;
        private bool _disposed = false;

        public StartupHostedService(ILogger<StartupHostedService> logger,
            IServiceScopeFactory scopeFactory,
            NouisanceFixer20ExtraLarge fixes,
            IConfiguration config)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _fixes = fixes;
            _config = config;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                try
                {
                    // Use a timeout for disposal
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    StopAsync(cts.Token).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during disposal of StartupHostedService");
                }
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        public async Task<bool> CheckStorageStatusAsync(AppDbContext db, SettingsDto settings, IHostApplicationLifetime lifetime, CancellationToken token = default)
        {

            Models.Database.SeriesEntity? series = await db.Series.AsNoTracking().OrderBy(a=>a.Id).FirstOrDefaultAsync(token).ConfigureAwait(false);

            bool hasArchiveFiles = ArchiveHelperService.ContainsArchiveFilesRecursive(settings.StorageFolder);
            if (!hasArchiveFiles && series!=null)
            {
                _logger.LogError("No archive files found in the storage folder. But database has content, shutting down...");
                lifetime.StopApplication();
                return false;
            }
            else if (hasArchiveFiles && series == null)
            {
                //We have archive files, but no series in the database, we start the wizard setup
                settings.IsWizardSetupComplete = false;
                settings.WizardSetupStepCompleted = 0;
            }
            else
            {
                // We have archive files and series in the database, or everything is empty, we can proceed
                settings.IsWizardSetupComplete = true;
                settings.WizardSetupStepCompleted = 0;
            }

            return true;
        }


        /// <summary>
        /// Ensures the local contributor database (contributor.db) exists and is
        /// migrated. On a fresh database, the schema is created via
        /// <c>EnsureCreatedAsync</c> and all known migrations are marked as applied so a
        /// later <c>MigrateAsync</c> no-ops (same bootstrap pattern as the main app DB).
        /// On an existing database, <c>MigrateAsync</c> applies any pending migrations.
        /// WAL journal mode is enabled in both cases.
        /// </summary>
        private static async Task EnsureContributionDbAsync(
            IConfiguration configuration, ILogger logger, CancellationToken cancellationToken)
        {
            var dbPath = EnvironmentSetup.ContributorDatabasePath(configuration);

            // Snapshot existence BEFORE opening the connection: executing any statement
            // (including the WAL pragma below) makes SQLite create the file, which would
            // otherwise force a fresh database down the MigrateAsync path instead of the
            // model-driven EnsureCreated path.
            var exists = File.Exists(dbPath);

            var options = new DbContextOptionsBuilder<ContributionDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .UseQueryTrackingBehavior(QueryTrackingBehavior.TrackAll)
                .Options;
            await using var db = new ContributionDbContext(options);
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);

            if (!exists)
            {
                await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
                await MarkContributionMigrationsAppliedAsync(db, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            }

            // Repair databases that already ran AddMetadataProviderTitleIds BEFORE the migration
            // backfilled the column: ADD COLUMN left every existing row NULL, and because the model
            // maps ProviderTitleIds as a non-nullable JSON list, materializing NULL throws
            // ("The data is NULL at ordinal 0"). This idempotent normalization removes any residual
            // NULLs so every read succeeds; the next scan / propagation / confirm repopulates them.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"Metadata\" SET \"provider_title_ids\" = '[]' WHERE \"provider_title_ids\" IS NULL;",
                cancellationToken).ConfigureAwait(false);

            // Same NULL-normalization for the new Sources.LatestIds JSON column.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"Sources\" SET \"LatestIds\" = '[]' WHERE \"LatestIds\" IS NULL;",
                cancellationToken).ConfigureAwait(false);

            // Repair databases that ran the FIRST (buggy) AddSourceSeriesAndPackageId migration:
            // it added the PackageId BLOB columns with a 0-length default, so every pre-existing
            // row holds an invalid blob and any read throws
            // "Guid BLOB must be exactly 16 bytes". Normalize NULL/0-length blobs to 16 zero bytes
            // (Guid.Empty) so materialization succeeds; the data migrator below then backfills the
            // real values.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"Sources\" SET \"PackageId\" = zeroblob(16) WHERE \"PackageId\" IS NULL OR length(\"PackageId\") <> 16;",
                cancellationToken).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"SeriesEntries\" SET \"PackageId\" = zeroblob(16) WHERE \"PackageId\" IS NULL OR length(\"PackageId\") <> 16;",
                cancellationToken).ConfigureAwait(false);

            // PART 2 of the SourceSeries normalization: backfill PackageId, consolidate
            // same-package same-url series entries onto one row, and populate SourceSeries.
            // Idempotent — a no-op once every source carries a PackageId. Best-effort: a failure
            // must not abort startup (the additive columns/table are already in place).
            try
            {
                await ContributionSourceSeriesMigrator.RunAsync(db, logger, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Contribution SourceSeries data migration failed; continuing.");
            }
        }
        /// <summary>
        /// Creates the EF migrations history table for the contributor database and
        /// records all known migrations as already applied (used after a fresh
        /// EnsureCreated so MigrateAsync won't try to re-create the schema).
        /// </summary>
        private static async Task MarkContributionMigrationsAppliedAsync(ContributionDbContext db, CancellationToken cancellationToken)
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL);",
                cancellationToken).ConfigureAwait(false);

            var efCoreVersion = typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "0.0.0";
            var allMigrations = db.Database.GetMigrations();
            foreach (var migrationId in allMigrations)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({0}, {1});",
                    new object[] { migrationId, efCoreVersion },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {

                using var scope = _scopeFactory.CreateScope();
                // Wait for BridgeHost (background) to fully initialize the bridge.
                // This awaits the full sequence: InitAndroidAppAsync (Kotlin/Android
                // compat layer, CEF, SettingsConfig) followed by BridgeManager.InitializeAsync
                // (repository discovery, extension validation, recompilation).
                // Once the gate completes, all bridge services are safe to use.
                var mihon = scope.ServiceProvider.GetRequiredService<IBridgeManager>();
                await mihon.InitializationCompleted.WaitAsync(cancellationToken);
                _logger.LogInformation("Bridge initialization gate passed — bridge is ready.");


               

                //Run migration if needed
                var migration = scope.ServiceProvider.GetRequiredService<MigrationService>();
                await migration.RunAsync(cancellationToken).ConfigureAwait(false);


                // Initialize other services
                var settingsService = scope.ServiceProvider.GetRequiredService<SettingsService>();
                var providerCacheService = scope.ServiceProvider.GetRequiredService<ProviderCacheService>();
                
                // Load settings
                SettingsDto settings = await settingsService.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
                settingsService.SetThreadSettings(settings);
                await settingsService.SetTimesSettingsAsync(settings, cancellationToken).ConfigureAwait(false);

                // Push bridge-global preferences (FlareSolverr/trawl endpoint, SOCKS proxy, browser)
                // into the RUNNING RPC host. The backend owns these settings, but the RPC host only
                // loads its own preferences.json at startup — without this push a configured
                // FlareSolverr endpoint sat unused and Cloudflare challenges failed silently.
                // Best-effort: a failure must not abort startup.
                try
                {
                    await settingsService.PushBridgePreferencesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to push bridge preferences to the RPC host at startup; continuing.");
                }
                AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                await BackfillMappingStatusAsync(db, cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);

                // Ensure the local contributor database exists and is migrated.
                // Path is derived from DefaultConnection (sibling contributor.db).
                await EnsureContributionDbAsync(_config, _logger, cancellationToken).ConfigureAwait(false);

                //await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
                await _fixes.FixThumbnailsOfSeriesWithMissingThumbnailsAsync(cancellationToken).ConfigureAwait(false);

                // Repair any series with an empty Type using genre → categorized-path → Unknown resolution.
                try
                {
                    await _fixes.FixEmptySeriesTypesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Startup series Type repair failed");
                }

                // Fix: Ensure IsLocal = true for all providers without MihonProviderId that aren't Unknown
                List<SeriesProviderEntity> localProviderFixes = await db.SeriesProviders
                    .Where(a => string.IsNullOrEmpty(a.MihonProviderId) && !a.IsUnknown && !a.IsLocal)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                if (localProviderFixes.Count > 0)
                {
                    _logger.LogInformation("Fixing {Count} SeriesProviders with missing IsLocal flag", localProviderFixes.Count);
                    foreach (var p in localProviderFixes)
                        p.IsLocal = true;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                // Read state cache is populated lazily on first OPDS access (bounded LRU) —
                // eager prefetch of every series×user read state at startup is unnecessary
                // memory pressure and was removed to keep the memory footprint flat.

                IHostApplicationLifetime lifetime = scope.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();
                JobManagementService jobManagement = scope.ServiceProvider.GetRequiredService<JobManagementService>();
                _logger.LogInformation("Checking Storage folder Status...");
                bool save = await CheckStorageStatusAsync(db, settings, lifetime, cancellationToken).ConfigureAwait(false);
                if (save)
                    await settingsService.SaveSettingsAsync(settings, true, cancellationToken).ConfigureAwait(false);
                // Cache providers
                _logger.LogInformation("Syncing Mihon Extensions Preferences.");
                await providerCacheService.RefreshCacheAsync(false, cancellationToken).ConfigureAwait(false);
                var jobs = await jobManagement.GetRecurringJobsByTypeAsync(JobType.DailyUpdate, cancellationToken).ConfigureAwait(false);
                if (jobs.Count == 0)
                {
                    await jobManagement.ScheduleRecurringJobAsync(JobType.DailyUpdate, (string?)null,null, null,false, TimeSpan.FromDays(1),Priority.Normal, cancellationToken).ConfigureAwait(false);
                }
                // Schedule health status check job (runs every hour)
                var statusJobs = await jobManagement.GetRecurringJobsByTypeAsync(JobType.StatusCheck, cancellationToken).ConfigureAwait(false);
                if (statusJobs.Count == 0)
                {
                    await jobManagement.ScheduleRecurringJobAsync(JobType.StatusCheck, (string?)null, null, null, false, TimeSpan.FromHours(1), Priority.Normal, cancellationToken).ConfigureAwait(false);
                }

                // Schedule daily series verification job
                var verifyJobs = await jobManagement.GetRecurringJobsByTypeAsync(JobType.VerifyAllSeries, cancellationToken).ConfigureAwait(false);
                if (verifyJobs.Count == 0)
                {
                    _logger.LogInformation("Scheduling daily series verification job...");
                    await jobManagement.ScheduleRecurringJobAsync(JobType.VerifyAllSeries, (string?)null, null, null, false, TimeSpan.FromDays(1), Priority.Low, cancellationToken).ConfigureAwait(false);
                }

                // Enqueue an immediate verification run at startup.
                // Use matching key "VerifyAllSeries" so the dedup check in EnqueueJobAsIsAsync
                // prevents a double-run if the scheduled job already enqueued one.
                _logger.LogWarning("Starting initial series integrity verification at startup. This may take a while depending on the library size and archive file sizes.");
                await jobManagement.EnqueueJobAsync(JobType.VerifyAllSeries, (string?)null, Priority.Low, "VerifyAllSeries", null, null, "Default", cancellationToken).ConfigureAwait(false);

                // Wrong-auto-linkage repair, contribution title reconciliation and provider-cover
                // backfill are NOT run here: they now run strictly sequentially (and under the
                // contribution DB gate) in ContributionRepairBackgroundService, started as a worker
                // below. This guarantees repair always completes BEFORE reconciliation starts.
                _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var workerToken = _workerCts.Token;
                _workerTasks.Add(StartWorker<JobQueueHostedService>(workerToken));
                _workerTasks.Add(StartWorker<JobScheduledHostedService>(workerToken));
                _workerTasks.Add(StartWorker<MetadataBackgroundScanService>(workerToken));
                _workerTasks.Add(StartWorker<ContributionUploadBackgroundService>(workerToken));
                _workerTasks.Add(StartWorker<ContributionImportBackgroundService>(workerToken));
                // Single orchestrator: conflict repair → title reconciliation → cover backfill,
                // run strictly sequentially under the contribution DB gate.
                _workerTasks.Add(StartWorker<ContributionRepairBackgroundService>(workerToken));

                // One-shot seeding sweep. Only started when --seed was supplied; without it
                // the worker is never registered and has zero cost.
                var seedOptions = scope.ServiceProvider.GetRequiredService<Models.SeedOptions>();
                if (seedOptions.Enabled)
                {
                    _logger.LogInformation("Seed mode enabled — starting the contribution seed sweep.");
                    _workerTasks.Add(StartWorker<SeedService>(workerToken));
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting Startup Hosted Service");
                throw;
            }
        }

        private async Task BackfillMappingStatusAsync(AppDbContext db, CancellationToken cancellationToken)
        {
            try
            {
                // 1. Global rows: backfill the new shared enum defaults.
                var globalMappings = await db.SeriesMappings
                    .Where(m => m.MappingStatus == null || m.LinkedDate == null)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var m in globalMappings)
                {
                    if (m.MappingStatus == null)
                        m.MappingStatus = SeriesMappingStatus.AutoMatched;
                    if (m.LinkedDate == null)
                        m.LinkedDate = m.UpdateDate;
                }
                if (globalMappings.Count > 0)
                {
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Backfilled {Count} global series mappings status", globalMappings.Count);
                }

                // Note: the per-user UserSeriesMappings table has been dropped (global-only mappings).
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to backfill mapping status during startup");
            }
        }

        private Task StartWorker<TWorker>(CancellationToken workerToken) where TWorker : IWorkerService
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    // Resolution lives INSIDE the try: a missing DI registration would otherwise
                    // throw before the catch and silently fault the task, so the worker never runs.
                    var worker = scope.ServiceProvider.GetRequiredService<TWorker>();
                    await worker.ExecuteAsync(workerToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Worker {Worker} crashed", typeof(TWorker).Name);
                }
            });
            return task;
        }
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_workerCts == null)
                return;

            _workerCts.Cancel();

            try
            {
                // Use a dedicated 30-second timeout independent of the host's cancellation token
                // so workers have time to drain in-flight work even if the host cancels early.
                await Task.WhenAll(_workerTasks).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                _logger.LogInformation("All background workers stopped gracefully.");
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Background workers did not stop within the 30-second shutdown timeout.");
            }
            catch (OperationCanceledException)
            {
                // Swallow: host is shutting down, some workers may have been cancelled
            }
            finally
            {
                _workerCts.Dispose();
                _workerCts = null;
                _workerTasks.Clear();
            }
        }
    }
}
