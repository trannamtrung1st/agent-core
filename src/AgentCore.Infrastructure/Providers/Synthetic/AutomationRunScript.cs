using System.Text.Json;
using AgentCore.Application.Experience;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

internal static class AutomationRunScript
{
    public static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        if (!request.Messages.Any(m => m.Role == ModelRole.System && m.Text.StartsWith("Bounded background Session task.", StringComparison.Ordinal))) return null;
        var prompt = request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text ?? "";
        bool Offers(string name) => request.Tools?.Any(t => t.Name == name) == true;
        var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        var selectedSession = System.Text.RegularExpressions.Regex.Match(prompt,
            @"synthetic-automation-review-session:\s*([0-9a-fA-F-]{36})");
        if (selectedSession.Success)
        {
            if (!Offers(ExperienceService.SourceTool) || !Offers(ExperienceService.RecordTool))
                return Complete("NoAction", "Experience review is unavailable.", false);
            var recorded = results.LastOrDefault(m => m.Name == ExperienceService.RecordTool);
            if (recorded is not null)
                return Complete((recorded.Text.Contains("\"error\"", StringComparison.Ordinal) || recorded.Text.Contains("\"changed\":false", StringComparison.Ordinal)) ? "NoAction" : "Response",
                    "Reviewed the selected completed Session.", false);
            var inspected = results.LastOrDefault(m => m.Name == ExperienceService.SourceTool);
            if (inspected is null) return Call(ExperienceService.SourceTool, new { sourceKind = "Session", sourceId = selectedSession.Groups[1].Value });
            using var source = JsonDocument.Parse(inspected.Text);
            if (!source.RootElement.TryGetProperty("throughCursor", out var cursor))
                return Complete("NoAction", "No stable owned source was available.", false);
            return Call(ExperienceService.RecordTool, new {
                sourceKind = "Session", sourceId = selectedSession.Groups[1].Value, throughCursor = cursor.GetInt64(),
                goal = "Review observable completed work", attempts = Array.Empty<string>(), decisions = Array.Empty<string>(),
                outcomes = new[] { "Reviewed a stable completed source checkpoint" }, corrections = Array.Empty<string>(),
                unresolved = Array.Empty<string>(), difficulties = Array.Empty<string>(), lessons = new[] { "Verify observed state before acting" }
            });
        }
        if (prompt.Contains("\"experienceId\"", StringComparison.Ordinal))
        {
            if (results.Any(m => m.Name == ExperienceService.RecordTool))
                return Complete("Response", "Recorded observable completed work as Experience.", false);
            var source = string.Join("\n", request.Messages.Where(m => m.Text.Contains("Selected Experience source", StringComparison.Ordinal)).Select(m => m.Text));
            if (source.Length == 0 || !Offers(ExperienceService.RecordTool))
                return Complete("NoAction", "No eligible selected source is available.", false);
            return Call(ExperienceService.RecordTool, new AgentCore.Domain.Experience.ExperienceContent(
                "Review observable completed work", [], [], ["Completed observable source checkpoint"],
                source.Contains("correction", StringComparison.OrdinalIgnoreCase) ? ["The user supplied a correction"] : [], [],
                source.Contains("failed", StringComparison.OrdinalIgnoreCase) ? ["A recorded approach failed"] : [],
                ["Verify observable state before acting"]));
        }
        if (prompt.Contains("synthetic-automation-improve", StringComparison.Ordinal))
        {
            if (!Offers("skills.create") || !Offers("skills.list")) return Complete("NoAction", "Skill management is unavailable; no change was made.", false);
            if (results.LastOrDefault(m => m.Name == "skills.create") is { } saved)
                return Complete(saved.Text.Contains("\"error\"", StringComparison.Ordinal) ? "NoAction" : "Response", "Created an independent Instance review Skill.", false);
            if (results.LastOrDefault(m => m.Name == "skills.list") is not { } inspected) return Call("skills.list", new { });
            using var inspection = JsonDocument.Parse(inspected.Text);
            if (inspection.RootElement.ValueKind == JsonValueKind.Array && inspection.RootElement.EnumerateArray().Any(s => s.GetProperty("name").GetString() == "Experience review"))
                return Complete("NoAction", "The review Skill already exists.", false);
            return Call("skills.create", new { name = "Experience review", description = "Verify observed outcomes.",
                procedure = "Observe current state, check outcomes, and report confirmed results.", projection = "OnDemand", enabled = true, requiredCapabilities = Array.Empty<string>() });
        }
        if (prompt.Contains("synthetic-automation-attention", StringComparison.Ordinal))
            return Complete("NeedsAttention", "An unresolved checkpoint needs the owner's attention.", true);
        if (prompt.Contains("Call John", StringComparison.OrdinalIgnoreCase))
            return Complete("NeedsAttention", "Reminder: Call John.", true);
        if (prompt.Contains("check the oven", StringComparison.OrdinalIgnoreCase))
            return Complete("NeedsAttention", "Oven is ready.", true);
        if (prompt.Contains(ScriptedLanguageModel.SensitiveApprovalMarker, StringComparison.Ordinal) && Offers(ToolCatalog.DemoSensitiveAction))
        {
            var action = results.LastOrDefault(m => m.Name == ToolCatalog.DemoSensitiveAction);
            return action is null ? Call(ToolCatalog.DemoSensitiveAction, new { label = "Synthetic sensitive approval" })
                : Complete("Response", "Sensitive action completed after approval.", false);
        }
        return Complete("NoAction", "No meaningful change requires action.", false);
    }
    private static IReadOnlyList<ModelGenerationEvent> Complete(string outcome, string summary, bool attentionRequired) =>
        Call(ToolCatalog.WorkComplete, new { outcome, summary, attentionRequired });
    private static IReadOnlyList<ModelGenerationEvent> Call(string name, object args) =>
        [new ModelToolCallEvent(new("automation-" + name, name, JsonSerializer.Serialize(args, new JsonSerializerOptions(JsonSerializerDefaults.Web)))), new ModelCompleted(ModelStopReason.ToolCalls)];
}
