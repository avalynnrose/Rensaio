using System.Collections;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Rensaio.RPC.Contracts;
using Rensaio.RPC.Runtime;

namespace Rensaio.RPC.Logging;

/// <summary>
/// Secondary logging provider that publishes each log event to the gRPC
/// <see cref="RpcLogHub"/> in the same RENS-RPC delimited format the stdout provider
/// uses. It exists so the backend can receive logs over gRPC (ControlService.StreamLogs)
/// now that the child is launched detached with no stdout/stderr redirection.
///
/// Kept separate from <see cref="StdoutLoggerProvider"/> so both concerns stay
/// independent (stdout for local/console runs; hub for the detached/backend case).
/// </summary>
public sealed class RpcLogStreamProvider : ILoggerProvider
{
    private readonly RpcLogHub _hub;
    private readonly LogLevel _minimum;

    public RpcLogStreamProvider(RpcLogHub hub, LogLevel minimum = LogLevel.Information)
    {
        _hub = hub;
        _minimum = minimum;
    }

    public ILogger CreateLogger(string categoryName) => new HubLogger(categoryName, this);

    private void Write(string category, LogLevel level, string message, string template, IReadOnlyList<RpcLogProperty> properties)
    {
        if (level < _minimum)
            return;

        string line = new RpcLogLine
        {
            Level = MapLevel(level),
            Category = category,
            Correlation = string.Empty,
            App = AppTagger.ForCategory(category),
            Message = message,
            Template = template,
            Properties = properties,
        }.ToLine();

        _hub.Publish(line);
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

    private sealed class HubLogger : ILogger
    {
        private readonly string _category;
        private readonly RpcLogStreamProvider _provider;

        public HubLogger(string category, RpcLogStreamProvider provider)
        {
            _category = category;
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider._minimum;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            string message = formatter(state, exception);
            if (exception != null)
                message = message + Environment.NewLine + exception;

            string template = string.Empty;
            var properties = new List<RpcLogProperty>();

            if (state is IReadOnlyList<KeyValuePair<string, object?>> kvs)
            {
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

        private static RpcLogProperty ToProperty(string name, object? value)
        {
            switch (value)
            {
                case null:
                    return new RpcLogProperty { Name = name, Kind = RpcLogValueKind.Null };

                case bool b:
                    return new RpcLogProperty { Name = name, Kind = RpcLogValueKind.Bool, Value = b ? "true" : "false" };

                case byte or sbyte or short or ushort or int or uint or long or ulong
                     or float or double or decimal:
                    return new RpcLogProperty
                    {
                        Name = name,
                        Kind = RpcLogValueKind.Number,
                        Value = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                    };

                case string s:
                    return new RpcLogProperty { Name = name, Kind = RpcLogValueKind.String, Value = s };

                case DateTime or DateTimeOffset or TimeSpan or Guid:
                    return new RpcLogProperty
                    {
                        Name = name,
                        Kind = RpcLogValueKind.String,
                        Value = value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture)
                                                        : value.ToString() ?? string.Empty,
                    };

                case IEnumerable:
                    return new RpcLogProperty { Name = name, Kind = RpcLogValueKind.String, Value = DescribeEnumerable((IEnumerable)value) };

                default:
                    return new RpcLogProperty { Name = name, Kind = RpcLogValueKind.String, Value = value.ToString() ?? string.Empty };
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

    public void Dispose()
    {
    }
}
