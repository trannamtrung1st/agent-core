using System.Text.Json;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public sealed record ScheduleDraftContext(
    string Instructions,
    TriggerCommandAction Action,
    string RecurrenceKind,
    int? IntervalSeconds,
    string FailureCode,
    DateTimeOffset CapturedAtUtc,
    string? ArgumentsJson = null)
{
    public bool IsActive =>
        Action is TriggerCommandAction.Create
        && !string.IsNullOrWhiteSpace(Instructions)
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
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("scheduleDraft", out var draft) || draft.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var intent = draft.TryGetProperty("instructions", out var intentElement)
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
                : new ScheduleDraftContext(intent, action, recurrenceKind, intervalSeconds, failureCode, captured,
                    draft.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object
                        ? arguments.GetRawText() : null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
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
        instructions = Instructions,
        action = Action.ToString(),
        recurrenceKind = RecurrenceKind,
        intervalSeconds = IntervalSeconds,
        failureCode = FailureCode,
        capturedAtUtc = CapturedAtUtc.ToString("O"),
        arguments = ArgumentsJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(ArgumentsJson)
    };

    public IReadOnlyList<string> ToPromptLines() =>
    [
        "Schedule draft (clarification only; does not authorize by itself):",
        $"intent=\"{Instructions}\"",
        $"recurrenceKind={RecurrenceKind}",
        IntervalSeconds is int seconds ? $"intervalSeconds={seconds}" : "intervalSeconds=(none)",
        $"lastFailure={FailureCode}",
        ArgumentsJson is null ? "previousArguments=(unavailable)" : $"previousArguments={ArgumentsJson}"
    ];
}
