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

    public static string NormalizeToolOutcome(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return "empty";
        }

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return "ok";
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

        if (!BrowserToolCatalog.TryGet(toolName, out var feature)) return string.Empty;
        var parts = new List<string> { $"feature={feature.Feature}" };
        if (TryReadBoolProperty(resultJson, "settled", out var settled)) parts.Add($"settled={settled.ToString().ToLowerInvariant()}");
        if (TryReadBoolProperty(resultJson, "truncated", out var truncated)) parts.Add($"truncated={truncated.ToString().ToLowerInvariant()}");
        if (TryReadIntProperty(resultJson, "returnedCount", out var count)) parts.Add($"returnedCount={count}");
        return string.Join(';', parts);
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
            if (document.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;
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

    private static string Clip(string? value, int max)
    {
        var text = value ?? string.Empty;
        return text.Length <= max ? text : text[..max];
    }
}
