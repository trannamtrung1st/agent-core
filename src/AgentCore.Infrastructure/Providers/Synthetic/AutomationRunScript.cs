using System.Text.Json;
using AgentCore.Application.Experience;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

internal static class AutomationRunScript
{
    public static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        if (!request.Messages.Any(m => m.Role == ModelRole.System && m.Text.StartsWith("Bounded Automation Run.", StringComparison.Ordinal))) return null;
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
                return Complete((recorded.Text.Contains("\"error\"", StringComparison.Ordinal) || recorded.Text.Contains("\"changed\":false", StringComparison.Ordinal)) ? "NoAction" : "ActionCompleted",
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
                return Complete("ActionCompleted", "Recorded observable completed work as Experience.", false);
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
            if (!Offers(HarnessChatTools.Inspect) || !Offers("harness.skill.upsert"))
                return Complete("NoAction", "Harness management is unavailable; no change was made.", false);
            if (!request.Messages.Any(m => (m.Text.StartsWith("Historical Experience", StringComparison.Ordinal) || m.Text.StartsWith("Historical Continuity", StringComparison.Ordinal) && m.Text.Contains("\"kind\":\"Experience\"", StringComparison.Ordinal))))
                return Complete("NoAction", "No prior experience needs investigation.", false);
            var saved = results.LastOrDefault(m => m.Name == "harness.skill.upsert");
            if (saved is not null)
            {
                using var response = JsonDocument.Parse(saved.Text);
                var changed = response.RootElement.TryGetProperty("saved", out var flag) && flag.ValueKind == JsonValueKind.True;
                return Complete(changed ? "ActionCompleted" : "NoAction", changed
                    ? "Refined the review Skill after exact approval and Core verification. External outcomes remain unverified."
                    : "The proposed change was not applied.", false);
            }
            var inspected = results.LastOrDefault(m => m.Name == HarnessChatTools.Inspect);
            if (inspected is null) return Call(HarnessChatTools.Inspect, new { });
            using var inspection = JsonDocument.Parse(inspected.Text);
            var root = inspection.RootElement;
            if (!root.TryGetProperty("activeDefinitionVersion", out var activeVersion))
                return Complete("NoAction", "Harness inspection is incomplete; no change was made.", false);
            if (root.TryGetProperty("skills", out var skills) && skills.EnumerateArray().Any(s =>
                (s.TryGetProperty("name", out var name) || s.TryGetProperty("Name", out name)) && name.GetString() == "Experience review"))
                return Complete("NoAction", "The review Skill already incorporates the experience; nothing new needs action.", false);
            return Call("harness.skill.upsert", new
            {
                expectedVersion = activeVersion.GetInt32(),
                policyRevision = root.GetProperty("policyRevision").GetInt64(),
                skill = new { name = "Experience review", description = "Verify observed state before reviewing store outcomes.",
                    procedure = "Observe current page state before choosing a browser action. Check the outcome and report only confirmed results." }
            });
        }
        if (prompt.Contains("synthetic-automation-attention", StringComparison.Ordinal))
            return Complete("AttentionRequested", "An unresolved checkpoint needs the owner's attention.", true);
        if (prompt.Contains("Call John", StringComparison.OrdinalIgnoreCase))
            return Complete("AttentionRequested", "Reminder: Call John.", true);
        if (prompt.Contains("check the oven", StringComparison.OrdinalIgnoreCase))
            return Complete("AttentionRequested", "Oven is ready.", true);
        if (prompt.Contains(ScriptedLanguageModel.SensitiveApprovalMarker, StringComparison.Ordinal) && Offers(ToolCatalog.DemoSensitiveAction))
        {
            var action = results.LastOrDefault(m => m.Name == ToolCatalog.DemoSensitiveAction);
            return action is null ? Call(ToolCatalog.DemoSensitiveAction, new { label = "Synthetic sensitive approval" })
                : Complete("ActionCompleted", "Sensitive action completed after approval.", false);
        }
        return Complete("NoAction", "No meaningful change requires action.", false);
    }
    private static IReadOnlyList<ModelGenerationEvent> Complete(string outcome, string summary, bool attentionRequired) =>
        Call(ToolCatalog.WorkComplete, new { outcome, summary, attentionRequired });
    private static IReadOnlyList<ModelGenerationEvent> Call(string name, object args) =>
        [new ModelToolCallEvent(new("automation-" + name, name, JsonSerializer.Serialize(args, new JsonSerializerOptions(JsonSerializerDefaults.Web)))), new ModelCompleted(ModelStopReason.ToolCalls)];
}
