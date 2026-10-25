namespace RensaioBackend.Services.Rpc;

/// <summary>
/// Operator-controlled, per-extension-package rate limits for Mihon source
/// calls, bound from the "SourceRateLimits" configuration section.
///
/// Only packages listed in <see cref="Packages"/> are ever throttled; any other
/// package is untouched. Each package may limit the catalogue/metadata calls
/// (<see cref="PackageLimits.Methods"/>) and the binary content fetches
/// (<see cref="PackageLimits.Images"/>) independently. A slot left absent (or
/// null) leaves that dimension unthrottled, so a package can limit:
///   * both dimensions with the same values (joint behaviour),
///   * both dimensions with different values (separate behaviour),
///   * only Methods,
///   * only Images.
///
/// The values are surfaced into the process-wide
/// <see cref="RensaioBackend.Services.Scrobbling.Providers.ProviderRateLimits"/>
/// registry, which owns the actual token buckets.
/// </summary>
public sealed class SourceRateLimitsOptions
{
    public const string SectionName = "SourceRateLimits";

    /// <summary>Master switch. When false no source call is ever throttled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Fixed dictionary keyed by extension package id. Comparison is
    /// case-insensitive. Packages absent from this map are never throttled.
    /// </summary>
    public Dictionary<string, PackageLimits> Packages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Optional per-dimension buckets for a single package.</summary>
public sealed class PackageLimits
{
    /// <summary>Catalogue/metadata calls bucket, or null to leave that dimension unthrottled.</summary>
    public PackageRateLimit? Methods { get; set; }

    /// <summary>Page-image / direct-download bucket, or null to leave that dimension unthrottled.</summary>
    public PackageRateLimit? Images { get; set; }
}

/// <summary>
/// Token-bucket shape for one dimension, mirroring the option set used by
/// <see cref="RensaioBackend.Services.Scrobbling.Providers.ProviderRateLimits"/>.
/// </summary>
public sealed class PackageRateLimit
{
    /// <summary>Maximum number of permits held at once (burst size).</summary>
    public int TokenLimit { get; set; }

    /// <summary>Permits replenished every <see cref="Period"/>.</summary>
    public int TokensPerPeriod { get; set; }

    /// <summary>Replenishment period. Defaults to one second.</summary>
    public TimeSpan Period { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Bounded wait queue so bursts queue up rather than being dropped.</summary>
    public int QueueLimit { get; set; } = 4;
}
