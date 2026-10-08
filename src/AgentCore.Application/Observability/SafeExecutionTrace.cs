using System.Text.Json;
using AgentCore.Application.Tools;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Observability;

public static class SafeExecutionTrace
{
    public const string ToolStepStage = "tool.step";
    public const string GenerationTerminalStage = "generation.terminal";

    private const int MaxToolNameLength = 80;
    private const int MaxOutcomeLength = 80;
    private const int MaxDetailLength = 480;
    private const int MaxAccessibleNameLength = 80;

    private static readonly HashSet<string> ArgumentReasons = new(StringComparer.Ordinal)
    {
        "missing_ref",
        "invalid_ref",
        "missing_value",
        "value_too_long",
        "missing_key",
        "unsupported_key",
        "missing_artifact_id",
        "invalid_artifact_id",
        "unsupported_property"
    };
    private const int MaxRoleLength = 40;

    public static string NormalizeToolOutcome(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return "empty";
        }

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() is { Length: > 0 } code)
            {
                return Clip(code, MaxOutcomeLength);
            }

            if (document.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && status.GetString() is { Length: > 0 } statusValue)
            {
                return Clip(statusValue, MaxOutcomeLength);
            }
        }
        catch (JsonException)
        {
            return "invalid_json";
        }

        return "ok";
    }

    public static string BuildToolDetail(string toolName, string? argumentsJson, string? resultJson)
    {
        if (!ToolCatalog.IsBrowserTool(toolName))
        {
            return ScheduleFailureDetail(resultJson);
        }

        return toolName switch
        {
            ToolCatalog.BrowserNavigate => BuildNavigateDetail(argumentsJson, resultJson),
            ToolCatalog.BrowserSnapshot => BuildObserveDetail(argumentsJson, resultJson),
            _ when BrowserToolCatalog.IsInteraction(toolName) => BuildActDetail(argumentsJson, resultJson),
            ToolCatalog.BrowserClose => BuildCloseDetail(resultJson),
            _ => string.Empty
        };
    }

    public static void RecordToolStep(
        ILogger logger,
        Guid sessionId,
        Guid responseId,
        int stepNumber,
        string toolName,
        double durationMs,
        string outcome,
        string? detail)
    {
        var safeTool = Clip(toolName, MaxToolNameLength);
        var safeOutcome = Clip(outcome, MaxOutcomeLength);
        var safeDetail = Clip(SafeLogRedactor.Redact(detail ?? string.Empty), MaxDetailLength);
        var correlation = $"sessionId={sessionId:D};responseId={responseId:D};step={stepNumber}";
        var payload = string.IsNullOrEmpty(safeDetail)
            ? $"{correlation};tool={safeTool};outcome={safeOutcome}"
            : $"{correlation};tool={safeTool};outcome={safeOutcome};{safeDetail}";
        RuntimeTelemetry.RecordDiagnostic(ToolStepStage, durationMs, payload);
        logger.LogInformation(
            "Tool step {SessionId} {ResponseId} step {Step} tool {Tool} outcome {Outcome} durationMs {DurationMs} detail {Detail}",
            sessionId,
            responseId,
            stepNumber,
            safeTool,
            safeOutcome,
            Math.Round(durationMs, 1),
            string.IsNullOrEmpty(safeDetail) ? "-" : safeDetail);
    }

    public static void RecordGenerationTerminal(
        ILogger logger,
        Guid sessionId,
        Guid responseId,
        bool failed,
        string channel,
        string? stopReason,
        string? disposition,
        string? actionKind,
        bool hasDisplayText,
        string? failureCategory = null,
        string? failureCode = null)
    {
        var safeChannel = Clip(channel, 40);
        var safeStop = Clip(stopReason ?? "none", 40);
        var safeDisposition = Clip(disposition ?? "none", 40);
        var safeAction = Clip(actionKind ?? "none", 40);
        var safeCategory = Clip(failureCategory ?? "none", 40);
        var safeCode = Clip(failureCode ?? "none", 40);
        var kind = failed ? "provider_failure" : "terminal_response";
        var detail =
            $"sessionId={sessionId:D};responseId={responseId:D};kind={kind};channel={safeChannel};stopReason={safeStop};disposition={safeDisposition};action={safeAction};hasDisplayText={hasDisplayText};failureCategory={safeCategory};failureCode={safeCode}";
        RuntimeTelemetry.RecordDiagnostic(GenerationTerminalStage, 0, detail);
        logger.LogInformation(
            "Generation terminal {SessionId} {ResponseId} kind {Kind} channel {Channel} stopReason {StopReason} disposition {Disposition} action {Action} hasDisplayText {HasDisplayText} failureCategory {FailureCategory} failureCode {FailureCode}",
            sessionId,
            responseId,
            kind,
            safeChannel,
            safeStop,
            safeDisposition,
            safeAction,
            hasDisplayText,
            safeCategory,
            safeCode);
    }

    internal static bool SensitiveAccessibleName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var haystack = name.ToLowerInvariant();
        string[] terms =
        [
            "password",
            "passwd",
            "passcode",
            "secret",
            "token",
            "api key",
            "apikey",
            "access key",
            "private key",
            "client secret",
            "authorization",
            "one-time-code",
            "otp"
        ];
        return terms.Any(term => haystack.Contains(term, StringComparison.Ordinal));
    }

    private static string ScheduleFailureDetail(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (!document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.String
                || error.GetString() != "schedule_validation_failed"
                || !document.RootElement.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            var text = message.GetString() ?? string.Empty;
            const string marker = "Present: ";
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                return "schedule_validation_failed";
            }

            var rest = text[(at + marker.Length)..];
            var end = rest.IndexOf('.');
            return "present=" + Clip(end >= 0 ? rest[..end] : rest, 80);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string BuildNavigateDetail(string? argumentsJson, string? resultJson)
    {
        var parts = new List<string>();
        if (TryReadStringProperty(argumentsJson, "url", out var url))
        {
            parts.Add($"path={SafeUrlPath(url)}");
        }

        if (TryReadStringProperty(resultJson, "url", out var observed))
        {
            parts.Add($"resultPath={SafeUrlPath(observed)}");
        }

        AppendSettled(parts, resultJson);
        return string.Join(';', parts);
    }

    private static string BuildObserveDetail(string? argumentsJson, string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson) && string.IsNullOrWhiteSpace(argumentsJson))
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (TryReadStringProperty(argumentsJson, "waitFor", out var waitFor)
            && string.Equals(waitFor, "stable", StringComparison.Ordinal))
        {
            parts.Add("waitFor=stable");
        }

        if (TryReadIntProperty(argumentsJson, "timeoutMs", out var timeoutMs)
            && timeoutMs is >= BrowserToolLimits.MinObserveTimeoutMs and <= BrowserToolLimits.MaxObserveTimeoutMs)
        {
            parts.Add($"timeoutMs={timeoutMs}");
        }

        if (TryReadStringProperty(resultJson, "url", out var url))
        {
            parts.Add($"path={SafeUrlPath(url)}");
        }

        if (TryReadStringProperty(resultJson, "error", out var error) && error == "user_intervention_required")
        {
            parts.Add("intervention=true");
        }
        else if (!string.IsNullOrWhiteSpace(resultJson))
        {
            parts.Add("intervention=false");
        }

        AppendSettled(parts, resultJson);
        if (!string.IsNullOrWhiteSpace(resultJson))
        {
            parts.Add($"elementCount={CountElements(resultJson)}");
        }

        return string.Join(';', parts);
    }

    private static void AppendSettled(List<string> parts, string? resultJson)
    {
        if (TryReadBoolProperty(resultJson, "settled", out var settled))
        {
            parts.Add(settled ? "settled=true" : "settled=false");
        }
    }

    private static string BuildActDetail(string? argumentsJson, string? resultJson)
    {
        if (!TryReadStringProperty(argumentsJson, "operation", out var operation))
        {
            return string.Empty;
        }

        var parts = new List<string> { $"operation={Clip(operation, 20)}" };
        if (TryReadStringProperty(resultJson, "reason", out var argumentReason)
            && ArgumentReasons.Contains(argumentReason))
        {
            parts.Add($"argumentReason={argumentReason}");
        }
        if (TryReadStringProperty(resultJson, "url", out var url))
        {
            parts.Add($"path={SafeUrlPath(url)}");
        }

        AppendSettled(parts, resultJson);
        if (string.Equals(operation, "upload", StringComparison.Ordinal))
        {
            if (TryResolveActTarget(argumentsJson, resultJson, out var role, out var name))
            {
                parts.Add($"targetRole={role}");
                if (!string.IsNullOrEmpty(name))
                {
                    parts.Add($"targetName={name}");
                }
            }
            else
            {
                parts.Add("targetName=Picture");
            }

            return string.Join(';', parts);
        }

        if (TryResolveActTarget(argumentsJson, resultJson, out var actRole, out var actName))
        {
            parts.Add($"targetRole={actRole}");
            if (!string.IsNullOrEmpty(actName))
            {
                parts.Add($"targetName={actName}");
            }
        }

        return string.Join(';', parts);
    }

    private static string BuildCloseDetail(string? resultJson)
    {
        return TryReadStringProperty(resultJson, "status", out var status)
            ? $"status={Clip(status, 40)}"
            : string.Empty;
    }

    private static bool TryResolveActTarget(
        string? argumentsJson,
        string? resultJson,
        out string role,
        out string name)
    {
        role = string.Empty;
        name = string.Empty;
        if (!TryReadStringProperty(argumentsJson, "ref", out var reference)
            || string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        if (!TryFindElement(resultJson, reference, out var elementRole, out var elementName))
        {
            return false;
        }

        role = Clip(elementRole, MaxRoleLength);
        if (!SensitiveAccessibleName(elementName))
        {
            name = Clip(elementName, MaxAccessibleNameLength);
        }

        return !string.IsNullOrEmpty(role) || !string.IsNullOrEmpty(name);
    }

    private static bool TryFindElement(string? json, string reference, out string role, out string name)
    {
        role = string.Empty;
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("elements", out var elements)
                || elements.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var element in elements.EnumerateArray())
            {
                if (!element.TryGetProperty("ref", out var refProperty)
                    || refProperty.ValueKind != JsonValueKind.String
                    || !string.Equals(refProperty.GetString(), reference, StringComparison.Ordinal))
                {
                    continue;
                }

                if (element.TryGetProperty("role", out var roleProperty) && roleProperty.ValueKind == JsonValueKind.String)
                {
                    role = roleProperty.GetString() ?? string.Empty;
                }

                if (element.TryGetProperty("name", out var nameProperty) && nameProperty.ValueKind == JsonValueKind.String)
                {
                    name = nameProperty.GetString() ?? string.Empty;
                }

                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static int CountElements(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("elements", out var elements)
                || elements.ValueKind != JsonValueKind.Array)
            {
                return 0;
            }

            return elements.GetArrayLength();
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static bool TryReadBoolProperty(string? json, string propertyName, out bool value)
    {
        value = false;
        if (!TryReadProperty(json, propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryReadIntProperty(string? json, string propertyName, out int value)
    {
        value = 0;
        if (!TryReadProperty(json, propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        return property.TryGetInt32(out value);
    }

    private static bool TryReadProperty(string? json, string propertyName, out JsonElement property)
    {
        property = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty(propertyName, out var found))
            {
                return false;
            }

            property = found.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadStringProperty(string? json, string propertyName, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String
                && property.GetString() is { } text)
            {
                value = text;
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static string SafeUrlPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Clip(SafeLogRedactor.Redact(url), MaxDetailLength);
        }

        var path = uri.GetLeftPart(UriPartial.Path);
        return Clip(SafeLogRedactor.Redact(path), MaxDetailLength);
    }

    private static string Clip(string? value, int max)
    {
        var text = value ?? string.Empty;
        return text.Length <= max ? text : text[..max];
    }
}
