namespace Rensaio.RPC.Runtime;

/// <summary>
/// Holds the per-start token the backend passes on the command line and sends
/// on every call. It ensures only the backend that spawned this process can
/// drive the loopback endpoint, even though TCP on localhost is reachable by
/// any process on the host.
/// </summary>
public sealed class RpcStartupToken
{
    public RpcStartupToken(string token) => Token = token ?? string.Empty;

    /// <summary>The expected token. Empty means "no token configured".</summary>
    public string Token { get; }

    /// <summary>
    /// Returns true when the supplied token matches. An unconfigured token
    /// (empty) disables the check so local development still works.
    /// </summary>
    public bool IsValid(string? candidate)
        => string.IsNullOrEmpty(Token) || string.Equals(Token, candidate, StringComparison.Ordinal);
}
