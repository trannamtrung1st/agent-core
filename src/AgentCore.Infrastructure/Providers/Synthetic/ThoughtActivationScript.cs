using System.Text.Json;
using AgentCore.Application.Experience;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

internal static class ThoughtActivationScript
{
    public static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        if (!request.Messages.Any(m => m.Role == ModelRole.System && m.Text.StartsWith("Bounded thought activation.", StringComparison.Ordinal))) return null;
        var prompt = request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text ?? "";
        bool Offers(string name) => request.Tools?.Any(t => t.Name == name) == true;
        var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        if (prompt.Contains("synthetic-thought-improve", StringComparison.Ordinal))
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
        if (prompt.Contains("synthetic-thought-attention", StringComparison.Ordinal))
            return Complete("AttentionRequested", "An unresolved checkpoint needs the owner's attention.", true);
        return Complete("NoAction", "No meaningful change requires action.", false);
    }
    private static IReadOnlyList<ModelGenerationEvent> Complete(string outcome, string summary, bool attentionRequired) =>
        Call(ToolCatalog.WorkComplete, new { outcome, summary, attentionRequired });
    private static IReadOnlyList<ModelGenerationEvent> Call(string name, object args) =>
        [new ModelToolCallEvent(new("thought-" + name, name, JsonSerializer.Serialize(args))), new ModelCompleted(ModelStopReason.ToolCalls)];
}
