namespace Rensaio.RPC;

/// <summary>
/// Headless entry point for the isolated Mihon/IKVM/CEF process.
///
/// This is the Docker/Linux host: plain ASP.NET hosting with no UI and no desktop pump
/// (the default <see cref="Runtime.RpcCefTimer"/> is a no-op when JCEF isn't available).
/// The desktop host (<c>Rensaio.DesktopRPC</c>) reuses <see cref="RpcHost"/> but injects an
/// Avalonia-UI-thread pump. See <see cref="RpcHost"/> for the shared configuration.
/// </summary>
public static class Program
{
    /// <summary>Protocol/version reported in the readiness handshake.</summary>
    public const string RpcVersion = "1.0.0";

    [STAThread]
    public static int Main(string[] args)
    {
        RpcHost.PrepareEnvironment();
        var app = RpcHost.Build(args);
        RpcHost.RunAsync(app).GetAwaiter().GetResult();
        return 0;
    }
}
