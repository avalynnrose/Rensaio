using System.Text;

namespace Rensaio.RPC.Contracts;

/// <summary>Value kind carried for a log property so the backend can rebuild a typed value.</summary>
public static class RpcLogValueKind
{
    public const string String = "string";
    public const string Number = "number";
    public const string Bool = "bool";
    public const string Null = "null";
}

/// <summary>One structured log property (name + typed value text).</summary>
public sealed class RpcLogProperty
{
    public required string Name { get; init; }

    /// <summary>One of <see cref="RpcLogValueKind"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>Invariant-culture text of the value (empty for null).</summary>
    public string Value { get; init; } = string.Empty;
}

/// <summary>
/// One structured log event emitted by Rensaio.RPC to stdout and parsed by
/// RensaioBackend, which re-emits it through Serilog so the console/log-file
/// form stays identical to the pre-split layout.
///
/// Format (single line, LF terminated). Eight fields; everything after APP is
/// escaped so the separator can never appear inside a field:
///
///   RENS-RPC|LEVEL|CATEGORY|CORRELATION|APP|MESSAGE|TEMPLATE|PROPERTIES
///
///   LEVEL        - Verbose|Debug|Info|Warn|Error|Fatal
///   CATEGORY     - .NET logger category ("Android", "Mihon.ExtensionsBridge.*", ...)
///   CORRELATION  - optional correlation id, empty when absent
///   APP          - "MihonEx" or "Android" (mirrors EnvironmentSetup.AppMihon/AppAndroid)
///   MESSAGE      - the formatted message (fallback when TEMPLATE is empty)
///   TEMPLATE     - the ORIGINAL message template, so the backend can rebuild a
///                  real Serilog event and preserve structured logging
///   PROPERTIES   - "name=kind:value" entries joined by ';'
///
/// WHY the template and typed properties travel over the wire: Serilog's console
/// theme colours a destination by the property's runtime TYPE (numbers/bools green,
/// strings orange, ...). If the RPC pre-formats everything to a single string, the
/// backend can only ever see strings and the whole line renders in one colour. So
/// the original template plus the typed values are forwarded, and the backend re-emits
/// the event unchanged.
/// </summary>
public sealed class RpcLogLine
{
    public string Level { get; init; } = "Info";
    public string Category { get; init; } = string.Empty;
    public string Correlation { get; init; } = string.Empty;
    public string App { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    /// <summary>Original message template; empty when unavailable.</summary>
    public string Template { get; init; } = string.Empty;

    /// <summary>Structured properties carrying their original value type.</summary>
    public IReadOnlyList<RpcLogProperty> Properties { get; init; } = [];

    // ── Field escaping ──
    // A single backslash escape keeps every field separator-free while staying
    // human-readable in raw stdout.

    public static string EscapeField(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '|': sb.Append("\\p"); break;
                case ';': sb.Append("\\s"); break;
                case ':': sb.Append("\\c"); break;
                case '=': sb.Append("\\e"); break;
                case '\r': break; // normalise line endings away
                case '\n': sb.Append("\\n"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    public static string UnescapeField(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                sb.Append(c);
                continue;
            }

            char next = value[++i];
            switch (next)
            {
                case '\\': sb.Append('\\'); break;
                case 'p': sb.Append('|'); break;
                case 's': sb.Append(';'); break;
                case 'c': sb.Append(':'); break;
                case 'e': sb.Append('='); break;
                case 'n': sb.Append('\n'); break;
                default: sb.Append(next); break;
            }
        }
        return sb.ToString();
    }

    private static string EncodeProperties(IReadOnlyList<RpcLogProperty> properties)
    {
        if (properties.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var p in properties)
        {
            if (sb.Length > 0)
                sb.Append(';');
            sb.Append(EscapeField(p.Name)).Append('=')
              .Append(EscapeField(p.Kind)).Append(':')
              .Append(EscapeField(p.Value));
        }
        return sb.ToString();
    }

    private static List<RpcLogProperty> DecodeProperties(string encoded)
    {
        var result = new List<RpcLogProperty>();
        if (string.IsNullOrEmpty(encoded))
            return result;

        foreach (string entry in SplitUnescaped(encoded, ';'))
        {
            if (entry.Length == 0)
                continue;

            int eq = IndexOfUnescaped(entry, '=');
            if (eq <= 0)
                continue;

            string name = UnescapeField(entry[..eq]);
            string rest = entry[(eq + 1)..];
            int colon = IndexOfUnescaped(rest, ':');
            string kind = colon >= 0 ? UnescapeField(rest[..colon]) : RpcLogValueKind.String;
            string value = colon >= 0 ? UnescapeField(rest[(colon + 1)..]) : string.Empty;

            result.Add(new RpcLogProperty { Name = name, Kind = kind, Value = value });
        }
        return result;
    }

    // Splits on an unescaped delimiter (a delimiter preceded by an odd number of
    // backslashes is escaped).
    private static IEnumerable<string> SplitUnescaped(string value, char delimiter)
    {
        int start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == delimiter && !IsEscaped(value, i))
            {
                yield return value[start..i];
                start = i + 1;
            }
        }
        yield return value[start..];
    }

    private static int IndexOfUnescaped(string value, char target)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == target && !IsEscaped(value, i))
                return i;
        }
        return -1;
    }

    private static bool IsEscaped(string value, int index)
    {
        int backslashes = 0;
        for (int i = index - 1; i >= 0 && value[i] == '\\'; i--)
            backslashes++;
        return backslashes % 2 == 1;
    }

    /// <summary>Renders the line (without trailing newline).</summary>
    public string ToLine()
    {
        var sb = new StringBuilder();
        sb.Append(RpcHandshake.LogPrefix).Append(RpcHandshake.FieldSeparator);
        sb.Append(EscapeField(Level)).Append(RpcHandshake.FieldSeparator);
        sb.Append(EscapeField(Category)).Append(RpcHandshake.FieldSeparator);
        sb.Append(EscapeField(Correlation)).Append(RpcHandshake.FieldSeparator);
        sb.Append(EscapeField(App)).Append(RpcHandshake.FieldSeparator);
        sb.Append(EscapeField(Message)).Append(RpcHandshake.FieldSeparator);
        sb.Append(EscapeField(Template)).Append(RpcHandshake.FieldSeparator);
        sb.Append(EncodeProperties(Properties));
        return sb.ToString();
    }

    /// <summary>
    /// Parses a stdout line. Returns null for non-log lines (handshake, raw
    /// framework output that predates the logger, etc.).
    /// </summary>
    public static RpcLogLine? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        string trimmed = line.Trim();

        // Never treat the readiness handshake as a log line.
        if (trimmed.StartsWith(RpcHandshake.ReadyPrefix, StringComparison.Ordinal))
            return null;

        if (!trimmed.StartsWith(RpcHandshake.LogPrefix + RpcHandshake.FieldSeparator, StringComparison.Ordinal))
            return null;

        // Split into at most 6 parts, then split the remainder (message + template +
        // properties) out of the message field. The message field itself never
        // contains a raw '|' because it is escaped, so a simple split is safe.
        string[] parts = trimmed.Split(RpcHandshake.FieldSeparator, 8);
        if (parts.Length < 6)
            return null;

        string message = UnescapeField(parts[5]);
        string template = parts.Length > 6 ? UnescapeField(parts[6]) : string.Empty;
        var properties = parts.Length > 7 ? DecodeProperties(parts[7]) : [];

        return new RpcLogLine
        {
            Level = parts[1],
            Category = parts[2],
            Correlation = parts[3],
            App = parts[4],
            Message = message,
            Template = template,
            Properties = properties,
        };
    }
}
