using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
namespace AgentCore.Infrastructure.Providers.Synthetic;
internal static class InstanceSkillScript
{
    public static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var user = request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text ?? "";
        if (user is not ("Learn this accounting Skill." or "Use my accounting Skill." or "Show my pinned Skills.")) return null;
        var prompt = string.Join('\n', request.Messages.Where(m => m.Role == ModelRole.System).Select(m => m.Text));
        var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        bool Offered(string name) => request.Tools?.Any(t => t.Name == name) == true;
        if (user == "Show my pinned Skills.") return Answer(prompt.Contains("Available Skills pinned", StringComparison.Ordinal)
            ? string.Join('\n', prompt.Split('\n').Where(l => l.StartsWith("key:", StringComparison.Ordinal))) : "No enabled Skills are pinned.");
        if (user == "Learn this accounting Skill.")
        {
            if (results.LastOrDefault(m => m.Name == "skills.create") is { } created)
                return Answer(created.Text.Contains("\"error\"", StringComparison.Ordinal) ? "Skill creation was rejected." : "Saved the accounting Skill. It applies to the next execution.");
            if (!Offered("skills.create")) return Answer("Skill management is not authorized. Nothing was saved.");
            return Call("skills.create", new { name = "Accounting", description = "Review an accounting entry", procedure = "ACCOUNTING_PROCEDURE: inspect evidence, check totals, and explain the result.", projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "workspace.read" } });
        }
        if (results.LastOrDefault(m => m.Name == ToolCatalog.SkillsLoad) is { })
            return Answer(prompt.Contains("ACCOUNTING_PROCEDURE", StringComparison.Ordinal) ? "Accounting procedure loaded from the pinned Instance Skill." : "The Skill did not load.");
        var line = prompt.Split('\n').FirstOrDefault(l => l.StartsWith("key: instance:", StringComparison.Ordinal) && l.Contains("name: Accounting;", StringComparison.Ordinal));
        if (line is null || !Offered(ToolCatalog.SkillsLoad)) return Answer("Accounting is not present in this execution's pinned catalog.");
        var key = line[5..line.IndexOf(';')];
        return Call(ToolCatalog.SkillsLoad, new { ids = new[] { key } });
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(string name, object arguments) => [new ModelToolCallEvent(new("instance-" + name, name, JsonSerializer.Serialize(arguments))), new ModelCompleted(ModelStopReason.ToolCalls)];
    private static IReadOnlyList<ModelGenerationEvent> Answer(string text) => [new ModelSemanticResponseReady(new(text, new(ModelSpeechMode.Same, null), [])), new ModelCompleted(ModelStopReason.Completed)];
}
