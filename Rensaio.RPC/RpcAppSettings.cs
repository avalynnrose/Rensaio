namespace Rensaio.RPC;

/// <summary>
/// Command-line contract between RensaioBackend (which spawns this process) and
/// the RPC host. Passed as switches so both tray and Docker deployments inherit
/// the same behavior without shared config files.
///
/// Example:
///   Rensaio.RPC --handshake=stdio --token=abc123
///               --paths:bridge=/config/extensions --paths:temp=/tmp/extensionbridge
/// </summary>
public sealed class RpcAppSettings
{
    /// <summary>Per-start token the backend sends on every call. Optional.</summary>
    public string? Token { get; set; }

    /// <summary>Extension working folder (Paths:BridgeFolder).</summary>
    public string? BridgeFolder { get; set; }

    /// <summary>Temp folder (Paths:TempFolder).</summary>
    public string? TempFolder { get; set; }

    /// <summary>Whether the CEF pump is enabled. Overrides appsettings when set.</summary>
    public bool? CefEnabled { get; set; }

    /// <summary>
    /// True when the supervisor redirected our stdin, so the stdin-EOF parent watch is
    /// safe to arm. Passed only in that case, so an interactive launch (where stdin is
    /// the terminal) never mistakes a console read for the parent's death.
    /// </summary>
    public bool WatchParent { get; set; }

    /// <summary>
    /// OS process id of the spawning backend. When set, this process monitors it and
    /// exits as soon as the parent dies, so a killed/force-stopped parent (IDE Stop,
    /// crash, task kill) can never orphan this process.
    /// </summary>
    public int? ParentProcessId { get; set; }

    /// <summary>
    /// Path the host writes its readiness handshake to. When set, the supervisor learns the
    /// bound port from this file instead of a redirected stdout pipe (no stdio redirection).
    /// </summary>
    public string? HandshakeFile { get; set; }

    /// <summary>Parses "key=value" switches; unknown keys are ignored.</summary>
    public static RpcAppSettings Parse(string[] args)
    {
        var settings = new RpcAppSettings();
        foreach (string raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            string arg = raw.TrimStart('-');
            int eq = arg.IndexOf('=');
            string key = eq >= 0 ? arg[..eq] : arg;
            string value = eq >= 0 ? arg[(eq + 1)..] : string.Empty;

            switch (key.ToLowerInvariant())
            {
                case "token":
                    settings.Token = value;
                    break;
                case "paths:bridge":
                case "bridgefolder":
                    settings.BridgeFolder = value;
                    break;
                case "paths:temp":
                case "tempfolder":
                    settings.TempFolder = value;
                    break;
                case "parentpid":
                    if (int.TryParse(value, out int ppid) && ppid > 0)
                        settings.ParentProcessId = ppid;
                    break;
                case "handshake-file":
                    settings.HandshakeFile = value;
                    break;
                case "rpc:cef:enabled":
                case "cefenabled":
                    if (bool.TryParse(value, out bool cef))
                        settings.CefEnabled = cef;
                    break;
                case "watch-parent":
                    // Bare switch (no value) or --watch-parent=true.
                    settings.WatchParent = string.IsNullOrEmpty(value)
                        || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        return settings;
    }
}
