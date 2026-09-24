using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mihon.ExtensionsBridge.Core.Extensions;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Configuration;
using Rensaio.RPC.Configuration;
using Rensaio.RPC.Logging;
using Rensaio.RPC.Runtime;

namespace Rensaio.RPC;

/// <summary>
/// Reusable host for the isolated Mihon/IKVM/CEF runtime.
///
/// Two hosts share this:
///  - <c>Rensaio.RPC</c> (headless): plain ASP.NET hosting, no UI, no CEF pump host.
///    This is the Docker/Linux behavior.
///  - <c>Rensaio.DesktopRPC</c> (desktop): an Avalonia app that hosts this same ASP.NET
///    app but injects an Avalonia-UI-thread CEF pump (<see cref="IStartCefTimer"/>) via the
///    <c>configure</c> hook. This reproduces the historically-working tray topology where
///    IKVM/CEF ran on the Avalonia UI thread.
///
/// The pump is deliberately overridable: the headless default is registered first and the
/// caller's <c>configure</c> runs last, so the last <c>IStartCefTimer</c> registration wins.
/// </summary>
public static class RpcHost
{
    /// <summary>
    /// One-time environment preparation shared by every host. Must run before any
    /// IKVM/AWT/JCEF class loads.
    /// </summary>
    public static void PrepareEnvironment()
    {
        // Diagnostic parity with the tray (RensaioTray/Program.cs sets this too).
        // IKVM_VERBOSE makes IKVM emit its classloader/JNI-attach diagnostics.
        Environment.SetEnvironmentVariable("IKVM_VERBOSE", "1");

        // Debug hook: when RENSAIO_RPC_DEBUGGER=1, block at startup until a debugger is
        // attached. Never set in normal runs.
        if (Environment.GetEnvironmentVariable("RENSAIO_RPC_DEBUGGER") == "1")
        {
            Console.WriteLine($"[RPC] waiting for debugger to attach (pid {Environment.ProcessId})...");
            while (!System.Diagnostics.Debugger.IsAttached)
                Thread.Sleep(200);
            Console.WriteLine("[RPC] debugger attached; continuing.");
            System.Diagnostics.Debugger.Break();
        }

        // Force AWT headless mode BEFORE any IKVM/JCEF code runs.
        java.lang.System.setProperty("java.awt.headless", "true");

        // Verify the headless property actually took effect before AWT/JCEF initialize.
        try
        {
            string? prop = java.lang.System.getProperty("java.awt.headless");
            bool isHeadless = java.awt.GraphicsEnvironment.isHeadless();
            string toolkit = java.awt.Toolkit.getDefaultToolkit().getClass().getName();
            Console.WriteLine($"[RPC] headless probe: property={prop} isHeadless={isHeadless} toolkit={toolkit}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RPC] headless probe failed: {ex.GetType().Name}: {ex.Message}");
        }

        // TLS trust diagnostic (backs TrustManagerBridge).
        try
        {
            string javaHome = java.lang.System.getProperty("java.home") ?? "(null)";
            var tmf = javax.net.ssl.TrustManagerFactory.getInstance(
                javax.net.ssl.TrustManagerFactory.getDefaultAlgorithm());
            tmf.init((java.security.KeyStore?)null);
            int accepted = 0;
            var managers = tmf.getTrustManagers()!;
            foreach (var tm in managers)
            {
                if (tm is javax.net.ssl.X509TrustManager x509)
                    accepted += x509.getAcceptedIssuers()?.Length ?? 0;
            }
            Console.WriteLine($"[RPC] trust probe: java.home={javaHome} acceptedIssuers={accepted}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RPC] trust probe failed: {ex.GetType().Name}: {ex.Message}");
        }

        InstallFirstChanceLogger();
    }

