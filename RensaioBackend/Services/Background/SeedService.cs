using RensaioBackend.Models;
using RensaioBackend.Models.ContributionDatabase;
using RensaioBackend.Models.Dto;
using RensaioBackend.Services.Contributions;
using RensaioBackend.Services.Metadata;
using RensaioBackend.Services.Rpc;
using RensaioBackend.Services.Scrobbling.Abstractions;
using RensaioBackend.Services.Settings;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Extensions;
using Rensaio.RPC.Contracts;

namespace RensaioBackend.Services.Background;

/// <summary>
/// One-shot seeding sweep, started only when <c>--seed [<extensionPackage>]</c> is present.
///
/// It enumerates every series each installed extension's sources expose and records them
/// **only into the local contribution database** (<c>contributor.db</c>). It never writes
/// to rensaio.db, and never pulls details or chapters — the catalogue listing plus the
/// thumbnail is all that is needed. After a source is swept and matched, the contribution
/// database is exported.
///
/// Per source:
///   1. GetPopular over every page until HasNextPage is false  → SeenInPopular records
///   2. provider metadata match for the swept series (scoped)
///   3. if the source SupportsLatest, a GetLatest tail marks newly-seen series SeenInLatest
///   4. write title / mapping / source / series rows into contributor.db
///   5. export via ContributionUploadService (skipped when contributions are disabled)
/// </summary>
public sealed class SeedService : IWorkerService
{
    private readonly SeedOptions _options;
    private readonly IExtensionRpcBridge _rpc;
    private readonly ContributionGraphWriter _writer;
    private readonly ContributionMappingService _mapping;
    private readonly ContributionUploadService _upload;
    private readonly ExternalMappingSupport _providerSupport;
    private readonly SettingsService _settings;
    private readonly ILogger<SeedService> _logger;

    public SeedService(
        SeedOptions options,
        IExtensionRpcBridge rpc,
        ContributionGraphWriter writer,
        ContributionMappingService mapping,
        ContributionUploadService upload,
        ExternalMappingSupport providerSupport,
        SettingsService settings,
        ILogger<SeedService> logger)
    {
        _options = options;
        _rpc = rpc;
        _writer = writer;
        _mapping = mapping;
        _upload = upload;
        _providerSupport = providerSupport;
        _settings = settings;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SeedService: --seed not supplied; nothing to do.");
            return;
        }

        _logger.LogInformation("SeedService: starting sweep{Scope}.",
            _options.StartPackage == null ? " over all extensions" : $" from package '{_options.StartPackage}'");

        // Ordered list of installed extensions from RPC.
        List<RepositoryGroup> groups;
        try
        {
            groups = await _rpc.ListExtensionsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SeedService: unable to list extensions from RPC; aborting.");
            return;
        }

        int startIndex = 0;
        if (!string.IsNullOrWhiteSpace(_options.StartPackage))
        {
            startIndex = groups.FindIndex(g => g.Entries.Any(e =>
                string.Equals(e.Extension?.Package, _options.StartPackage, StringComparison.OrdinalIgnoreCase)));

            if (startIndex < 0)
            {
                _logger.LogError(
                    "SeedService: start package '{Package}' was not found among installed extensions; aborting.",
                    _options.StartPackage);
                return;
            }
        }

        int extensionsSwept = 0, sourcesSwept = 0, seriesTotal = 0;

