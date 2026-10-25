using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Rensaio.RPC.Runtime;

/// <summary>
/// Re-asserts <c>java.awt.headless=true</c> immediately before the extension runtime
/// (IKVM/AndroidCompat/JCEF) initializes, and logs the AWT state at that exact moment.
///
/// Why: the windowed AWT toolkit (<c>sun.awt.windows.WToolkit</c>) runs a native
/// <c>eventLoop</c> thread; when that thread calls back into IKVM without a valid per-thread
/// JNIEnv the process dies in <c>IKVM.Runtime.JNI.JavaVM.GetEnv</c>. The headless toolkit
/// (<c>sun.awt.HeadlessToolkit</c>) has no such native event loop.
///
/// This guard (re-added/enhanced):
///  - sets every headless-related property we know of BEFORE any AWT class loads;
///  - logs the property, <c>isHeadless()</c>, the resolved toolkit class, and the CURRENT
///    thread, so we can see which toolkit JCEF ended up with and on which thread;
///  - specifically flags when the windowed toolkit is in play.
///
/// Registered BEFORE AddExtensionsBridge so it starts first (hosted services start in
/// registration order).
/// </summary>
public sealed class AwtHeadlessGuard : IHostedService
{
    private readonly ILogger<AwtHeadlessGuard> _logger;

    public AwtHeadlessGuard(ILogger<AwtHeadlessGuard> logger) => _logger = logger;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Re-assert headless as late as possible, before the bridge (IKVM/CEF) initializes.
            java.lang.System.setProperty("java.awt.headless", "true");
            // Belt-and-braces: some AWT/2D paths consult this as well.
            try { java.lang.System.setProperty("sun.java2d.headless", "true"); } catch { /* optional */ }

            // DO NOT touch `awt.toolkit` here — neither set NOR clear.
            //
            // Setting it to `sun.awt.HeadlessToolkit` fails: that class has ONLY a one-argument
            // constructor (HeadlessToolkit(Toolkit)), so Toolkit.getDefaultToolkit() cannot
            // no-arg-instantiate it:
            //   AWTError: Could not instantiate Toolkit: sun.awt.HeadlessToolkit
            //
            // Clearing it also fails: the runtime then has no toolkit name at all, and
            // Toolkit.getDefaultToolkit() -> java.awt.Toolkit$2.run calls
            // Class.forName(null, true, ...) which NPEs inside
            //   IKVM.Java.Externs.java.lang.Class.forName0
            //
            // IKVM supplies its own working default for `awt.toolkit`; `java.awt.headless=true`
            // below is what selects headless behaviour. Leave the property exactly as found.

            string? prop = java.lang.System.getProperty("java.awt.headless");
            // GraphicsEnvironment.isHeadless() only reads the headless flag (plus a display probe on
            // some platforms); unlike Toolkit.getDefaultToolkit() it does NOT instantiate and cache
            // a toolkit for the lifetime of the process.
            bool isHeadless = java.awt.GraphicsEnvironment.isHeadless();

            _logger.LogInformation(
                "AWT headless guard: property={Prop} isHeadless={IsHeadless} " +
                "awtToolkitProperty={ToolkitProp} thread={Thread}",
                prop, isHeadless,
                java.lang.System.getProperty("awt.toolkit"),
                Environment.CurrentManagedThreadId);

            if (!isHeadless)
            {
                _logger.LogError(
                    "AWT is NOT headless (isHeadless={IsHeadless}). The windowed toolkit's " +
                    "eventLoop thread is what faults in JavaVM.GetEnv.",
                    isHeadless);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AWT headless guard failed.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
