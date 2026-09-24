namespace Rensaio.RPC.Configuration;

/// <summary>
/// Strongly-typed RPC host options bound from the "Rpc" configuration section.
/// The backend passes these through environment variables so both tray and
/// Docker deployments can override them without editing files.
/// </summary>
public sealed class RpcOptions
{
    public const string SectionName = "Rpc";

    public CefOptions Cef { get; set; } = new();
    public LruOptions Lru { get; set; } = new();
}

/// <summary>
/// Controls the CEF/JCEF embedded browser inside the RPC process.
///
/// The CEF message pump now runs on a dedicated RPC thread instead of the
/// Avalonia UI thread (there is no Avalonia in RPC). When JCEF never
/// initializes (headless/Docker), the pump is a harmless no-op.
/// </summary>
public sealed class CefOptions
{
    /// <summary>
    /// Master switch. When false the pump is not started and JCEF stays unused,
    /// matching the Docker behavior where WebView-requiring sources fall back to
    /// the direct network chain / FlareSolverr.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Extension LRU cache policy.
/// </summary>
public sealed class LruOptions
{
    /// <summary>Maximum number of extension interops kept loaded at once.</summary>
    public int MaxLoadedExtensions { get; set; } = 16;

    /// <summary>Idle minutes after which a loaded extension may be unloaded.</summary>
    public int IdleUnloadMinutes { get; set; } = 30;

    /// <summary>How often the idle sweep runs.</summary>
    public int SweepIntervalSeconds { get; set; } = 300;
}
