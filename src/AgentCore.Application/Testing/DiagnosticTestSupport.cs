using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Testing;

public sealed class QueueDiagnosticIdSource : IDiagnosticIdSource
{
    private readonly Queue<Guid> _ids;

    public QueueDiagnosticIdSource(IEnumerable<Guid> ids) => _ids = new Queue<Guid>(ids);

    public Guid NewId()
    {
        if (_ids.Count == 0)
        {
            throw new InvalidOperationException("Diagnostic id queue is empty.");
        }

        return _ids.Dequeue();
    }
}

public sealed record DiagnosticLogEntry(
    LogLevel Level,
    Exception? Exception,
    string Message,
    IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyDictionary<string, object?> Scope);

public sealed class DiagnosticLogCapture<T> : ILogger<T>
{
    private Dictionary<string, object?>? _scope;

    public List<DiagnosticLogEntry> Entries { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        var previous = _scope;
        _scope = ReadPairs(state);
        return new ScopeCookie(() => _scope = previous);
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new DiagnosticLogEntry(
            logLevel,
            exception,
            formatter(state, exception),
            ReadPairs(state),
            _scope is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(_scope, StringComparer.Ordinal)));
    }

    private static Dictionary<string, object?> ReadPairs<TState>(TState state)
    {
        var pairs = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (state is not System.Collections.IEnumerable enumerable)
        {
            return pairs;
        }

        foreach (var item in enumerable)
        {
            if (item is KeyValuePair<string, object?> pair)
            {
                if (pair.Key != "{OriginalFormat}")
                {
                    pairs[pair.Key] = pair.Value;
                }

                continue;
            }

            var itemType = item?.GetType();
            if (itemType is null
                || !itemType.IsGenericType
                || itemType.GetGenericTypeDefinition() != typeof(KeyValuePair<,>))
            {
                continue;
            }

            if (itemType.GetProperty("Key")?.GetValue(item) is not string key || key == "{OriginalFormat}")
            {
                continue;
            }

            pairs[key] = itemType.GetProperty("Value")?.GetValue(item);
        }

        return pairs;
    }

    private sealed class ScopeCookie(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

public static class DiagnosticActivity
{
    public static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RuntimeTelemetry.Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

public sealed class RuntimeMetricProbe : IDisposable
{
    public static readonly string[] IdentityTagNames =
    [
        "DiagnosticId",
        "CorrelationId",
        "TraceId",
        "SessionId",
        "ResponseId",
        "WorkItemId",
        "TriggerOccurrenceId",
        "AutomationId",
        "AgentInstanceId"
    ];

    private readonly object _gate = new();
    private readonly List<(string Instrument, string Key, object? Value)> _tags = [];
    private readonly MeterListener _listener;

    private RuntimeMetricProbe()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RuntimeTelemetry.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.SetMeasurementEventCallback<double>(OnMeasurement);
    }

    public static RuntimeMetricProbe Start()
    {
        var probe = new RuntimeMetricProbe();
        probe._listener.Start();
        return probe;
    }

    public IReadOnlyList<(string Instrument, string Key, object? Value)> Tags
    {
        get
        {
            lock (_gate)
            {
                return [.. _tags];
            }
        }
    }

    public void Dispose() => _listener.Dispose();

    private void OnMeasurement<T>(
        Instrument instrument,
        T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        lock (_gate)
        {
            foreach (var tag in tags)
            {
                _tags.Add((instrument.Name, tag.Key, tag.Value));
            }
        }
    }
}
