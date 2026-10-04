using System.Text.Json;
using System.Text.Json.Nodes;
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
            yield return new(new(name,
                $"Author {operation.Kind} for future conversations only. {OperationHelp(operation.Kind)} Inspect for expectedVersion/policyRevision. Retain enduring role knowledge or repeatable procedures, never credentials. Record expected/observed assessment and honest limitations. Assisted edits, instructions and every tool change require exact approval. Core validates and adopts an immutable version; the current Session pin is unchanged.",
                AuthoringSchema(operation.Kind)), ToolEffect.Write, ToolOfferRule.HarnessAuthority, ToolResourceScope.Session, ToolReplaySafety.NonReplayable);
        }
    }

    public const string SkillPayloadHelp = "Send a nested skill object with id, name, description, procedure, activationKeywords, requiredCapabilities and resourcePaths. "
        + "skill.id must match ^[a-z][a-z0-9._]{0,63}$ (for example kubernetes.operations; hyphens are invalid). "
        + "name is 1..80 characters, description 1..240, procedure 1..4000; use procedural text without fenced code or script blocks. "
        + "activationKeywords is an array of up to 8 unique, trimmed, nonblank strings of at most 64 characters; use [] when none. "
        + "requiredCapabilities is an array of up to 8 unique capability ids, limited to chat.respond or already selected tools; requirements do not grant tools. "
        + "resourcePaths is an array of up to 4 unique existing definition resource paths (max 240 characters, relative forward-slash paths without dot segments); use [] when none. Workspace files are not definition resources. "
        + "The resulting definition may have at most 16 Skills and 12000 total procedure characters.";

    private static string OperationHelp(string kind) => kind switch
    {
        "skill.upsert" => SkillPayloadHelp + " Read source material through ordinary tools first. Adapt the example returned by harness.inspect; never guess an activation object.",
        "skill.remove" => "id is the exact Skill id from harness.inspect.",
        "knowledge.upsert" => "id is a simple alphanumeric name (hyphens/underscores allowed). content is the retained text. source must name material actually read in this turn (for example workspace:playbook.md), or conversation:user for current owner-provided material.",
        "knowledge.remove" => "id is the exact knowledge identity from harness.inspect.",
        "instructions.update" => "content replaces the complete operating instructions; preserve other intended instructions from harness.inspect.",
        "tool.select" => "id must be an eligible configured tool from harness.inspect; enabled adds or removes it. attachments.read uses harness.tool.configure instead.",
        "tool.configure" => "Only id=attachments.read is supported; allowUnreadUnsupportedTypes sets its readability policy.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string AuthoringSchema(string kind)
    {
        static JsonObject Text(int max, string description, string? pattern = null)
        {
            var field = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = max, ["description"] = description };
            if (pattern is not null) field["pattern"] = pattern;
            return field;
        }
        static JsonObject List(int max, JsonObject item) => new()
        {
            ["type"] = "array", ["maxItems"] = max, ["uniqueItems"] = true, ["items"] = item
        };
        const string identifier = "^[a-z][a-z0-9._]{0,63}$";
        var fields = new JsonObject
        {
            ["expectedVersion"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = "Use the latest harness.inspect expectedVersion." },
            ["policyRevision"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = "Use the latest harness.inspect policyRevision." },
            ["expected"] = Text(2000, "Intended reusable behavior or validation outcome."),
            ["observed"] = Text(2000, "What was actually read or checked; do not claim unperformed checks."),
            ["limitation"] = Text(2000, "Optional honest verification limits.")
        };
        var required = new JsonArray("expectedVersion", "policyRevision", "expected", "observed");
        void Add(string key, JsonObject field) { fields[key] = field; required.Add(key); }
        switch (kind)
        {
            case "skill.upsert":
                Add("skill", new JsonObject
                {
                    ["type"] = "object", ["additionalProperties"] = false,
                    ["required"] = new JsonArray("id", "name", "description", "procedure", "activationKeywords", "requiredCapabilities", "resourcePaths"),
                    ["properties"] = new JsonObject
                    {
                        ["id"] = Text(64, "Stable lowercase id, e.g. kubernetes.operations. No hyphens.", identifier),
                        ["name"] = Text(80, "Human-readable name."),
                        ["description"] = Text(240, "When and why this Skill is useful."),
                        ["procedure"] = Text(4000, "Reusable procedural text; no fenced code or script blocks."),
                        ["activationKeywords"] = List(8, Text(64, "Unique trimmed activation phrase; [] is allowed.")),
                        ["requiredCapabilities"] = List(8, Text(64, "chat.respond or a tool already selected in harness.inspect; [] is allowed.", identifier)),
                        ["resourcePaths"] = List(4, Text(240, "Existing definition-relative resource path, never a workspace path; [] is allowed."))
                    }
                });
                break;
            case "skill.remove": Add("id", Text(64, "Existing Skill id.", identifier)); break;
            case "knowledge.upsert":
            case "knowledge.remove":
                Add("id", Text(80, "Knowledge identity.", "^[a-zA-Z0-9_-]+$"));
                if (kind == "knowledge.upsert")
                {
                    Add("content", Text(32000, "Enduring knowledge to retain."));
                    Add("source", Text(2048, "Source actually read this turn, or conversation:user."));
                }
                break;
            case "instructions.update": Add("content", Text(32000, "Complete replacement operating instructions.")); break;
            case "tool.select":
                Add("id", Text(64, "Eligible configured tool, excluding attachments.read.", identifier));
                Add("enabled", new JsonObject { ["type"] = "boolean" });
                break;
            case "tool.configure":
                Add("id", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("attachments.read") });
                Add("allowUnreadUnsupportedTypes", new JsonObject { ["type"] = "boolean" });
                break;
        }
        return new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = fields, ["required"] = required }.ToJsonString();
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
