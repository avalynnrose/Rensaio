namespace Rensaio.RPC;

/// <summary>
/// Headless entry point for the isolated Mihon/IKVM/CEF process.
///
/// This is the single RPC host used by every deployment (Docker and the desktop tray):
/// See <see cref="RpcHost"/> for the shared configuration.
/// </summary>
public static class Program
{
    /// <summary>Protocol/version reported in the readiness handshake.</summary>
    public const string RpcVersion = "1.0.0";

    [STAThread]
    public static async Task Main(string[] args)
    {
        RpcHost.PrepareEnvironment();
        var app = RpcHost.Build(args);
        await RpcHost.RunAsync(app);
    }
}
