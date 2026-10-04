using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

/// <summary>Contextual trusted-local owner authority; never sourced from Definition text.</summary>
public sealed record HarnessChatContext(HarnessManagementPolicy Policy, long PolicyRevision, int ActiveVersion);
public sealed record HarnessSourceReceipt(string Source, string ToolName);

public static class HarnessChatTools
{
    public const string Inspect = "harness.inspect";
    public static readonly IReadOnlyDictionary<string, (string Kind, HarnessManagementScope Scope)> Operations =
        new Dictionary<string, (string, HarnessManagementScope)>(StringComparer.Ordinal)
        {
            ["harness.knowledge.upsert"] = ("knowledge.upsert", HarnessManagementScope.KnowledgeResources),
            ["harness.knowledge.remove"] = ("knowledge.remove", HarnessManagementScope.KnowledgeResources),
            ["harness.skill.upsert"] = ("skill.upsert", HarnessManagementScope.Skills),
            ["harness.skill.remove"] = ("skill.remove", HarnessManagementScope.Skills),
            ["harness.instructions.update"] = ("instructions.update", HarnessManagementScope.Instructions),
            ["harness.tool.select"] = ("tool.select", HarnessManagementScope.ToolSelection),
            ["harness.tool.configure"] = ("tool.configure", HarnessManagementScope.ToolSelection)
        };
    public static bool IsHarness(string name) => name == Inspect || Operations.ContainsKey(name);
    public static bool Allows(string name, HarnessChatContext? context) => context is not null
        && context.Policy.Mode != HarnessManagementMode.Disabled && !context.Policy.Frozen
        && (name == Inspect || Operations.TryGetValue(name, out var op) && context.Policy.Allows(op.Scope));
    public static bool NeedsApproval(string name, HarnessChatContext context) => name != Inspect
        && (context.Policy.Mode == HarnessManagementMode.Assisted || Operations[name].Scope is HarnessManagementScope.Instructions or HarnessManagementScope.ToolSelection);

    public static IEnumerable<ToolDescriptor> Descriptors()
    {
        yield return new(new(Inspect, "Inspect current instance authority and active version before a durable harness change. No provider configuration or credentials are exposed.", """{"type":"object","properties":{},"additionalProperties":false}"""), ToolEffect.ReadOnly, ToolOfferRule.HarnessAuthority);
        foreach (var (name, operation) in Operations)
        {
            var properties = """{"expectedVersion":{"type":"integer"},"policyRevision":{"type":"integer"},"id":{"type":"string"},"content":{"type":"string"},"source":{"type":"string"},"enabled":{"type":"boolean"},"allowUnreadUnsupportedTypes":{"type":"boolean"},"skill":{"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"description":{"type":"string"},"procedure":{"type":"string"},"activationKeywords":{"type":"array","items":{"type":"string"}},"requiredCapabilities":{"type":"array","items":{"type":"string"}},"resourcePaths":{"type":"array","items":{"type":"string"}}},"required":["id","name","description","procedure","activationKeywords","requiredCapabilities","resourcePaths"]},"expected":{"type":"string"},"observed":{"type":"string"},"limitation":{"type":"string"}}""";
            yield return new(new(name,
                $"Author {operation.Kind} for future conversations only. Inspect for expectedVersion/policyRevision. Retain only enduring role knowledge or repeatable procedures, not temporary facts or credentials. Source must be actually read by an ordinary tool in this turn, or conversation:user for current owner-provided material. Record expected/observed assessment and honest limitations. Assisted edits, instructions and every tool change require exact approval. Core validates and adopts an immutable version; the current Session pin is unchanged.",
                "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":" + properties + ",\"required\":[\"expectedVersion\",\"policyRevision\",\"expected\",\"observed\"]}"), ToolEffect.Write, ToolOfferRule.HarnessAuthority, ToolResourceScope.Session, ToolReplaySafety.NonReplayable);
        }
    }

    // Receipts come from successful admitted tools in this execution, not from model claims.
    public static IReadOnlyList<HarnessSourceReceipt> Sources(ModelToolCall call, string result)
    {
        try
        {
            using var output = JsonDocument.Parse(result);
            if (output.RootElement.ValueKind != JsonValueKind.Object || output.RootElement.TryGetProperty("error", out _)) return [];
            using var args = JsonDocument.Parse(call.ArgumentsJson);
            if (args.RootElement.ValueKind != JsonValueKind.Object) return [];
            string? Read(string key) => args.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var hasContent = new[] { "content", "text", "visibleText", "body" }.Any(k => output.RootElement.TryGetProperty(k, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                || output.RootElement.TryGetProperty("contentProvided", out var provided) && provided.ValueKind == JsonValueKind.True;
            if (!hasContent) return [];
            var source = call.Name switch
            {
                ToolCatalog.WebFetch => Read("url"),
                ToolCatalog.WorkspaceRead => Read("path") is { } path ? "workspace:" + path : null,
                ToolCatalog.KnowledgeRetrieve => Read("identity") is { } id ? "knowledge:" + id : null,
                ToolCatalog.AttachmentsRead => Read("attachmentId") is { } id ? "attachment:" + id : null,
                ToolCatalog.BrowserNavigate or ToolCatalog.BrowserObserve or ToolCatalog.BrowserAct => output.RootElement.TryGetProperty("url", out var browserUrl) && browserUrl.ValueKind == JsonValueKind.String ? browserUrl.GetString() : null,
                ToolCatalog.HttpRequest => Read("url"),
                _ => null
            };
            if (source is null) return [];
            var sources = new List<HarnessSourceReceipt> { new(source, call.Name) };
            if (call.Name == ToolCatalog.WebFetch && output.RootElement.TryGetProperty("finalUrl", out var url) && url.ValueKind == JsonValueKind.String && url.GetString() is { } final)
                sources.Add(new(final, call.Name));
            return sources;
        }
        catch (JsonException) { return []; }
    }
}
