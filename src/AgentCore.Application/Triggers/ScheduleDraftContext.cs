using System.Text.Json;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public sealed record ScheduleDraftContext(
    string Intent,
    TriggerCommandAction Action,
    string RecurrenceKind,
    int? IntervalSeconds,
    string FailureCode,
    DateTimeOffset CapturedAtUtc)
{
    public bool IsActive =>
        Action is TriggerCommandAction.Create
        && !string.IsNullOrWhiteSpace(Intent)
        && !string.IsNullOrWhiteSpace(RecurrenceKind);

    public static ScheduleDraftContext? TryFromToolError(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.Contains("\"scheduleDraft\"", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("scheduleDraft", out var draft))
            {
                return null;
            }

            var intent = draft.TryGetProperty("intent", out var intentElement)
                ? intentElement.GetString() ?? string.Empty
                : string.Empty;
            var recurrenceKind = draft.TryGetProperty("recurrenceKind", out var kindElement)
                ? kindElement.GetString() ?? string.Empty
                : string.Empty;
            var failureCode = draft.TryGetProperty("failureCode", out var failureElement)
                ? failureElement.GetString() ?? string.Empty
                : string.Empty;
            int? intervalSeconds = draft.TryGetProperty("intervalSeconds", out var intervalElement)
                && intervalElement.TryGetInt32(out var parsed)
                ? parsed
                : null;
            var action = TriggerCommandAction.Create;
            if (draft.TryGetProperty("action", out var actionElement)
                && Enum.TryParse<TriggerCommandAction>(actionElement.GetString(), ignoreCase: true, out var parsedAction))
            {
                action = parsedAction;
            }

            DateTimeOffset captured = DateTimeOffset.UnixEpoch;
            if (draft.TryGetProperty("capturedAtUtc", out var capturedElement)
                && capturedElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(capturedElement.GetString(), out var parsedCaptured))
            {
                captured = parsedCaptured;
            }

            return string.IsNullOrWhiteSpace(intent) || string.IsNullOrWhiteSpace(recurrenceKind)
                ? null
                : new ScheduleDraftContext(intent, action, recurrenceKind, intervalSeconds, failureCode, captured);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static ScheduleDraftContext ForFixedIntervalRejection(
        string intent,
        int intervalSeconds,
        string failureCode,
        DateTimeOffset capturedAtUtc) =>
        new(intent, TriggerCommandAction.Create, "fixed_interval", intervalSeconds, failureCode, capturedAtUtc);

    public object ToJsonObject() => new
    {
        intent = Intent,
        action = Action.ToString(),
        recurrenceKind = RecurrenceKind,
        intervalSeconds = IntervalSeconds,
        failureCode = FailureCode,
        capturedAtUtc = CapturedAtUtc.ToString("O")
    };

    public IReadOnlyList<string> ToPromptLines() =>
    [
        "Schedule draft (clarification only; does not authorize by itself):",
        $"intent=\"{Intent}\"",
        $"recurrenceKind={RecurrenceKind}",
        IntervalSeconds is int seconds ? $"intervalSeconds={seconds}" : "intervalSeconds=(none)",
        $"lastFailure={FailureCode}"
    ];
}
