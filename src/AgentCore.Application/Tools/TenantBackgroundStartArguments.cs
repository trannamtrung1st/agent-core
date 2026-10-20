using System.Text.Json;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tools;

public sealed record TenantBackgroundStartArguments(string Objective, string Title, bool ReportCompletion)
{
    public static TenantBackgroundStartArguments Parse(string json)
    {
        using var document = JsonDocument.Parse(json); var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => p.Name is not ("objective" or "title" or "reportCompletion"))
            || value.EnumerateObject().Select(p => p.Name).Distinct().Count() != value.EnumerateObject().Count()
            || !value.TryGetProperty("objective", out var objective) || objective.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(objective.GetString()) || objective.GetString()!.Length > AgentRunLimits.MaxBackgroundObjectiveCharacters
            || value.TryGetProperty("title", out var title) && (title.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(title.GetString()) || title.GetString()!.Length > 80)
            || value.TryGetProperty("reportCompletion", out var report) && report.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException("Use an exact bounded background task.");
        var text = objective.GetString()!.Trim();
        return new(text, title.ValueKind == JsonValueKind.String ? title.GetString()!.Trim() : text[..Math.Min(text.Length, 80)], report.ValueKind != JsonValueKind.False);
    }
    public string Canonical => JsonSerializer.Serialize(new { objective = Objective, title = Title, reportCompletion = ReportCompletion });
}
