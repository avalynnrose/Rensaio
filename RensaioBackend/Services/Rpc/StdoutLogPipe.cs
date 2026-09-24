using System.Globalization;
using Microsoft.Extensions.Logging;
using Rensaio.RPC.Contracts;
using RensaioBackend.Utils;
using Serilog.Events;
using Serilog.Parsing;

namespace RensaioBackend.Services.Rpc;

/// <summary>
/// Reads Rensaio.RPC stdout/stderr line by line and re-emits each RENS-RPC log
/// line through the backend's Serilog pipeline, reproducing the event the
/// pre-split in-process logger would have produced.
///
/// Reproducing it faithfully matters for more than cosmetics: Serilog's console
/// theme colours each value by its RUNTIME TYPE (numbers/bools green, strings
/// orange). If only a pre-formatted string is forwarded, every value becomes a
/// string and the whole line renders in one colour. So the RPC also sends the
/// ORIGINAL message template and each property's typed value, and this pipe
/// rebuilds a genuine <see cref="LogEvent"/> from them.
/// </summary>
public sealed class StdoutLogPipe
{
    private readonly ILogger<StdoutLogPipe> _logger;
    private readonly string _channel;
    private readonly MessageTemplateParser _parser = new();

    public StdoutLogPipe(ILogger<StdoutLogPipe> logger, string channel)
    {
        _logger = logger;
        _channel = channel;
    }

    /// <summary>
    /// Pumps a reader until EOF. Intended to run on its own task per stream.
    /// </summary>
    public async Task PumpAsync(TextReader reader, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // Stream closed because the child exited; nothing left to pump.
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (line == null)
                break; // EOF

            Emit(line);
        }
    }

    /// <summary>Parses and re-emits one line. Exposed for unit testing.</summary>
    public void Emit(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        // The readiness handshake is consumed by the supervisor, not logged.
        if (RpcHandshake.TryParseReadyLine(line, out _, out _))
            return;

        RpcLogLine? parsed = RpcLogLine.TryParse(line);
        if (parsed == null)
        {
            // Raw framework/native output (e.g. JVM or CEF chatter) — keep it,
            // tagged so it is still attributable to the RPC process.
            _logger.LogInformation("{Channel}: {Line}", _channel, line);
            return;
        }

        var props = new Dictionary<string, LogEventPropertyValue>(StringComparer.Ordinal);

        // Typed values, so the theme renders (and colours) them as in-process.
        foreach (RpcLogProperty p in parsed.Properties)
        {
            if (string.IsNullOrEmpty(p.Name) || p.Name == "{OriginalFormat}")
                continue;
            props[p.Name] = ToSerilogValue(p);
        }

        // The App tag drives RewriteMessageSink routing and colouring.
        props["App"] = new ScalarValue(parsed.App);

        string? className = ClassNameFromCategory(parsed.Category);
        string templateText;

        if (string.Equals(parsed.App, EnvironmentSetup.AppAndroid, StringComparison.OrdinalIgnoreCase))
        {
            // Android logs are plain text that already carries a "[Class]" prefix.
            // RewriteMessageSink lifts that prefix into the class column and styles the
            // remainder, so keep this shape (it is what makes Android output render
            // correctly rather than falling through unstyled).
            string androidMessage = HasClassPrefix(parsed.Message)
                ? parsed.Message
                : $"[{className ?? EnvironmentSetup.AppAndroid}] {parsed.Message}";

            props["AndroidCompatMessage"] = new ScalarValue(androidMessage);
            props["Message"] = new ScalarValue(parsed.Message);
            templateText = "{Message:l}";
        }
        else
        {
            // The class column ({PascalClassName}) is derived from SourceContext.
            props["SourceContext"] = new ScalarValue(parsed.Category);

            // Use the REAL template so structured values render typed (and therefore
            // coloured). Fall back to the formatted text when no template survived, or
            // when extra content was appended after it (e.g. an exception, which the RPC
            // appends as a newline-terminated block) — those would otherwise be lost.
            bool hasExtraContent = parsed.Message.Contains('\n');
            if (!string.IsNullOrEmpty(parsed.Template) && !hasExtraContent)
            {
                templateText = parsed.Template;
            }
            else
            {
                props["Message"] = new ScalarValue(parsed.Message);
                templateText = "{Message:l}";
            }
        }

        var logEvent = new LogEvent(
            DateTimeOffset.Now,
            MapLevel(parsed.Level),
            null,
            _parser.Parse(templateText),
            props.Select(kv => new LogEventProperty(kv.Key, kv.Value)));

        Serilog.Log.Logger.Write(logEvent);
    }

    /// <summary>
    /// Rebuilds a typed Serilog value from the wire representation. Numbers stay
    /// numbers and booleans stay booleans precisely so the console theme applies the
    /// same colour it would have in-process.
    /// </summary>
    private static LogEventPropertyValue ToSerilogValue(RpcLogProperty property) => property.Kind switch
    {
        RpcLogValueKind.Number when long.TryParse(
            property.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)
            => new ScalarValue(l),

        RpcLogValueKind.Number when double.TryParse(
            property.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            => new ScalarValue(d),

        RpcLogValueKind.Bool
            => new ScalarValue(string.Equals(property.Value, "true", StringComparison.OrdinalIgnoreCase)),

        RpcLogValueKind.Null
            => new ScalarValue(null),

        _ => new ScalarValue(property.Value),
    };

    /// <summary>
    /// The class name Serilog would have derived from this logger category: its final
    /// dotted segment (e.g. "Mihon.ExtensionsBridge.Core.Services.RepositoryManager"
    /// to "RepositoryManager"), matching RewriteMessageSink's own derivation.
    /// </summary>
    private static string? ClassNameFromCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return null;

        int lastDot = category.LastIndexOf('.');
        string name = lastDot >= 0 ? category[(lastDot + 1)..] : category;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>True when the message already carries an Android "[Class]" prefix.</summary>
    private static bool HasClassPrefix(string message)
        => !string.IsNullOrEmpty(message) && message.StartsWith('[') && message.Contains(']');

    private static LogEventLevel MapLevel(string level) => level switch
    {
        "Verbose" => LogEventLevel.Verbose,
        "Debug" => LogEventLevel.Debug,
        "Info" => LogEventLevel.Information,
        "Warn" => LogEventLevel.Warning,
        "Error" => LogEventLevel.Error,
        "Fatal" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };
}
