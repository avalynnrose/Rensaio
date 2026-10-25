using Mihon.ExtensionsBridge.Core.Abstractions;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;

namespace Rensaio.RPC.Services;

/// <summary>
/// Resolves an <see cref="ISourceInterop"/> from a package id and numeric source
/// id, mirroring the "package|sourceId" convention the backend used before the
/// split. Shared by the catalog and content gRPC services.
/// </summary>
public sealed class SourceResolver
{
    private readonly IInternalExtensionManager _extensions;
    private readonly ILogger<SourceResolver> _logger;

    public SourceResolver(IInternalExtensionManager extensions, ILogger<SourceResolver> logger)
    {
        _extensions = extensions;
        _logger = logger;
    }

    /// <summary>
    /// Finds the interop for the given package + source, or throws a
    /// <see cref="SourceNotFoundException"/> the services map to gRPC NotFound.
    /// </summary>
    public async Task<ISourceInterop> ResolveAsync(string packageId, long sourceId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            throw new SourceNotFoundException("Package id is required.");

        // Local-first with temporary fallback: a package prepared from an online repository for a
        // sweep is resolvable here too, without being part of the installed set.
        RepositoryGroup? group = _extensions.ListExtensions(includeTemporary: true)
            .FirstOrDefault(g => g.Entries.Any(e =>
                string.Equals(e.Extension?.Package, packageId, StringComparison.OrdinalIgnoreCase)));

        if (group == null)
            throw new SourceNotFoundException($"Extension '{packageId}' was not found.");

        IExtensionInterop interop = await _extensions.GetInteropAsync(group, token).ConfigureAwait(false);
        ISourceInterop? source = interop.Sources.FirstOrDefault(s => s.Id == sourceId);
        if (source == null)
            throw new SourceNotFoundException($"Source '{sourceId}' was not found in extension '{packageId}'.");

        return source;
    }
}

/// <summary>Raised when a package/source could not be resolved.</summary>
public sealed class SourceNotFoundException : Exception
{
    public SourceNotFoundException(string message) : base(message) { }
}

/// <summary>Raised when the caller's token does not match the per-start token.</summary>
public sealed class UnauthorizedRpcException : Exception
{
    public UnauthorizedRpcException() : base("Invalid or missing RPC token.") { }
}
