using Avalonia.Threading;
using Mihon.ExtensionsBridge.Core.Utilities;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Microsoft.Extensions.Logging;

namespace Rensaio.DesktopRPC.Utils;

/// <summary>
/// The original tray CEF pump (commit eb8a897): an Avalonia <see cref="DispatcherTimer"/>
/// whose tick runs <see cref="CefPumpBridge.PumpWork"/> on the UI thread. This is the pump
/// that historically avoided the reverse-JNI <c>JavaVM.GetEnv</c> crash, because CEF's
/// native callbacks are serviced while an Avalonia UI thread (STA + Win32 message loop)
/// drives the process.
/// </summary>
public sealed class CefTimer : IStartCefTimer, IDisposable
{
    private readonly ILogger<CefTimer> _logger;
    private DispatcherTimer? _cefPumpTimer;

    public CefTimer(ILogger<CefTimer> logger) => _logger = logger;

    /// <summary>
    /// Starts a DispatcherTimer that drives CEF's message loop from the UI thread at 10 ms.
    /// </summary>
    public void StartCefPumpTimer()
    {
        if (_cefPumpTimer != null)
            return;

        Dispatcher.UIThread.Invoke(() =>
        {
            if (!CefPumpBridge.IsAvailable)
            {
                // JCEF not initialized (e.g. headless) — nothing to pump.
                return;
            }

            _cefPumpTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(10),
                DispatcherPriority.Background,
                (_, _) =>
                {
                    try
                    {
                        CefPumpBridge.PumpWork();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "CEF pump tick failed (continuing).");
                    }
                });

            _cefPumpTimer.Start();
            _logger.LogInformation("CEF external message pump timer started (DispatcherTimer, 10ms interval).");
        });
    }

    public void Dispose()
    {
        if (_cefPumpTimer == null)
            return;

        _cefPumpTimer.Stop();
        _cefPumpTimer = null;
        CefPumpBridge.Invalidate();
        _logger.LogInformation("CEF external message pump timer stopped.");
    }
}
