using System.Diagnostics;
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
    public IReadOnlyList<KeyValuePair<string, object?>> ToScope(Guid diagnosticId, string? traceId)
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
        if (diagnosticId == Guid.Empty)
        {
            logger.Log(level, exception, "{DiagnosticMessage}", SafeMessage(message));
            return;
        }

        var traceId = CurrentTraceId();
        var diagnosticMessage = SafeMessage(message);
        using (logger.BeginScope(context.ToScope(diagnosticId, traceId)))
        {
            logger.Log(level, exception, "{DiagnosticMessage} {DiagnosticId}", diagnosticMessage, diagnosticId);
        }
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
}