    /// <summary>
    /// Builds the RPC <see cref="WebApplication"/>. The optional <paramref name="configure"/>
    /// hook runs last so a host can override registrations (notably <see cref="IStartCefTimer"/>).
    /// </summary>
    public static WebApplication Build(string[] args, Action<IServiceCollection>? configure = null)
    {
        RpcAppSettings settings = RpcAppSettings.Parse(args);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Validate the DI graph while the host is starting, not on first use.
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });

        // Route every log event to stdout in the RENS-RPC format.
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new StdoutLoggerProvider(Console.Out));

        // Fold command-line path switches into the bridge Paths options.
        builder.Services.Configure<Paths>(p =>
        {
            if (!string.IsNullOrWhiteSpace(settings.BridgeFolder))
                p.BridgeFolder = settings.BridgeFolder;
            if (!string.IsNullOrWhiteSpace(settings.TempFolder))
                p.TempFolder = settings.TempFolder;
        });

        builder.Services.Configure<RpcOptions>(o =>
        {
            if (settings.CefEnabled.HasValue)
                o.Cef.Enabled = settings.CefEnabled.Value;
        });

        // Bridge registration — the RPC is the only place that touches IKVM/CEF.
        builder.Services.AddExtensionsBridge();

        // Default (headless) CEF pump. A desktop host overrides this via `configure`.
        builder.Services.AddSingleton<IStartCefTimer>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RpcOptions>>().Value;
            if (!options.Cef.Enabled)
                return new DisabledCefTimer(sp.GetRequiredService<ILogger<DisabledCefTimer>>());
            return ActivatorUtilities.CreateInstance<RpcCefTimer>(sp);
        });

        // Per-start token + LRU policy.
        builder.Services.AddSingleton(new RpcStartupToken(settings.Token ?? string.Empty));

        // Parent liveness watchdog.
        builder.Services.AddSingleton(new ParentProcessId(settings.ParentProcessId));
        builder.Services.AddSingleton(new ParentWatchOptions(settings.WatchParent));
        builder.Services.AddHostedService<ParentProcessWatcher>();
        builder.Services.AddSingleton<SourceCallGate>(_ => new SourceCallGate(8));
        builder.Services.AddSingleton<KeyedAsyncLock>();
        builder.Services.AddSingleton<SourceCallRunner>();
        builder.Services.AddSingleton<Services.SourceResolver>();
        builder.Services.AddSingleton<ExtensionLruService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ExtensionLruService>());
        builder.Services.AddSingleton<RpcReadiness>();

        // gRPC, loopback only (explicit addresses — ListenLocalhost(0) is illegal in Kestrel).
        builder.Services.AddGrpc();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(System.Net.IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
            });
            kestrel.Listen(System.Net.IPAddress.IPv6Loopback, 0, listen =>
            {
                listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
            });
        });

        // Host-specific overrides last (e.g. an Avalonia UI-thread IStartCefTimer).
        configure?.Invoke(builder.Services);

        return builder.Build();
    }

    /// <summary>Maps services, wires the readiness handshake, and runs the host.</summary>
    public static Task RunAsync(WebApplication app) => RunAsync(app, CancellationToken.None);

    /// <summary>Maps services, wires the readiness handshake, and runs the host.</summary>
    public static async Task RunAsync(WebApplication app, CancellationToken cancellationToken)
    {
        app.MapGrpcService<Services.ControlGrpcService>();
        app.MapGrpcService<Services.ExtensionsGrpcService>();
        app.MapGrpcService<Services.CatalogGrpcService>();
        app.MapGrpcService<Services.ContentGrpcService>();

        var readiness = app.Services.GetRequiredService<RpcReadiness>();

        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Register(() =>
        {
            int port = ResolveBoundPort(app);
            RpcHandshakeWriter.WriteReady(port, Program.RpcVersion);

            var bridge = app.Services.GetRequiredService<IBridgeManager>();
            _ = Task.Run(async () =>
            {
                try
                {
                    await bridge.InitializationCompleted.ConfigureAwait(false);
                    readiness.MarkReady();
                }
                catch (Exception ex)
                {
                    readiness.MarkFailed(ex);
                }
            });
        });

        await app.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    public static int ResolveBoundPort(WebApplication app)
    {
        var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var addresses = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        foreach (string? address in addresses?.Addresses ?? [])
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port > 0)
                return uri.Port;
        }
        return 0;
    }

    /// <summary>
    /// Diagnostic: writes every IKVM/CEF/Java-origin first-chance exception to
    /// <c>rpc-crash-YYYYMMDD.log</c> so a reverse-JNI failure has recoverable context.
    /// </summary>
    private static void InstallFirstChanceLogger()
    {
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            var ex = e.Exception;
            if (ex == null)
                return;

            string source = ex.Source ?? string.Empty;
            string typeName = ex.GetType().FullName ?? string.Empty;
            bool interesting =
                source.Contains("IKVM", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("Android", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("cef", StringComparison.OrdinalIgnoreCase) ||
                typeName.Contains("jni", StringComparison.OrdinalIgnoreCase) ||
                typeName.StartsWith("java.", StringComparison.Ordinal) ||
                typeName.StartsWith("javax.", StringComparison.Ordinal);
            if (!interesting)
                return;

            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, $"rpc-crash-{DateTime.UtcNow:yyyyMMdd}.log");
                string entry =
                    $"[{DateTime.UtcNow:O}] thread={Environment.CurrentManagedThreadId} " +
                    $"type={typeName} source={source}\n{ex}\n---\n";
                File.AppendAllText(path, entry);
            }
            catch
            {
                // Logging must never throw.
            }
        };
    }
}

/// <summary>No-op pump used when CEF is disabled by configuration.</summary>
public sealed class DisabledCefTimer : IStartCefTimer
{
    private readonly ILogger _logger;
    public DisabledCefTimer(ILogger logger) => _logger = logger;
    public void StartCefPumpTimer() => _logger.LogInformation("CEF pump not started (disabled).");
}

/// <summary>Writes the single readiness handshake line the backend parses.</summary>
public static class RpcHandshakeWriter
{
    private static readonly object Sync = new();

    public static void WriteReady(int port, string version)
    {
        lock (Sync)
        {
            Console.Out.WriteLine(Contracts.RpcHandshake.BuildReadyLine(port, version));
            Console.Out.Flush();
        }
    }
}

/// <summary>Tracks bridge initialization so Ping/GetStatus can report readiness.</summary>
public sealed class RpcReadiness
{
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private volatile bool _ready;
    private Exception? _failure;

    public bool IsReady => _ready;
    public Exception? Failure => _failure;
    public TimeSpan Uptime => DateTime.UtcNow - _startedUtc;

    public void MarkReady() => _ready = true;
    public void MarkFailed(Exception ex) => _failure = ex;
}
