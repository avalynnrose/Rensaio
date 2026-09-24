using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Mihon.ExtensionsBridge.Core.Utilities;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Rensaio.RPC.Configuration;

namespace Rensaio.RPC.Runtime;

/// <summary>
/// Drives the CEF message pump the same way the Avalonia desktop build did: on a
/// dedicated STA UI thread that owns a running Win32 message pump.
///
/// Why this exact shape:
///   - The working desktop build pumped CEF from the Avalonia main thread, which is an
///     [STAThread] with a live Win32 message loop (GetMessage / TranslateMessage /
///     DispatchMessage). That thread existed for the whole process and serviced a
///     message queue.
///   - JCEF's OSR integration is AWT-based; its native side (renderer/IO/FILE threads)
///     raises reverse-JNI callbacks that must be serviced while a UI message pump is
///     running on the host thread. A bare worker thread — or an STA thread with no
///     message loop — does NOT provide that pump, which is what surfaces the
///     NullReferenceException at IKVM.Runtime.JNI.JavaVM.GetEnv(...).
///
/// On Windows this creates an STA thread, forces its Win32 message queue into existence,
/// and installs a Win32 timer so each WM_TIMER runs one CEF pump iteration while the thread
/// stays inside GetMessage/DispatchMessage. On non-Windows (Docker/Linux) there is no Win32
/// message pump and JCEF never initializes headlessly, so it degrades to a simple sleep loop.
/// </summary>
public sealed class RpcCefTimer : IStartCefTimer, IDisposable
{
    private readonly ILogger<RpcCefTimer> _logger;
    private readonly CefOptions _options;
    private Thread? _thread;
    private volatile bool _stop;
    private uint _threadId;
    private long _ticks;

    public RpcCefTimer(IOptions<RpcOptions> options, ILogger<RpcCefTimer> logger)
    {
        _options = options.Value.Cef;
        _logger = logger;
    }

    /// <summary>
    /// Starts the STA UI pump thread. Called by BridgeHost after applicationSetup
    /// (i.e. after IKVM/JCEF are up). No-op when CEF is disabled.
    /// </summary>
    public void StartCefPumpTimer()
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("CEF pump disabled by configuration (Rpc:Cef:Enabled=false).");
            return;
        }

        if (_thread != null)
            return;

        _stop = false;
        _thread = new Thread(RunPump)
        {
            IsBackground = true,
            Name = "rpc-cef-ui",
        };
        // UI threads on Windows are STA — this is the apartment the Avalonia UI thread had.
        // Apartment state is a Windows-only concept; on other OSes this is a no-op.
        if (OperatingSystem.IsWindows())
            _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void RunPump()
    {
        _threadId = GetCurrentThreadId();

        // Make the first transition into Java on THIS thread so IKVM/JCEF bind their UI
        // thread identity here (the Avalonia UI thread did the same on first use).
        try
        {
            _ = CefPumpBridge.CurrentIntervalMs();
        }
        catch
        {
            // Retried every tick below.
        }

        if (OperatingSystem.IsWindows())
        {
            // Desktop apps (Avalonia/WinForms/WPF) initialize OLE/COM on their UI thread.
            // JCEF's OSR browser uses an AWT component as an OLE drop target, so the UI
            // thread must have OLE initialized; a bare STA thread (SetApartmentState only
            // does CoInitializeEx) is not enough. This is another piece of the Avalonia
            // desktop threading model reproduced here.
            try
            {
                int hr = OleInitialize(IntPtr.Zero);
                _logger.LogInformation("OleInitialize on the CEF UI thread returned 0x{Hr:X8}.", hr);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OleInitialize on the CEF UI thread failed (continuing).");
            }

            WindowsMessageLoop();
        }
        else
        {
            SleepLoop();
        }
    }

    /// <summary>
    /// STA + Win32 message pump, matching the Avalonia main thread. A Win32 timer posts
    /// WM_TIMER to this thread's queue; each tick runs one CEF pump iteration, and the
    /// thread otherwise blocks in GetMessage servicing the queue.
    /// </summary>
    private void WindowsMessageLoop()
    {
        // Force the OS to create this thread's message queue before CEF native threads
        // may post to it.
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);

        var timerId = SetTimer(IntPtr.Zero, UIntPtr.Zero, (uint)CurrentInterval(), IntPtr.Zero);
        _logger.LogInformation(
            "RPC CEF pump running on STA UI thread 'rpc-cef-ui' with a Win32 message pump (timer {TimerId}).",
            timerId);

        try
        {
            while (!_stop && GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_TIMER)
                    Tick();

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            if (timerId != UIntPtr.Zero)
                KillTimer(IntPtr.Zero, timerId);
        }

        _logger.LogInformation("RPC CEF UI pump thread stopped.");
    }

    /// <summary>Non-Windows (Docker/Linux): no Win32 pump; JCEF never initializes headlessly.</summary>
    private void SleepLoop()
    {
        _logger.LogInformation("RPC CEF pump running on STA thread 'rpc-cef-ui' (no Win32 message pump on this OS).");

        while (!_stop)
        {
            Tick();
            try
            {
                Thread.Sleep(CurrentInterval());
            }
            catch (ThreadInterruptedException)
            {
                // Dispose() wakes us; loop condition decides.
            }
        }

        _logger.LogInformation("RPC CEF UI pump thread stopped.");
    }

    private void Tick()
    {
        try
        {
            if (CefPumpBridge.IsAvailable)
            {
                CefPumpBridge.PumpWork();

                if (++_ticks % 200 == 0)
                    _logger.LogDebug("CEF pump alive (tick {Ticks}).", _ticks);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RPC CEF pump tick failed (best-effort, continuing).");
        }
    }

    private static int CurrentInterval()
    {
        try
        {
            return (int)Math.Clamp(CefPumpBridge.CurrentIntervalMs(), 1, 100);
        }
        catch
        {
            return 16;
        }
    }

    public void Dispose()
    {
        if (_stop)
            return;

        _stop = true;

        // Break the Win32 message loop by posting WM_QUIT to this thread's queue.
        if (OperatingSystem.IsWindows() && _threadId != 0)
        {
            try { PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { /* best effort */ }
        }

        var thread = _thread;
        _thread = null;
        if (thread != null)
        {
            try { thread.Interrupt(); } catch { /* best effort */ }
            try { thread.Join(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        }

        CefPumpBridge.Invalidate();
        _logger.LogInformation("RPC CEF message pump stopped.");
    }

    // ── Win32 interop ──────────────────────────────────────────────────────────
    private const uint WM_TIMER = 0x0113;
    private const uint WM_QUIT = 0x0012;
    private const uint PM_NOREMOVE = 0x0000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr pvReserved);
}
