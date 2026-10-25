namespace RensaioBackend.Models;

/// <summary>
/// Command-line options for the one-shot seeding sweep.
///
/// Triggered by <c>--seed [<extensionPackage>]</c> or <c>--seed-all [<extensionPackage>]</c>:
///   * <c>--seed</c>                        -> sweep every installed extension, first to last.
///   * <c>--seed eu.kanade...mangadex</c>   -> start at that package and continue to the end.
///   * <c>--seed-all</c>                    -> sweep every online extension, first to last.
///   * <c>--seed-all eu.kanade...mangadex</c> -> start at that online package and continue to the end.
///   * no <c>--seed</c>/<c>--seed-all</c>   -> the seed worker is never registered or started.
///
/// Optional <c>--force</c> (used together with <c>--seed</c>):
///   * <c>--force --seed</c>                -> metadata-match EVERY swept series (current behavior).
///   * <c>--seed</c> (no <c>--force</c>)    -> metadata-match only a NEW series, or a series with a
///                                            missing mapping for its seriesSource (at least one
///                                            enabled metadata provider has no live row for it);
///                                            series fully covered by provider rows are skipped.
///
/// Preferred order is <c>--force --seed [pkg]</c>: <c>--seed</c> optionally consumes the next
/// token as its package, so a trailing <c>--force</c> is less readable. When <c>--force</c>
/// appears AFTER <c>--seed</c> the sweep still works (the parser never consumes a switch-shaped
/// token as the package), but <see cref="ForceAfterSeed"/> is set so the caller can warn.
///
/// Parsed once at startup and exposed to DI, so the worker does not rely on static state.
/// </summary>
public sealed class SeedOptions
{
    /// <summary>True when <c>--seed</c> was passed (with or without a package).</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Optional starting extension package, shared by both sweep modes. For <c>--seed</c> it is
    /// the first installed extension to sweep; for <c>--seed-all</c> the first online package to
    /// sweep. Null/empty means "start from the first extension". When set and not found, the
    /// sweep aborts (see SeedService).
    /// </summary>
    public string? StartPackage { get; init; }

    /// <summary>
    /// Parsed package names from <c>--seed</c>. A single package scans just that package,
    /// regardless of whether it is installed (an online-only package is prepared temporarily).
    /// Multiple packages may be supplied comma-separated.
    /// </summary>
    public IReadOnlyList<string> Packages { get; init; } = [];

    /// <summary>
    /// True when <c>--seed-all</c> was passed: sweep every extension offered by the configured
    /// online repositories (installed packages via the local path, the rest prepared temporarily).
    /// Like <c>--seed</c>, an optional package token sets <see cref="StartPackage"/> so the sweep
    /// begins at that online package and continues to the end.
    /// </summary>
    public bool AllOnline { get; init; }

    /// <summary>
    /// True when <c>--force</c> was passed: metadata-match every swept series (the historical
    /// behavior). When false (default) matching is gated to new series / series with a missing
    /// provider mapping. Only meaningful together with <see cref="Enabled"/>.
    /// </summary>
    public bool Force { get; init; }

    /// <summary>
    /// True when a <c>--force</c> token appeared AFTER the <c>--seed</c> token (i.e. the
    /// <c>--seed ... --force</c> form). Purely advisory: the parser handles either order, but the
    /// caller logs a warning recommending the unambiguous <c>--force --seed</c> order.
    /// </summary>
    public bool ForceAfterSeed { get; init; }

    /// <summary>
    /// Parses the process arguments. Recognizes <c>--seed</c>, <c>--seed=<pkg></c>,
    /// the space-separated form <c>--seed <pkg></c>, the <c>--seed-all</c>/<c>--all</c> switch
    /// (optionally with a start package: <c>--seed-all <pkg></c> / <c>--seed-all=<pkg></c>),
    /// and the <c>--force</c> flag.
    /// </summary>
    public static SeedOptions Parse(IReadOnlyList<string> args)
    {
        if (args == null || args.Count == 0)
            return new SeedOptions { Enabled = false };

        bool enabled = false;
        bool allOnline = false;
        string? startPackage = null;
        bool force = false;
        int seedIndex = -1;
        int forceIndex = -1;
        var packages = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            string raw = args[i];
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            string arg = raw.Trim();

            // "--seed-all=<pkg>" / "--all=<pkg>" — sweep every online extension starting at <pkg>.
            if (arg.StartsWith("--seed-all=", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--all=", StringComparison.OrdinalIgnoreCase))
            {
                string value = arg[(arg.IndexOf('=') + 1)..].Trim().Trim('"');
                enabled = true;
                allOnline = true;
                if (seedIndex < 0) seedIndex = i;
                startPackage ??= NullIfEmpty(value);
                continue;
            }

            // "--seed-all [<pkg>]" / "--all [<pkg>]" — sweep every online extension; an optional
            // following token names the STARTING package (sweep from there to the end), mirroring
            // the "--seed [<pkg>]" form.
            if (arg.Equals("--seed-all", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--all", StringComparison.OrdinalIgnoreCase))
            {
                enabled = true;
                allOnline = true;
                if (seedIndex < 0) seedIndex = i;

                if (i + 1 < args.Count && startPackage == null && packages.Count == 0)
                {
                    string next = (args[i + 1] ?? string.Empty).Trim();
                    // Only treat the next token as the package when it is not another switch.
                    if (next.Length > 0 && !next.StartsWith('-'))
                    {
                        next = next.Trim('"');
                        startPackage = NullIfEmpty(next);
                    }
                }
                continue;
            }

            // "--seed=<pkg>[,<pkg>...]"
            if (arg.StartsWith("--seed=", StringComparison.OrdinalIgnoreCase))
            {
                string value = arg["--seed=".Length..].Trim().Trim('"');
                enabled = true;
                if (seedIndex < 0) seedIndex = i;
                AddPackages(packages, value);
                startPackage ??= NullIfEmpty(value);
                continue;
            }

            // "--seed [<pkg>[,<pkg>...]]"
            if (arg.Equals("--seed", StringComparison.OrdinalIgnoreCase))
            {
                enabled = true;
                if (seedIndex < 0) seedIndex = i;

                if (i + 1 < args.Count && packages.Count == 0)
                {
                    string next = (args[i + 1] ?? string.Empty).Trim();
                    // Only treat the next token as the package when it is not another switch.
                    if (next.Length > 0 && !next.StartsWith('-'))
                    {
                        next = next.Trim('"');
                        AddPackages(packages, next);
                        startPackage = NullIfEmpty(next);
                    }
                }
                continue;
            }

            // "--force"
            if (arg.Equals("--force", StringComparison.OrdinalIgnoreCase))
            {
                force = true;
                if (forceIndex < 0) forceIndex = i;
                continue;
            }
        }

        return new SeedOptions
        {
            Enabled = enabled,
            AllOnline = allOnline,
            StartPackage = NullIfEmpty(startPackage),
            Packages = packages,
            Force = force,
            // A trailing --force is only "after seed" when a seed flag was actually supplied.
            ForceAfterSeed = enabled && force && forceIndex > seedIndex
        };
    }

    /// <summary>Splits a trimmed, comma-separated package list into <paramref name="target"/>.</summary>
    private static void AddPackages(List<string> target, string value)
    {
        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string p = part.Trim('"');
            if (p.Length > 0 && !target.Contains(p, StringComparer.OrdinalIgnoreCase))
                target.Add(p);
        }
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
