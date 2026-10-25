using RensaioBackend.Models;
using RensaioBackend.Models.Dto;
using RensaioBackend.Services.Contributions;
using RensaioBackend.Services.Rpc;
using RensaioBackend.Services.Settings;

namespace RensaioBackend.Services.Background;

/// <summary>
/// One-shot seeding sweep, started only when <c>--seed [<extensionPackage>]</c> is present.
///
/// Orchestration only: it delegates the extension scanning / matching / writing to
/// <see cref="ExtensionSweepService"/> (which records series into the local
/// contribution database incrementally, page by page) and performs a single
/// contribution export at the very end. Scrobbling/export concerns stay here so
/// the scan itself can be driven from other entry points later.
/// </summary>
public sealed class SeedService : IWorkerService
{
    private readonly SeedOptions _options;
    private readonly ExtensionSweepService _sweep;
    private readonly ContributionUploadService _upload;
    private readonly SettingsService _settings;
    private readonly IExtensionRpcBridge _rpc;
    private readonly ILogger<SeedService> _logger;

    public SeedService(
        SeedOptions options,
        ExtensionSweepService sweep,
        ContributionUploadService upload,
        SettingsService settings,
        IExtensionRpcBridge rpc,
        ILogger<SeedService> logger)
    {
        _options = options;
        _sweep = sweep;
        _upload = upload;
        _settings = settings;
        _rpc = rpc;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SeedService: --seed not supplied; nothing to do.");
            return;
        }

        // --seed optionally consumes the next token as its package, so a trailing
        // `--seed ... --force` is ambiguous to read; the parser handles it either way, but
        // nudge the operator toward the unambiguous `--force --seed [pkg]` order.
        if (_options.ForceAfterSeed)
        {
            _logger.LogWarning(
                "SeedService: --force was supplied AFTER --seed. Prefer '--force --seed [pkg]' — --seed optionally takes a package parameter, so the trailing form is ambiguous.");
        }

        string scope = _options.AllOnline
            ? _options.StartPackage == null
                ? " over every online extension"
                : $" over every online extension from package '{_options.StartPackage}'"
            : _options.Packages.Count > 0
                ? $" over package(s) {string.Join(", ", _options.Packages)}"
                : _options.StartPackage == null
                    ? " over all installed extensions"
                    : $" from package '{_options.StartPackage}'";

        _logger.LogInformation("SeedService: starting sweep{Scope}{Mode}.",
            scope,
            _options.Force ? " (forced: metadata-matching every series)" : " (gated: only new series / series with a missing provider mapping)");

        SweepResult result;
        try
        {
            if (_options.AllOnline)
            {
                result = await _sweep.SweepAllOnlineAsync(_options.StartPackage, _options.Force, stoppingToken).ConfigureAwait(false);
            }
            else if (_options.Packages.Count > 0)
            {
                result = await _sweep.SweepPackagesAsync(_options.Packages, _options.Force, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                result = await _sweep.SweepAllAsync(_options.StartPackage, _options.Force, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Safety net: release any temporary extension left prepared (best-effort).
            try { await _rpc.ReleaseAllTemporaryExtensionsAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "SeedService: releasing temporary extensions failed; continuing."); }
        }

        _logger.LogInformation(
            "SeedService: sweep complete. Extensions: {Extensions}, sources: {Sources}, new series entries inserted: {Series}.",
            result.ExtensionsSwept, result.SourcesSwept, result.SeriesInserted);

        // ── export once, at the very end ──
        await ExportAsync(stoppingToken).ConfigureAwait(false);
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
