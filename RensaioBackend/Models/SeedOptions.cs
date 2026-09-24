namespace RensaioBackend.Models;

/// <summary>
/// Command-line options for the one-shot seeding sweep.
///
/// Triggered by <c>--seed [<extensionPackage>]</c>:
///   * <c>--seed</c>                       -> sweep every installed extension, first to last.
///   * <c>--seed eu.kanade...mangadex</c>   -> start at that package and continue to the end.
///   * no <c>--seed</c>                     -> the seed worker is never registered or started.
///
/// Parsed once at startup and exposed to DI, so the worker does not rely on static state.
/// </summary>
public sealed class SeedOptions
{
    /// <summary>True when <c>--seed</c> was passed (with or without a package).</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Optional starting extension package. Null/empty means "start from the first
    /// extension". When set and not found, the sweep aborts (see SeedService).
    /// </summary>
    public string? StartPackage { get; init; }

    /// <summary>
    /// Parses the process arguments. Recognizes <c>--seed</c> and <c>--seed=<pkg></c>
    /// plus the space-separated form <c>--seed <pkg></c>.
    /// </summary>
    public static SeedOptions Parse(IReadOnlyList<string> args)
    {
        if (args == null || args.Count == 0)
            return new SeedOptions { Enabled = false };

        for (int i = 0; i < args.Count; i++)
        {
            string raw = args[i];
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            string arg = raw.Trim();

            // "--seed=<pkg>"
            if (arg.StartsWith("--seed=", StringComparison.OrdinalIgnoreCase))
            {
                string value = arg["--seed=".Length..].Trim().Trim('"');
                return new SeedOptions { Enabled = true, StartPackage = NullIfEmpty(value) };
            }

            // "--seed [<pkg>]"
            if (arg.Equals("--seed", StringComparison.OrdinalIgnoreCase))
            {
                string? value = null;
                if (i + 1 < args.Count)
                {
                    string next = (args[i + 1] ?? string.Empty).Trim();
                    // Only treat the next token as the package when it is not another switch.
                    if (next.Length > 0 && !next.StartsWith('-'))
                        value = next.Trim('"');
                }
                return new SeedOptions { Enabled = true, StartPackage = NullIfEmpty(value) };
            }
        }

        return new SeedOptions { Enabled = false };
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
