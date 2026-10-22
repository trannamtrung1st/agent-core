using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tools;

public sealed record TenantAutomationMutationArguments(string Tool, string Json, Guid? AutomationId, long ExpectedRevision);
public sealed record TenantAutomationMutationIntent(string Tool, string ArgumentsJson)
{
    public bool Matches(PreparedTenantOperation operation) => operation.AutomationMutation is { } value && value.ToolName == Tool && value.ArgumentsJson == ArgumentsJson;
}
public static class TenantAutomationMutationContract
{
    public static bool IsTool(string tool) => tool is ToolCatalog.AutomationCreate or ToolCatalog.AutomationUpdate or ToolCatalog.AutomationEnable or ToolCatalog.AutomationDisable or ToolCatalog.AutomationDelete or ToolCatalog.AutomationRun;
    public static bool Allows(AgentDefinition definition, string tool) => IsTool(tool) && RolePermissions.AllowsTool(definition, tool)
        && (tool is ToolCatalog.AutomationDisable or ToolCatalog.AutomationDelete or ToolCatalog.AutomationRun || definition.TriggerPolicy?.AllowUserScheduling == true);
    public static IReadOnlyList<string> Allowed(AgentDefinition definition, AgentContext? context) => TenantTaskExecutionContract.Allows(context)
        ? new[] { ToolCatalog.AutomationCreate, ToolCatalog.AutomationUpdate, ToolCatalog.AutomationEnable, ToolCatalog.AutomationDisable, ToolCatalog.AutomationDelete, ToolCatalog.AutomationRun }
            .Where(t => Allows(definition, t) && (t != ToolCatalog.AutomationRun || context?.ExecutionContext?.OriginalInitiatorPrincipalId is not null
                && !TenantTaskExecutionContract.IsIndependent(context))).ToArray() : [];
    public static TenantAutomationMutationArguments Parse(string tool, string json)
    {
        if (!IsTool(tool) || Encoding.UTF8.GetByteCount(json) > 16384) throw new ArgumentException("Use a bounded exact Automation mutation.");
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        using var schema = JsonDocument.Parse(Arguments(tool).ToJsonString()); var fields = schema.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => !fields.Contains(p.Name))) throw new ArgumentException("Use only the offered Automation arguments; Core binds owners and destinations.");
        Unique(root);
        ValidateShape(root, schema.RootElement.GetProperty("properties"));
        Guid? id = null; long revision = 0;
        if (tool != ToolCatalog.AutomationCreate)
        {
            if (!root.TryGetProperty("automationId", out var selected) || selected.ValueKind != JsonValueKind.String || !Guid.TryParseExact(selected.GetString(), "D", out var parsed) || parsed == Guid.Empty
                || !root.TryGetProperty("expectedRevision", out var expected) || expected.ValueKind != JsonValueKind.Number || !expected.TryGetInt64(out revision) || revision < 1)
                throw new ArgumentException("Inspect one exact owned Automation and use its saved revision.");
            id = parsed;
        }
        else if (!root.TryGetProperty("executionTarget", out _)) throw new ArgumentException("Select an explicit execution destination.");
        foreach (var name in new[] { "enabled", "reportBack", "requiresTools", "requiresVision" })
            if (root.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("Use Boolean Automation choices.");
        if (root.TryGetProperty("executionTarget", out var target) && (target.ValueKind != JsonValueKind.String || target.GetString() is not ("currentSession" or "backgroundSession"))) throw new ArgumentException("Select the current conversation or independent Background work.");
        var kinds = new[] { "triggers", "coreEventKey", "eventId" }.Count(k => root.TryGetProperty(k, out _));
        var timing = new[] { "kind", "relativeDelaySeconds", "relativeDayOffset", "localDate", "localTime", "atUtc", "timeZone", "intervalSeconds", "interval", "weekdays", "startDate", "endDate", "endAtUtc", "maxOccurrences" }.Any(k => root.TryGetProperty(k, out _));
        if (kinds > 1 || kinds > 0 && timing) throw new ArgumentException("Choose one Schedule or typed Event collection.");
        return new(tool, ToolActionHash.Normalize(root), id, revision);
    }
    private static void ValidateShape(JsonElement root, JsonElement properties)
    {
        foreach (var field in root.EnumerateObject())
        {
            var value = field.Value; var shape = properties.GetProperty(field.Name);
            var nullable = field.Name is "modelKey" or "reasoningEffort" or "filterExpression" or "endDate" or "startDate" or "endAtUtc" or "maxOccurrences";
            if (value.ValueKind == JsonValueKind.Null && nullable) continue;
            var type = shape.TryGetProperty("type", out var expected) && expected.ValueKind == JsonValueKind.String ? expected.GetString() : null;
            if (type == "string" && value.ValueKind != JsonValueKind.String || type == "integer" && (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _))
                || type == "boolean" && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || type == "array" && value.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("Use typed Automation arguments.");
            if (shape.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(c => c.GetRawText() == value.GetRawText())) throw new ArgumentException("Use an offered Automation choice.");
        }
        static void Fields(JsonElement value, params string[] names)
        {
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => !names.Contains(p.Name, StringComparer.Ordinal))) throw new ArgumentException("Use only the typed Automation fields.");
        }
        if (!root.TryGetProperty("triggers", out var triggers)) return;
        if (triggers.GetArrayLength() is < 1 or > 32) throw new ArgumentException("Use a bounded trigger collection.");
        foreach (var trigger in triggers.EnumerateArray())
        {
            Fields(trigger, "triggerId", "revision", "enabled", "kind", "source", "schedule", "filterExpression", "dispatch");
            if (trigger.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("Use Boolean subscription choices.");
            if (!trigger.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() is not ("schedule" or "event")) throw new ArgumentException("Use one Schedule or typed Event.");
            if (trigger.TryGetProperty("source", out var source)) Fields(source, "kind", "key", "eventId");
            if (trigger.TryGetProperty("dispatch", out var dispatch)) Fields(dispatch, "mode", "windowSeconds");
            if (trigger.TryGetProperty("schedule", out var schedule)) Fields(schedule, "kind", "timeZone", "atUtc", "interval", "anchorAtUtc", "endAtUtc", "localTime", "weekdays", "startDate", "endDate", "maxOccurrences");
        }
    }
    private static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in value.EnumerateObject()) { if (!names.Add(field.Name)) throw new ArgumentException("Duplicate Automation fields are not permitted."); Unique(field.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
    }
    public static JsonNode Arguments(string tool)
    {
        var value = JsonNode.Parse(ToolRegistry.Get(tool).ModelDefinition.ParametersJson)!;
        value["additionalProperties"] = false;
        if (tool is ToolCatalog.AutomationCreate or ToolCatalog.AutomationUpdate)
            value["properties"]!["presetVersion"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 };
        return value;
    }
    public static ModelToolDefinition ForModel(ModelToolDefinition tool) => tool with { ParametersJson = Arguments(tool.Name).ToJsonString(), Description = tool.Name == ToolCatalog.AutomationRun
        ? "Admit one exact manual occurrence of this Agent's inspected active Automation on the actual current human request. Core binds requester, current configuration, model and destination. Independent Automation/read/creation rights, overlap checks and exact review remain enforced. Request mutate_automation with the unchanged tool and arguments when review is required. Admission is not execution completion and grants no recurring or future tool authority. Never repeat a completed or uncertain admission."
        :
        "Propose an exact Automation change for this Agent under current admitted task intent, Definition capability and IAM. Inspect the selected Automation first and preserve untouched configuration. Core derives identity, resolved timing and destinations. Disabled drafts confer no execution authority. Missing raw reads cannot be granted. Required oversight withholds the write: request mutate_automation with this exact tool and arguments. A completed mutation is a factual one-time result; never repeat an uncertain operation or claim recurring execution authority." };
    public static JsonNode Schema(IReadOnlyList<string> tools) => new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
        ["properties"] = new JsonObject { ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("mutate_automation") },
            ["automation"] = new JsonObject { ["oneOf"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["tool"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(t) }, ["arguments"] = Arguments(t) }, ["required"] = new JsonArray("tool", "arguments") }).ToArray()) } }, ["required"] = new JsonArray("operation", "automation") };
    public static TenantAutomationMutationIntent Intent(JsonElement root)
    {
        TenantApprovalToolContract.Require(root, ["operation", "automation"]); var value = root.GetProperty("automation"); TenantApprovalToolContract.Require(value, ["tool", "arguments"]);
        var args = Parse(value.GetProperty("tool").GetString()!, value.GetProperty("arguments").GetRawText()); return new(args.Tool, args.Json);
    }
}
