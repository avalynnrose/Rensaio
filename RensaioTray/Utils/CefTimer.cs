using Microsoft.Extensions.Logging;
using Mihon.ExtensionsBridge.Core.Utilities;
using Mihon.ExtensionsBridge.Models.Abstractions;
using System;

namespace RensaioTray.Utils
{
    /// <summary>
    /// Drives the CEF message pump from the Avalonia UI thread (desktop, in-process hosting).
    ///
    /// A separate child process hosting IKVM/CEF fails with a reverse-JNI crash in
    /// <c>IKVM.Runtime.JNI.JavaVM.GetEnv</c>; running the runtime in-process on the tray's
    /// Avalonia UI thread is the known-good configuration. The pump is an Avalonia
    /// <c>DispatcherTimer</c> whose tick calls <see cref="CefPumpBridge.PumpWork"/> on the UI
    /// thread, so CEF's native callbacks are serviced on a fully realized Avalonia thread.
    /// </summary>
    public class CefTimer : IStartCefTimer, IDisposable
    {
        private bool _isDisposed = false;
        private readonly ILogger _logger;
        private Avalonia.Threading.DispatcherTimer? _cefPumpTimer;

        public CefTimer(ILogger<CefTimer> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Starts an Avalonia DispatcherTimer that drives CEF's message loop from the UI
        /// thread. Replaces the internal Java daemon thread, avoiding reverse-JNI crashes
        /// from CEF native threads entering IKVM's JNI without a valid GetEnv() attachment.
        /// </summary>
        public void StartCefPumpTimer()
        {
            if (_cefPumpTimer != null)
                return;

            Avalonia.Threading.Dispatcher.UIThread.Invoke(() =>
            {
                if (!CefPumpBridge.IsAvailable)
                {
                    // JCEF not initialized (e.g. headless) — nothing to pump.
                    return;
                }

                _cefPumpTimer = new Avalonia.Threading.DispatcherTimer(
                    TimeSpan.FromMilliseconds(10),
                    Avalonia.Threading.DispatcherPriority.Background,
                    (sender, e) =>
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
            if (_isDisposed)
                return;

            if (_cefPumpTimer == null)
                return;

            _cefPumpTimer.Stop();
            _cefPumpTimer = null;
            CefPumpBridge.Invalidate();
            _logger.LogInformation("CEF external message pump timer stopped.");
            _isDisposed = true;
        }
    }
}
