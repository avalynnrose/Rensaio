namespace Rensaio.RPC.Contracts;

/// <summary>
/// Transport descriptor for one source inside an extension.
///
/// Deliberately lives in the contract assembly (not in the RPC host) so the
/// backend can materialize an ISourceInterop proxy with the right metadata
/// without referencing any IKVM-bearing assembly. Mirrors the fields the
/// backend previously read straight off ISourceInterop.
/// </summary>
public sealed class SourceDescriptor
{
    /// <summary>Owning extension package id, so the source can be addressed as package|sourceId.</summary>
    public string PackageId { get; set; } = string.Empty;

    public long SourceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public bool SupportsLatest { get; set; }
    public bool IsConfigurable { get; set; }
    public bool IsHttpSource { get; set; }
    public bool IsParsedHttpSource { get; set; }
    public bool IsCatalogueSource { get; set; }
}
