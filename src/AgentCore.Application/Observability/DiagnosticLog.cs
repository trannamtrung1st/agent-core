using System.Diagnostics;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Domain.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Observability;

public readonly record struct DiagnosticContext(
    Guid? CorrelationId = null,
    Guid? SessionId = null,
    Guid? ResponseId = null,
    Guid? AgentInstanceId = null,
    Guid? TriggerRegistrationId = null,
    Guid? TriggerOccurrenceId = null,
    Guid? WorkItemId = null,
    string? ErrorCategory = null,
    string? ErrorCode = null,
    string? ProviderAlias = null)
{
    public IReadOnlyList<KeyValuePair<string, object?>> ToFields(Guid diagnosticId, string? traceId)
    {
        var items = new List<KeyValuePair<string, object?>>(12)
        {
            new("DiagnosticId", diagnosticId)
        };
        AddGuid(items, "CorrelationId", CorrelationId);
        AddText(items, "TraceId", traceId);
        AddGuid(items, "SessionId", SessionId);
        AddGuid(items, "ResponseId", ResponseId);
        AddGuid(items, "AgentInstanceId", AgentInstanceId);
        AddGuid(items, "TriggerRegistrationId", TriggerRegistrationId);
        AddGuid(items, "TriggerOccurrenceId", TriggerOccurrenceId);
        AddGuid(items, "WorkItemId", WorkItemId);
        AddToken(items, "ErrorCategory", ErrorCategory);
        AddToken(items, "ErrorCode", ErrorCode);
        AddToken(items, "ProviderAlias", ProviderAlias);
        return items;
    }

    private static void AddGuid(List<KeyValuePair<string, object?>> items, string name, Guid? value)
    {
        if (value is Guid id && id != Guid.Empty)
        {
            items.Add(new KeyValuePair<string, object?>(name, id));
        }
    }

    private static void AddText(List<KeyValuePair<string, object?>> items, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            items.Add(new KeyValuePair<string, object?>(name, value));
        }
    }

    private static void AddToken(List<KeyValuePair<string, object?>> items, string name, string? value)
    {
        if (FailureReference.IsSafeToken(value))
        {
            items.Add(new KeyValuePair<string, object?>(name, value));
        }
    }
}

public static class DiagnosticLog
{
    public static void Warning(
        ILogger logger,
        Exception exception,
        Guid diagnosticId,
        string message,
        DiagnosticContext context = default) =>
        Write(logger, LogLevel.Warning, exception, diagnosticId, message, context);

    private static void Write(
        ILogger logger,
        LogLevel level,
        Exception? exception,
        Guid diagnosticId,
        string message,
        DiagnosticContext context = default)
    {
        ArgumentNullException.ThrowIfNull(logger);
        MarkActivity(context);
        IReadOnlyList<KeyValuePair<string, object?>> fields = diagnosticId == Guid.Empty
            ? []
            : context.ToFields(diagnosticId, CurrentTraceId());
        var record = DiagnosticLogRecord.Create(SafeMessage(message), fields);
        logger.Log(level, default, record, exception, static (state, _) => state.ToString());
    }

    private static void MarkActivity(DiagnosticContext context)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error);
        if (FailureReference.IsSafeToken(context.ErrorCategory))
        {
            activity.SetTag("error.category", context.ErrorCategory);
        }

        if (FailureReference.IsSafeToken(context.ErrorCode))
        {
            activity.SetTag("error.code", context.ErrorCode);
        }
    }

    private static string? CurrentTraceId()
    {
        var activity = Activity.Current;
        if (activity is null || activity.TraceId == default)
        {
            return null;
        }

        return activity.TraceId.ToHexString();
    }

    private static string SafeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Execution failed.";
        }

        foreach (var character in message)
        {
            if (char.IsControl(character))
            {
                return "Execution failed.";
            }
        }

        return message;
    }

    private sealed class DiagnosticLogRecord : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?>[] _items;
        private readonly string _text;

        private DiagnosticLogRecord(KeyValuePair<string, object?>[] items, string text)
        {
            _items = items;
            _text = text;
        }

        public static DiagnosticLogRecord Create(string message, IReadOnlyList<KeyValuePair<string, object?>> fields)
        {
            var items = new KeyValuePair<string, object?>[fields.Count + 1];
            var text = new StringBuilder(message.Length + (fields.Count * 48));
            text.Append(message);
            for (var index = 0; index < fields.Count; index++)
            {
                var field = fields[index];
                items[index] = field;
                text.Append(' ');
                text.Append(field.Key);
                text.Append('=');
                text.Append(field.Value);
            }

            var rendered = text.ToString();
            items[^1] = new KeyValuePair<string, object?>("{OriginalFormat}", rendered);
            return new DiagnosticLogRecord(items, rendered);
        }

        public int Count => _items.Length;

        public KeyValuePair<string, object?> this[int index] => _items[index];

        public override string ToString() => _text;

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            foreach (var item in _items)
            {
                yield return item;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
