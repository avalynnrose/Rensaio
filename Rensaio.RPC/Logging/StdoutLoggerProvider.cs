using System.Collections;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Rensaio.RPC.Contracts;

namespace Rensaio.RPC.Logging;

/// <summary>
/// Routes every .NET log event to stdout in the RENS-RPC delimited format so
/// RensaioBackend can parse and re-emit it through Serilog. This is the single
/// sink that keeps the post-split logs identical in form to the pre-split ones.
///
/// Crucially it forwards the ORIGINAL message template and the typed property
/// values, not just the formatted string. Serilog's console theme colours a value
/// by its runtime type (numbers/bools green, strings orange), so pre-formatting
/// everything to text would make the backend render the whole line in one colour.
/// </summary>
public sealed class StdoutLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _sync = new();
    private readonly LogLevel _minimum;

    public StdoutLoggerProvider(TextWriter writer, LogLevel minimum = LogLevel.Information)
    {
        // Keep the underlying stdout stream open for the whole process lifetime.
        _writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        _minimum = minimum;
        _ = writer; // callers may pass Console.Out; we always use the real stdout
    }

    public ILogger CreateLogger(string categoryName) => new StdoutLogger(categoryName, this);

    private void Write(string category, LogLevel level, string message, string template, IReadOnlyList<RpcLogProperty> properties)
    {
        if (level < _minimum)
            return;

        var line = new RpcLogLine
        {
            Level = MapLevel(level),
            Category = category,
            Correlation = string.Empty,
            App = AppTagger.ForCategory(category),
            Message = message,
            Template = template,
            Properties = properties,
        }.ToLine();

        lock (_sync)
        {
            _writer.WriteLine(line);
        }
    }

    private static string MapLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "Verbose",
        LogLevel.Debug => "Debug",
        LogLevel.Information => "Info",
        LogLevel.Warning => "Warn",
        LogLevel.Error => "Error",
        LogLevel.Critical => "Fatal",
        _ => "Info",
    };

    public void Dispose()
    {
        lock (_sync)
        {
            _writer.Flush();
        }
    }

    private sealed class StdoutLogger : ILogger
    {
        private readonly string _category;
        private readonly StdoutLoggerProvider _provider;

        public StdoutLogger(string category, StdoutLoggerProvider provider)
        {
            _category = category;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider._minimum;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            // The formatted text stays the source of truth for the message (it already
            // includes any state the template alone would not render, e.g. scopes).
            string message = formatter(state, exception);
            if (exception != null)
                message = message + Environment.NewLine + exception;

            // Prefer the REAL template when the state exposes one, so structured
            // logging survives the process boundary.
            string template = string.Empty;
            var properties = new List<RpcLogProperty>();

            if (state is IReadOnlyList<KeyValuePair<string, object?>> kvs)
            {
                // KVP[0] is "{OriginalFormat}" by convention.
                var templateEntry = kvs.FirstOrDefault(kv => kv.Key == "{OriginalFormat}");
                if (templateEntry.Value is string fmt && !string.IsNullOrEmpty(fmt))
                    template = fmt;

                foreach (var kv in kvs)
                {
                    if (kv.Key == "{OriginalFormat}")
                        continue;
                    properties.Add(ToProperty(kv.Key, kv.Value));
                }
            }

            _provider.Write(_category, logLevel, message, template, properties);
        }

        /// <summary>
        /// Converts a logging state value to a typed wire property. Scalars keep their
        /// type so Serilog renders (and colours) them as it would in-process; anything
        /// richer is sent as its string form.
        /// </summary>
        private static RpcLogProperty ToProperty(string name, object? value)
        {
            switch (value)
            {
                case null:
                    return new RpcLogProperty { Name = name, Kind = RpcLogValueKind.Null };

                case bool b:
                    return new RpcLogProperty
                    {
                        Name = name,
                        Kind = RpcLogValueKind.Bool,
                        Value = b ? "true" : "false",
                    };

                case byte or sbyte or short or ushort or int or uint or long or ulong
                     or float or double or decimal:
                    return new RpcLogProperty
                    {
                        Name = name,
                        Kind = RpcLogValueKind.Number,
                        Value = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                    };

                case string s:
                    return new RpcLogProperty
                    {
                        Name = name,
                        Kind = RpcLogValueKind.String,
                        Value = s,
                    };

                case DateTime or DateTimeOffset or TimeSpan or Guid:
                    // Rendered with the invariant culture; colouring for these is
                    // string-like in the console theme, so String is correct.
                    return new RpcLogProperty
                    {
                        Name = name,
                        Kind = RpcLogValueKind.String,
                        Value = value is IFormattable f
                            ? f.ToString(null, CultureInfo.InvariantCulture)
                            : value.ToString() ?? string.Empty,
                    };

                case IEnumerable:
                    return new RpcLogProperty
                    {
                        Name = name,
                        Kind = RpcLogValueKind.String,
                        Value = DescribeEnumerable((IEnumerable)value),
                    };

                default:
                    return new RpcLogProperty
                    {
                        Name = name,
                        Kind = RpcLogValueKind.String,
                        Value = value.ToString() ?? string.Empty,
                    };
            }
        }

        private static string DescribeEnumerable(IEnumerable values)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (object? item in values)
            {
                if (!first)
                    sb.Append(", ");
                first = false;
                sb.Append(item?.ToString() ?? "null");
                if (sb.Length > 512)
                {
                    sb.Append(", ...");
                    break;
                }
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}

/// <summary>
/// Maps a .NET logger category to the App context value the backend uses so
/// RensaioBackend can tag re-emitted events exactly like
/// LibraryTaggingLoggerFactory does today (MihonEx for the bridge, Android for
/// the compat layer).
/// </summary>
public static class AppTagger
{
    public const string AppMihon = "MihonEx";
    public const string AppAndroid = "Android";

    public static string ForCategory(string? category)
    {
        if (string.IsNullOrEmpty(category))
            return AppMihon;

        if (category.Equals(AppAndroid, StringComparison.OrdinalIgnoreCase))
            return AppAndroid;

        return AppMihon;
    }
}
