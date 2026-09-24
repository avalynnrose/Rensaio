using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rensaio.DesktopRPC.Utils;
using Rensaio.RPC;
using Mihon.ExtensionsBridge.Models.Abstractions;

namespace Rensaio.DesktopRPC;

/// <summary>
/// Minimal Avalonia application — no window, no tray icon. Its only job is to give the
/// process an Avalonia UI thread (STA + Win32 message loop) and host the RPC ASP.NET app on
/// it, with the CEF pump running on the UI thread (the eb8a897 tray architecture).
/// </summary>
public partial class App : Application
{
    private WebApplication? _rpc;
    private TrayIcon? _trayIcon;
    private readonly CancellationTokenSource _shutdownCts = new();
    private bool _shuttingDown;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownRequested += (_, e) =>
            {
                if (_shuttingDown)
                    return;
                _shuttingDown = true;
                e.Cancel = true; // shut down gracefully, then exit explicitly
                _ = Task.Run(async () =>
                {
                    await ShutdownAsync();
                    Environment.Exit(0);
                });
            };

            // Mirror the tray: create a REAL top-level notification-area window (TrayIcon)
            // on the UI thread. This ensures Avalonia sets up its full Win32 UI stack
            // (window + message loop + taskbar/notification host) exactly as the
            // historically-working tray did — not just a bare dispatcher thread.
            SetupTrayIcon();

            // Start the RPC host on a background task so the Avalonia UI loop stays responsive
            // (exactly like RensaioTray.StartApplication()).
            _ = Task.Run(StartRpcAsync);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartRpcAsync()
    {
        try
        {
            // Same ASP.NET app as the headless RPC, but inject the Avalonia-UI-thread pump.
            // configure runs last, so this IStartCefTimer replaces the headless default.
            // MUST forward the child's command line (--token, --paths:bridge/temp, ...), which
            // the supervisor passes exactly as it did to Rensaio.RPC.
            string[] childArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
            _rpc = RpcHost.Build(childArgs, services =>
            {
                services.AddSingleton<IStartCefTimer, CefTimer>();
            });

            await RpcHost.RunAsync(_rpc, _shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DesktopRPC] RPC host failed: {ex}");
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// Creates a notification-area icon (port of RensaioTray.SetupTrayIcon). A real
    /// Win32 window that keeps the Avalonia UI stack fully realized.
    /// </summary>
    private void SetupTrayIcon()
    {
        try
        {
            var trayIcon = new TrayIcon();

            try
            {
                var uri = new Uri("avares://Rensaio.DesktopRPC/Assets/rensaio.ico");
                using var stream = AssetLoader.Open(uri);
                var bitmap = new Bitmap(stream);
                trayIcon.Icon = new WindowIcon(bitmap);
            }
            catch
            {
                Console.WriteLine("[DesktopRPC] Could not load tray icon resource; using default icon");
            }

            trayIcon.ToolTipText = "Rensaiō (RPC)";
            trayIcon.IsVisible = true;
            _trayIcon = trayIcon;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DesktopRPC] Failed to setup tray icon: {ex.Message}");
        }
    }

    private async Task ShutdownAsync()
    {
        try
        {
            _shutdownCts.Cancel();
            if (_rpc != null)
                await ((IHost)_rpc).StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
        finally
        {
            _shutdownCts.Dispose();
        }
    }
}
