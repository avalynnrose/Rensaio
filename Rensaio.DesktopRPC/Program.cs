using System.Runtime.InteropServices;
using Avalonia;
using Rensaio.RPC;

namespace Rensaio.DesktopRPC;

/// <summary>
/// Desktop entry point for the isolated Mihon/IKVM/CEF runtime.
///
/// Mirrors the historically-working tray (commit eb8a897): an Avalonia app whose UI thread
/// hosts the RPC ASP.NET app and drives the CEF message pump. There is no visible window —
/// Avalonia exists only to provide the Win32/COM UI thread the reverse-JNI CEF callbacks
/// need. Headless Docker uses <c>Rensaio.RPC</c> instead (plain ASP.NET, no Avalonia).
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Match the tray's desktop-process setup: a WinExe that also owns a console.
        // AllocConsole resets the standard handles, which would break the supervisor's
        // redirected stdout/stderr log pipe, so only allocate when output is NOT
        // redirected (i.e. a direct/interactive launch, exactly like the tray).
        if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected)
        {
            try { AllocConsole(); } catch { /* best effort */ }
        }

        // Same environment prep as the headless host (IKVM_VERBOSE, headless AWT, diagnostics).
        RpcHost.PrepareEnvironment();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(
            args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
