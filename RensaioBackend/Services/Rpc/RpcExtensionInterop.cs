using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Extensions;

namespace RensaioBackend.Services.Rpc;

/// <summary>
/// Backend-side <see cref="IExtensionInterop"/> whose sources are
/// <see cref="RpcSourceInterop"/> proxies. Preference methods round-trip to RPC
/// through the source proxy.
/// </summary>
public sealed class RpcExtensionInterop : IExtensionInterop
{
    private readonly List<ISourceInterop> _sources;

    public RpcExtensionInterop(string name, string packageId, List<ISourceInterop> sources, string version = "")
    {
        Name = name;
        Id = packageId;
        _sources = sources;
        Version = version ?? string.Empty;
    }

    public string Id { get; }

    public string Name { get; }

    /// <summary>
    /// Active extension version, populated from the repository group at resolve
    /// time. The backend uses it to detect a stale pinned interop; empty means
    /// "not resolved" (e.g. groups with no entries).
    /// </summary>
    public string Version { get; set; } = string.Empty;

    public List<ISourceInterop> Sources => _sources;

    /// <summary>
    /// Preferences are owned by RPC; the backend has no direct store for them.
    /// </summary>
    public Task<List<UniquePreference>> LoadPreferencesAsync(CancellationToken token)
        => Task.FromResult(new List<UniquePreference>());

    public Task SavePreferencesAsync(List<UniquePreference> press, CancellationToken token)
        => Task.CompletedTask;
}
