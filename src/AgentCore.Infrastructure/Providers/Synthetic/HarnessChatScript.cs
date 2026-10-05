using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

/// <summary>Natural Chat fixtures use the real ordinary tool loop and Authoring owners.</summary>
internal static class HarnessChatScript
{
    public static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var user = request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text ?? "";
        var knowledge = user.StartsWith("Learn this order policy for future conversations", StringComparison.OrdinalIgnoreCase);
        var skill = user.StartsWith("Learn this order-review procedure for future conversations", StringComparison.OrdinalIgnoreCase);
        var tool = user.StartsWith("Propose disabling http.request for future conversations", StringComparison.OrdinalIgnoreCase);
        var instruction = user.StartsWith("Save these operating instructions for future conversations", StringComparison.OrdinalIgnoreCase);
        var sensitive = user.Equals("Try a sensitive HTTP action now.", StringComparison.OrdinalIgnoreCase);
        var recall = user.Equals("What is the learned order policy?", StringComparison.OrdinalIgnoreCase);
        var exercise = user.Equals("Use the learned order-review procedure.", StringComparison.OrdinalIgnoreCase);
        if (!(knowledge || skill || tool || instruction || sensitive || recall || exercise)) return null;
        var lastUser = request.Messages.Select((m, i) => (m, i)).Last(p => p.m.Role == ModelRole.User).i;
        var results = request.Messages.Skip(lastUser + 1).Where(m => m.Role == ModelRole.Tool).ToArray();
        bool Offered(string name) => request.Tools?.Any(t => t.Name == name) == true;
        if (sensitive)
            return results.Any(m => m.Name == ToolCatalog.HttpRequest) ? Answer("The sensitive action was not executed.")
                : Offered(ToolCatalog.HttpRequest) ? Call(ToolCatalog.HttpRequest, new { method = "POST", url = "https://example.test/p97/action", body = "owner approval required" })
                    : Answer("The sensitive HTTP action is unavailable in this Session.");
        if (recall)
        {
            if (results.LastOrDefault(m => m.Name == ToolCatalog.KnowledgeRetrieve) is { } recalled)
                return Answer(recalled.Text.Contains("payment", StringComparison.OrdinalIgnoreCase) ? "The saved policy says to check payment, shipping and fraud notes." : "No saved order policy is available in this Session.");
            return Offered(ToolCatalog.KnowledgeRetrieve) ? Call(ToolCatalog.KnowledgeRetrieve, new { identity = "learned-orders" }) : Answer("Knowledge retrieval is unavailable.");
        }
        if (exercise)
        {
            if (results.Any(m => m.Name == ToolCatalog.SkillsLoad))
                return Answer(request.Messages.Any(m => m.Role == ModelRole.System && m.Text.Contains("Check payment, then shipping, then fraud notes.", StringComparison.Ordinal))
                    ? "Using the learned procedure: check payment, then shipping, then fraud notes. Stop before production actions." : "The learned procedure did not activate.");
            return Offered(ToolCatalog.SkillsLoad) ? Call(ToolCatalog.SkillsLoad, new { ids = new[] { "order-review" } }) : Answer("No learned Skill is available in this Session.");
        }
        var name = knowledge ? "harness.knowledge.upsert" : skill ? "harness.skill.upsert" : tool ? "harness.tool.select" : "harness.instructions.update";
        if (!Offered(name)) return Answer("I can discuss that here, but I cannot save a durable harness change with the current policy or model. Nothing was saved.");
        if (results.LastOrDefault(m => m.Name == name) is { } authored)
        {
            using var result = JsonDocument.Parse(authored.Text);
            return Answer(result.RootElement.TryGetProperty("saved", out var saved) && saved.GetBoolean()
                ? "Saved that for future conversations. This conversation keeps its current version. Core checks passed; production outcomes remain unverified."
                : "Nothing was saved: " + (result.RootElement.TryGetProperty("message", out var message) ? message.GetString() : "The change was rejected or failed; inspect and try a fresh request."));
        }
        var inspection = results.LastOrDefault(m => m.Name == HarnessChatTools.Inspect);
        if (inspection is null) return Call(HarnessChatTools.Inspect, new { });
        using var inspected = JsonDocument.Parse(inspection.Text);
        if (!inspected.RootElement.TryGetProperty("expectedVersion", out var version)) return Answer("Harness inspection failed; nothing was saved.");
        var policyRevision = inspected.RootElement.GetProperty("policyRevision").GetInt64();
        if (knowledge && user.Contains("https://", StringComparison.Ordinal))
        {
            var web = results.LastOrDefault(m => m.Name == ToolCatalog.WebFetch);
            if (web is null) return Offered(ToolCatalog.WebFetch) ? Call(ToolCatalog.WebFetch, new { url = "https://example.test/p97/order-policy" }) : Answer("The source cannot be read with the current web authority. Nothing was saved.");
            using var fetched = JsonDocument.Parse(web.Text);
            if (fetched.RootElement.TryGetProperty("error", out _)) return Answer("The source could not be read. Nothing was saved.");
        }
        var args = new Dictionary<string, object?>
        {
            ["expectedVersion"] = version.GetInt32(), ["policyRevision"] = policyRevision
        };
        if (!skill)
        {
            args["expected"] = "Reusable role knowledge or procedure should persist for future Sessions.";
            args["observed"] = "The current owner supplied reusable material or it was read by the ordinary authorized web tool.";
            args["limitation"] = "Production actions and subjective procedure quality remain external evidence.";
        }
        if (knowledge)
        {
            args["id"] = "learned-orders"; args["content"] = "Check payment, shipping and fraud notes before acting on an order.";
            args["source"] = user.Contains("https://", StringComparison.Ordinal) ? "https://example.test/p97/order-policy" : "conversation:user";
        }
        if (skill) args["skill"] = new { name = "Order review", description = "Review orders in the owner's required sequence.", procedure = "Check payment, then shipping, then fraud notes. Stop before production actions." };
        if (tool) { args["id"] = "http.request"; args["enabled"] = false; }
        if (instruction) args["content"] = "Use concise order-review summaries and stop before production actions.";
        return Call(name, args);
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(string name, object args) =>
        [new ModelToolCallEvent(new("chat-" + name, name, JsonSerializer.Serialize(args))), new ModelCompleted(ModelStopReason.ToolCalls)];
    private static IReadOnlyList<ModelGenerationEvent> Answer(string text) =>
        [new ModelSemanticResponseReady(new(text, new(ModelSpeechMode.Same, null), [])), new ModelCompleted(ModelStopReason.Completed)];
}
