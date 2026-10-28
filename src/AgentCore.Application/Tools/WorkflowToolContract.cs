using System.Text.Json;

namespace AgentCore.Application.Tools;

public static class WorkflowToolContract
{
    public static IReadOnlyList<string> Names { get; } = ["workflows.list", "workflows.inspect", "workflows.start", "workflows.status"];
    public static bool IsTool(string name) => Names.Contains(name, StringComparer.Ordinal);
    public static string Permission(string name) => name == "workflows.start" ? "workflows.start" : name == "workflows.status" ? "workflows.monitor" : "workflows.read";
    public static IEnumerable<ToolDescriptor> Descriptors() => Names.Select(name => new ToolDescriptor(new(name, name switch {
        "workflows.list" => "Discover currently authorized published formal workflows eligible for this actual Agent. Discovery never starts work.",
        "workflows.inspect" => "Inspect an exact published workflow version, typed input schema or mandatory step outline. This grants no execution or tool authority.",
        "workflows.start" => "Start one exact published formal workflow with bounded typed inputs and an immutable request key. Core derives this real claimed Run and reserves its source budget. The model cannot choose the next step, claim approval or complete a workflow.",
        _ => "Read saved process status, required evidence and independently authorized Task/Run/Evaluation links. A successful Task is not verified process completion. Human decisions remain human-only." }, Schema(name)),
        name == "workflows.start" ? ToolEffect.Write : ToolEffect.ReadOnly, ToolOfferRule.RoleAllowlist, ToolResourceScope.Session,
        name == "workflows.start" ? ToolReplaySafety.IntegrationIdempotent : ToolReplaySafety.ReplaySafe));
    private static string Schema(string name)
    {
        var properties = new Dictionary<string, object>(); var required = new List<string>();
        void Field(string key, object schema, bool needed = false) { properties[key] = schema; if (needed) required.Add(key); }
        object Id() => new { type = "string", format = "uuid" };
        if (name == "workflows.status") Field("executionId", Id(), true);
        else if (name != "workflows.list") { Field("workflowId", Id(), true); Field("version", new { type = "integer", minimum = 1 }, true); }
        if (name is "workflows.list" or "workflows.inspect")
        { Field("page", new { type = "integer", minimum = 0, maximum = 100000 }); Field("pageSize", new { type = "integer", minimum = 1, maximum = 20 }); }
        if (name == "workflows.inspect") Field("section", new { type = "string", @enum = new[] { "overview", "steps", "inputs" } });
        if (name == "workflows.start")
        { Field("inputs", new { type = "object", maxProperties = 16 }, true); Field("requestKey", new { type = "string", minLength = 1, maxLength = 128 }, true); }
        return JsonSerializer.Serialize(new { type = "object", additionalProperties = false, properties, required });
    }
}
