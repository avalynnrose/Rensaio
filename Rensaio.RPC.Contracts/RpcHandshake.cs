namespace Rensaio.RPC.Contracts;

/// <summary>
/// Defines the stdout handshake the RPC host prints so the backend can learn the
/// OS-assigned loopback port and readiness state without guessing.
///
/// Line format (single line, LF terminated, written to stdout, flushed):
///
///   RENS-RPC-READY|proto=1|port=54321|version=1.0.0
///
/// Rules:
///   * Only the first matching line is authoritative.
///   * The prefix is ASCII and locale independent.
///   * Any other stdout line is treated as a log line (see RpcLogLine).
///   * The backend must tolerate CRLF and arbitrary leading whitespace so a
///     future console redirection change cannot break readiness detection.
/// </summary>
public static class RpcHandshake
{
    /// <summary>Prefix marking the readiness/handshake line.</summary>
    public const string ReadyPrefix = "RENS-RPC-READY";

    /// <summary>Protocol version; backend and RPC must agree.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Prefix marking every structured log line.</summary>
    public const string LogPrefix = "RENS-RPC";

    /// <summary>Field separator inside handshake and log lines.</summary>
    public const char FieldSeparator = '|';

    /// <summary>
    /// Builds the readiness line from the resolved port and RPC version.
    /// </summary>
    public static string BuildReadyLine(int port, string version)
        => $"{ReadyPrefix}{FieldSeparator}proto={ProtocolVersion}{FieldSeparator}port={port}{FieldSeparator}version={version}";

    /// <summary>
    /// Attempts to parse a handshake line. Returns false for any line that is not
    /// the readiness line, including log lines.
    /// </summary>
    public static bool TryParseReadyLine(string? line, out int port, out int protocolVersion)
    {
        port = 0;
        protocolVersion = 0;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        string trimmed = line.Trim();
        if (!trimmed.StartsWith(ReadyPrefix, StringComparison.Ordinal))
            return false;

        string[] parts = trimmed.Split(FieldSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            string key = part[..eq].Trim();
            string value = part[(eq + 1)..].Trim();
            if (key.Equals("port", StringComparison.OrdinalIgnoreCase))
                int.TryParse(value, out port);
            else if (key.Equals("proto", StringComparison.OrdinalIgnoreCase))
                int.TryParse(value, out protocolVersion);
        }

        return port > 0;
    }
}
