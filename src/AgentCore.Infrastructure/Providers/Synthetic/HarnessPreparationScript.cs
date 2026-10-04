using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Providers.Synthetic;

/// <summary>Deterministic fixture exercising real Authoring services; ordinary prompts never select it.</summary>
internal static class HarnessPreparationScript
{
    public static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        if (request.Messages.Any(m => m.Role == ModelRole.System && m.Text.StartsWith("HarnessSkillSample v1", StringComparison.Ordinal)))
            return [new ModelTextDelta("Inspect the permitted order policy, classify this fictional refund request as needing owner attention, and stop before issuing a refund or contacting the customer. Production refunds and customer messages require external owner evidence."), new ModelCompleted(ModelStopReason.Completed)];
        if (!request.Messages.Any(m => m.Role == ModelRole.System && m.Text.StartsWith(HarnessPreparationExecution.Directive, StringComparison.Ordinal))) return null;
        var inspect = request.Messages.LastOrDefault(m => m.Name == "harness.inspect");
        if (inspect is null) return Call("harness.inspect", new { });
        var snapshotMessage = request.Messages.LastOrDefault(m => m.Text.Contains("\"draftRevision\"", StringComparison.Ordinal)
            && (m.Role == ModelRole.System || m.Name is "harness.inspect" or "harness.author" or "harness.verify" or "harness.test" or "harness.evidence" or "harness.skill.test"));
        using var snapshot = JsonDocument.Parse(snapshotMessage!.Text);
        var root = snapshot.RootElement;
        var revision = root.GetProperty("draftRevision").GetInt64();
        var scopes = root.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()).ToArray();
        var source = root.GetProperty("permittedSources").EnumerateArray().Select(s => s.GetString()).FirstOrDefault();
        var knows = root.GetProperty("knowledge").EnumerateArray().Any(k => k.GetProperty("identity").GetString() == "preparation-reference");
        if (scopes.Contains("KnowledgeResources") && source is not null && !knows)
        {
            var material = request.Messages.LastOrDefault(m => m.Name == "harness.source.read");
            if (material is null) return Call("harness.source.read", new { source });
            using var data = JsonDocument.Parse(material.Text);
            if (data.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return Call("harness.author", new { kind = "knowledge.upsert", draftRevision = revision, id = "preparation-reference", source, content = content.GetString() });
        }
        var skills = root.GetProperty("skills").EnumerateArray();
        if (scopes.Contains("Skills") && !skills.Any(s => s.GetProperty("id").GetString() == "operations.review"))
            return Call("harness.author", new
            {
                kind = "skill.upsert", draftRevision = revision,
                skill = new { id = "operations.review", name = "Operations review", description = "Inspect evidence and classify whether owner attention is required.",
                    procedure = "Read permitted policy material. Inspect the request. Cite the applicable policy and classify owner attention. Stop before refunds, customer messages, or destructive production actions and request owner evidence.",
                    activationKeywords = new[] { "review", "order" }, requiredCapabilities = new[] { "chat.respond" },
                    resourcePaths = knows ? new[] { "knowledge/preparation-reference" } : Array.Empty<string>() }
            });
        if (scopes.Contains("ToolSelection"))
        {
            var tool = root.GetProperty("eligibleTools").EnumerateArray().Select(t => t.GetString()).FirstOrDefault();
            var approvals = root.GetProperty("approvals").EnumerateArray();
            if (tool is not null && !approvals.Any(a => a.GetProperty("kind").GetString() == "tool.select"))
                return Call("harness.author", new { kind = "tool.select", draftRevision = revision, id = tool, enabled = true });
        }
        var evidence = root.GetProperty("evidence").EnumerateArray();
        if (knows && !evidence.Any(e => e.GetProperty("check").GetString() == "Candidate knowledge readback" && e.GetProperty("draftRevision").GetInt64() == revision))
        {
            var material = request.Messages.LastOrDefault(m => m.Name == "harness.source.read");
            if (material is null) return Call("harness.source.read", new { source });
            using var data = JsonDocument.Parse(material.Text);
            if (data.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                return Call("harness.test", new { identity = "preparation-reference", expectedText = content.GetString()![..Math.Min(content.GetString()!.Length, 80)] });
        }
        if (scopes.Contains("Skills") && root.GetProperty("skills").EnumerateArray().Any(s => s.GetProperty("id").GetString() == "operations.review")
            && !evidence.Any(e => e.GetProperty("check").GetString() == "Safe representative Skill sample" && e.GetProperty("draftRevision").GetInt64() == revision))
            return Call("harness.skill.test", new { skillId = "operations.review", sample = "Fictional sample: a customer asks for a refund before the order evidence has been reviewed. What is the safe next step?" });
        if (!evidence.Any(e => e.GetProperty("actor").GetString() == "Agent" && e.GetProperty("draftRevision").GetInt64() == revision))
            return Call("harness.evidence", new { draftRevision = revision, check = "Safe representative operations review", status = 1,
                expected = "Candidate knowledge and procedure can be inspected without production side effects.",
                observed = knows ? "Permitted material was read, attached with provenance, and the procedure references that candidate resource." : "Procedure inspected; no knowledge source was available.",
                limitation = "Production refunds and customer messages require external owner evidence; no such side effect was performed." });
        if (request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool)?.Name != "harness.verify") return Call("harness.verify", new { });
        return [new ModelTextDelta("Candidate preparation is ready for owner review with external limitations."), new ModelCompleted(ModelStopReason.Completed)];
    }

    private static IReadOnlyList<ModelGenerationEvent> Call(string name, object args) =>
        [new ModelToolCallEvent(new("preparation-" + name, name, JsonSerializer.Serialize(args, new JsonSerializerOptions(JsonSerializerDefaults.Web)))),
            new ModelCompleted(ModelStopReason.ToolCalls)];
}
