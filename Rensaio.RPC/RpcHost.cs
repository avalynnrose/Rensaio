using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mihon.ExtensionsBridge.Browser;
using Mihon.ExtensionsBridge.Core.Extensions;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Configuration;
using Rensaio.RPC.Configuration;
using Rensaio.RPC.Contracts;
using Rensaio.RPC.Logging;
using Rensaio.RPC.Runtime;

namespace Rensaio.RPC;

/// <summary>
/// Reusable host for the isolated Mihon/IKVM/CEF runtime.
///
/// The single host <c>Rensaio.RPC</c> (plain ASP.NET, no UI) serves every deployment:
/// Docker and the desktop tray. The Avalonia-hosted <c>Rensaio.DesktopRPC</c> variant was
/// removed with JCEF.
///
/// NOTE: the Avalonia-UI-thread CEF message pump (IStartCefTimer) was removed with JCEF; the
/// .NET-hosted browser drives itself and needs no external pump.
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

      
    }

    /// <summary>
    /// Builds the RPC <see cref="WebApplication"/>. The optional <paramref name="configure"/>
    /// hook runs last so a host can override registrations (notably <see cref="IStartCefTimer"/>).
    /// </summary>
    /// <summary>Readiness handshake target file (from --handshake-file), or null for stdout.</summary>
    internal static string? HandshakeFilePath;

    /// <summary>
    /// EXPERIMENT: the loopback port the host bound to, published for in-process self-tests
    /// (the standalone gRPC soak drives the real Kestrel HTTP/2 endpoint from inside the
    /// same process, so it needs the negotiated port without a supervisor handshake).
    /// 0 until the application has started.
    /// </summary>
    public static int BoundPort;

    public static WebApplication Build(string[] args, Action<IServiceCollection>? configure = null)
    {
        RpcAppSettings settings = RpcAppSettings.Parse(args);
        HandshakeFilePath = settings.HandshakeFile;

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
//        builder.Logging.ClearProviders();
//        builder.Logging.AddProvider(new StdoutLoggerProvider(Console.Out));

        // ── DIAGNOSTIC OPT-IN (RENSAIO_RPC_VERBOSE=1) ───────────────────────────────────────
        // LOWERS the minimum level to Debug for the whole RPC process tree. This is what makes the
        // webview/browser diagnostics (all of which are now Debug) actually visible, since the
        // JVM-side floor also has to be lowered (see AndroidCompatLogManager).
        //
        // OFF by default: the per-call browser telemetry is heavy, and normal operation should be
        // quiet. Turn it on only when diagnosing a webview-specific problem.
        bool verbose = string.Equals(
            Environment.GetEnvironmentVariable("RENSAIO_RPC_VERBOSE"),
            "1",
            StringComparison.OrdinalIgnoreCase);
        if (verbose)
        {
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            // Also lift the per-provider filters, otherwise the default configuration filters
            // debug out again for the "Microsoft" / "System" categories.
            builder.Logging.AddFilter((_, _, level) => level >= LogLevel.Debug);
        }

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

        // Pluggable browser automation providers (Patchright primary, Playwright fallback).
        // Registered here so Core never takes an assembly reference on a browser engine.
        builder.Services.AddBrowserProviders();

        // Per-start token + LRU policy.
        builder.Services.AddSingleton(new RpcStartupToken(settings.Token ?? string.Empty));

        // Parent liveness watchdog.
        builder.Services.AddSingleton(new ParentProcessId(settings.ParentProcessId));
        builder.Services.AddSingleton(new ParentWatchOptions(settings.WatchParent));
        builder.Services.AddHostedService<ParentProcessWatcher>();
        // EXPERIMENT (RENSAIO_RPC_GATE): make the global extension-call concurrency budget
        // tunable so we can test whether bounding concurrent extension/WebView execution
        // removes the WToolkit.eventLoop -> JavaVM.GetEnv FailFast. Default 8 (unchanged).
        // Set RENSAIO_RPC_GATE=1 to fully serialize extension calls.
        int gateCapacity = int.TryParse(Environment.GetEnvironmentVariable("RENSAIO_RPC_GATE"), out int gc) && gc > 0
            ? gc
            : 8;
        builder.Services.AddSingleton<SourceCallGate>(_ => new SourceCallGate(gateCapacity));
        builder.Services.AddSingleton<KeyedAsyncLock>();
        builder.Services.AddSingleton<SourceCallRunner>();
        builder.Services.AddSingleton<Services.SourceResolver>();
        builder.Services.AddSingleton<ExtensionLruService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ExtensionLruService>());
        builder.Services.AddSingleton<RpcReadiness>();

        // gRPC log stream: every structured log line is ALSO published to the hub and
        // streamed to the backend (the child is launched detached, so stdout is not captured).
        var logHub = new RpcLogHub();
        builder.Services.AddSingleton(logHub);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new RpcLogStreamProvider(logHub));

        // ── EXPERIMENT (dimension B): allow disabling gRPC + Kestrel HTTP2 entirely. ──
        // Set RENSAIO_RPC_NO_GRPC=1 to skip AddGrpc and the HTTP/2 listeners, so the
        // DesktopRPC standalone soak runs IKVM/CEF with NO gRPC/Kestrel surface at all.
        // If the WToolkit.eventLoop -> JavaVM.GetEnv FailFast disappears, dimension B
        // gRPC, loopback only (explicit addresses — ListenLocalhost(0) is illegal in Kestrel).
        // EXPERIMENT: when RENSAIO_RPC_DIAG=1, install the call-tracing interceptor globally.
        bool diagGrpc = Environment.GetEnvironmentVariable("RENSAIO_RPC_DIAG") == "1";
        builder.Services.AddGrpc(options =>
        {
            // Match the backend client's raised ceilings (RpcGrpcSizes): catalog/chapter
            // JSON payloads routinely exceed gRPC's 4 MiB default, which otherwise surfaces
            // as ResourceExhausted on either side of the call.
            options.MaxReceiveMessageSize = RpcGrpcSizes.MaxMessageSize;
            options.MaxSendMessageSize = RpcGrpcSizes.MaxMessageSize;
            if (diagGrpc)
                options.Interceptors.Add<Runtime.RpcCallInterceptor>();
        });
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
            BoundPort = port; // EXPERIMENT: expose for in-process gRPC soak
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

}


/// <summary>Writes the single readiness handshake line the backend parses.</summary>
public static class RpcHandshakeWriter
{
    private static readonly object Sync = new();

    public static void WriteReady(int port, string version)
    {
        string line = Contracts.RpcHandshake.BuildReadyLine(port, version);

        // Preferred: a handshake FILE the supervisor polls (no stdio redirection needed).
        string? file = RpcHost.HandshakeFilePath;
        if (!string.IsNullOrWhiteSpace(file))
        {
            try { File.WriteAllText(file, line); } catch { /* best effort */ }
            return;
        }

        lock (Sync)
        {
            Console.Out.WriteLine(line);
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