        for (int i = startIndex; i < groups.Count && !stoppingToken.IsCancellationRequested; i++)
        {
            RepositoryGroup group = groups[i];
            RepositoryEntry? active = group.Entries.ElementAtOrDefault(group.ActiveEntry)
                                      ?? group.Entries.FirstOrDefault();
            string? package = active?.Extension?.Package;
            if (string.IsNullOrWhiteSpace(package))
                continue;

            _logger.LogInformation("SeedService: Started with extension {Name} ({Package}).",
                active?.Extension?.Name ?? group.Name, package);

            IReadOnlyList<SourceDescriptor> sources;
            try
            {
                sources = await _rpc.ListSourceDescriptorsAsync(package, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SeedService: unable to list sources for {Package}; skipping extension.", package);
                continue;
            }

            extensionsSwept++;
            foreach (SourceDescriptor source in sources)
            {
                if (stoppingToken.IsCancellationRequested) break;
                sourcesSwept++;

                int swept = await SweepSourceAsync(package, source, stoppingToken).ConfigureAwait(false);
                seriesTotal += swept;
            }
        }

        _logger.LogInformation(
            "SeedService: sweep complete. Extensions: {Extensions}, sources: {Sources}, series: {Series}.",
            extensionsSwept, sourcesSwept, seriesTotal);
    }

    /// <summary>Sweeps one source and returns the number of series recorded.</summary>
    private async Task<int> SweepSourceAsync(string package, SourceDescriptor source, CancellationToken token)
    {
        string label = $"{source.Name} ({source.Language})";
        try
        {
            ISourceInterop interop = await ResolveSourceAsync(package, source.SourceId, token).ConfigureAwait(false);

            // ── 1. GetPopular over all pages ──
            var collected = new Dictionary<string, Manga>(StringComparer.Ordinal);
            int page = 1;
            while (true)
            {
                MangaList list = await interop.GetPopularAsync(page, token).ConfigureAwait(false);
                if (list?.Mangas == null)
                    break;

                foreach (Manga m in list.Mangas)
                {
                    string key = string.IsNullOrEmpty(m.Url) ? m.Title ?? string.Empty : m.Url;
                    if (!string.IsNullOrEmpty(key))
                        collected[key] = m;
                }

                if (!list.HasNextPage)
                    break;
                page++;
            }

            _logger.LogInformation("SeedService: [{Source}] Obtained {Count} Series.", label, collected.Count);

            // ── 2. metadata match (scoped to the swept titles) ──
            _logger.LogInformation("SeedService: [{Source}] Matching metadata...", label);
            await MatchAsync(collected.Values, token).ConfigureAwait(false);

            // ── 3. GetLatest tail (only when supported) ──
            var latestKeys = new HashSet<string>(StringComparer.Ordinal);
            if (source.SupportsLatest)
            {
                _logger.LogInformation("SeedService: [{Source}] Source supports GetLatest — continuing.", label);
                int latestPage = 1;
                while (true)
                {
                    MangaList list = await interop.GetLatestAsync(latestPage, token).ConfigureAwait(false);
                    if (list?.Mangas == null)
                        break;

                    bool addedAny = false;
                    foreach (Manga m in list.Mangas)
                    {
                        string key = string.IsNullOrEmpty(m.Url) ? m.Title ?? string.Empty : m.Url;
                        if (string.IsNullOrEmpty(key))
                            continue;
                        latestKeys.Add(key);
                        if (!collected.ContainsKey(key))
                        {
                            collected[key] = m;
                            addedAny = true;
                        }
                    }

                    if (!list.HasNextPage || !addedAny)
                        break;
                    latestPage++;
                }
            }

            // ── 4. write to contributor.db (SeenInPopular / SeenInLatest set per collection) ──
            int written = await WriteAsync(package, source, collected, latestKeys, token).ConfigureAwait(false);

            // ── 5. export ──
            await ExportAsync(token).ConfigureAwait(false);

            _logger.LogInformation("SeedService: [{Source}] Done — {Written} series recorded.", label, written);
            return written;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SeedService: [{Source}] sweep failed; continuing.", label);
            return 0;
        }
    }

    private Task<ISourceInterop> ResolveSourceAsync(string package, long sourceId, CancellationToken token)
        => _rpc.GetSourceInteropAsync(package, sourceId, token);

