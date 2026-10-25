namespace Rensaio.RPC.Contracts;

/// <summary>
/// Transport descriptor returned when an online extension is prepared for a sweep.
///
/// Deliberately lives in the contract assembly (not in the RPC host) so the backend can
/// materialize/observe the prepared package without referencing any IKVM-bearing assembly.
/// Mirrors the fields the backend needs to drive a per-source sweep.
/// </summary>
public sealed class OnlineExtensionDescriptor
{
    /// <summary>Owning extension package id.</summary>
    public string PackageId { get; set; } = string.Empty;

    /// <summary>Extension display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Active version string of the prepared extension.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// True when the prepared group is a Temporary (online-prepared) group rather than an
    /// already-installed extension.
    /// </summary>
    public bool Temporary { get; set; }

    /// <summary>Descriptors of every source the prepared extension exposes.</summary>
    public List<SourceDescriptor> Sources { get; set; } = new();
}