    private async Task MatchAsync(IEnumerable<Manga> mangas, CancellationToken token)
    {
        List<string> titles = mangas
            .Select(m => m.Title)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (titles.Count == 0)
            return;

        List<Guid> mappingIds = await _mapping.FindMappingIdsByTitlesAsync(titles, token).ConfigureAwait(false);
        if (mappingIds.Count == 0)
            return;

        List<IExternalSeriesProvider> providers = _providerSupport.GetMetadataProvidersInOrder();
        await _providerSupport.EnsureProvidersAuthenticatedAsync(providers, token).ConfigureAwait(false);
        await _mapping.ScanMappingsAsync(mappingIds, providers, token).ConfigureAwait(false);
    }

    private async Task<int> WriteAsync(
        string package, SourceDescriptor source, Dictionary<string, Manga> collected,
        HashSet<string> latestKeys, CancellationToken token)
    {
        if (collected.Count == 0)
            return 0;

        // Resolve all titles up front (deterministic ids), then map each manga to a record.
        Dictionary<string, Guid> titleIds = await _writer
            .ResolveTitlesAsync(collected.Values.Select(m => m.Title ?? string.Empty), token)
            .ConfigureAwait(false);

        await _writer.EnsureSourceAsync(
            package, source.SourceId,
            string.IsNullOrWhiteSpace(source.Name) ? "Unknown" : source.Name,
            source.Language ?? string.Empty,
            DateTime.UtcNow, token).ConfigureAwait(false);

        Guid sourceEntityId = ContributionSourceEntity.DeriveId(package, source.SourceId);

        int written = 0;
        foreach ((string url, Manga manga) in collected)
        {
            if (string.IsNullOrWhiteSpace(manga.Title))
                continue;

            // One mapping per series, resolved from its own title set.
            var seriesTitles = new Dictionary<string, Guid>(StringComparer.Ordinal)
            {
                [TitleEntity.Normalize(manga.Title)] = titleIds[TitleEntity.Normalize(manga.Title)]
            };
            Guid mappingId = await _writer.ResolveMappingAsync(seriesTitles, manga.Title, token).ConfigureAwait(false);
            await _writer.SyncMappingTitlesAsync(mappingId, seriesTitles.Values, token).ConfigureAwait(false);

            var record = new ContributionRecordV1
            {
                SchemaVersion = ContributionRecordV1.CurrentSchemaVersion,
                TitleId = titleIds[TitleEntity.Normalize(manga.Title)],
                ThumbnailUrl = manga.ThumbnailUrl,
                Status = (int)manga.Status,
                SeenInPopular = true,
                SeenInLatest = latestKeys.Contains(url),
                LastUpdateUTC = DateTime.UtcNow
            };

            await _writer.UpsertSeriesRecordAsync(
                mappingId, sourceEntityId, package, source.SourceId, url, record, token).ConfigureAwait(false);
            written++;
        }

        await _writer.SaveAsync(token).ConfigureAwait(false);
        return written;
    }

    private async Task ExportAsync(CancellationToken token)
    {
        try
        {
            SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            if (!settings.ContributionEnabled || !settings.ContributionVerified)
            {
                _logger.LogInformation("SeedService: contribution export skipped (disabled/unverified).");
                return;
            }

            _logger.LogInformation("SeedService: Exporting contributions...");
            var result = await _upload.UploadPendingChangesAsync(false, token).ConfigureAwait(false);
            if (result.Success)
            {
                _logger.LogInformation(
                    "SeedService: export complete — {Total} rows ({Titles} titles, {Mappings} mappings, {Sources} sources, {Series} series, {Metadata} metadata).",
                    result.Total, result.Titles, result.Mappings, result.Sources, result.Series, result.Metadata);
            }
            else
            {
                _logger.LogWarning("SeedService: export failed — {Error}", result.Error);
            }
        }
        catch (Exception ex)
        {
            // Log-and-continue: a failed export must not abort the sweep.
            _logger.LogWarning(ex, "SeedService: export failed; continuing.");
        }
    }
}
