using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
namespace AgentCore.Application.Tools;

public static class InstanceSkillTools
{
    public static bool IsManagement(string name) => name is "skills.list" or "skills.inspect" or "skills.create" or "skills.update" or "skills.set_enabled" or "skills.delete" or "skills.customize";
    public static IEnumerable<ToolDescriptor> Descriptors()
    {
        foreach (var op in new[] { "list", "inspect", "create", "update", "set_enabled", "delete", "customize" })
        {
            var properties = new JsonObject(); var required = new JsonArray();
            void Add(string name, JsonObject field) { properties[name] = field; required.Add(name); }
            if (op is not ("list" or "create")) Add("key", new() { ["type"] = "string", ["maxLength"] = 75 });
            if (op is "update" or "set_enabled" or "delete" or "customize") Add("expectedRevision", new() { ["type"] = "integer", ["minimum"] = 1 });
            if (op == "create") properties["id"] = new JsonObject { ["type"] = "string", ["maxLength"] = 64, ["description"] = "Optional ID unique within this instance. Empty generates from name." };
            if (op is "create" or "update")
            {
                foreach (var (field, max) in new[] { ("name", 80), ("description", 240), ("procedure", 4000) })
                    Add(field, new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = max });
                Add("projection", new() { ["type"] = "string", ["enum"] = new JsonArray("Always", "OnDemand") });
                Add("requiredCapabilities", new() { ["type"] = "array", ["maxItems"] = 8, ["uniqueItems"] = true, ["items"] = new JsonObject { ["type"] = "string", ["maxLength"] = 64 } });
            }
            if (op is "create" or "update" or "set_enabled") Add("enabled", new() { ["type"] = "boolean" });
            var schema = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties, ["required"] = required };
            yield return new(new("skills." + op,
                $"{op} Skills of the current trusted Agent Instance. Definition content is read-only. Customize returns instanceSkill (the independent copy) and definitionSkill (the disabled source key, enabled state and revision). Writes apply to future executions; use current revisions from inspect. Required capabilities never grant authority.", schema.ToJsonString()),
                op is "list" or "inspect" ? ToolEffect.ReadOnly : ToolEffect.Write, ToolOfferRule.RoleAllowlist,
                ToolResourceScope.Owner, op is "list" or "inspect" ? ToolReplaySafety.ReplaySafe : ToolReplaySafety.NonReplayable);
        }
    }
    public static InstanceSkillInput ReadInput(JsonElement a)
    {
        if (!a.TryGetProperty("projection", out var projection) || projection.ValueKind != JsonValueKind.String
            || projection.GetString() is not ("Always" or "OnDemand")) throw AgentCoreErrors.Validation("projection must be Always or OnDemand.");
        return new(a.GetProperty("name").GetString()!, a.GetProperty("description").GetString()!, a.GetProperty("procedure").GetString()!,
            projection.GetString() == "Always" ? SkillProjection.Always : SkillProjection.OnDemand, a.GetProperty("enabled").GetBoolean(), a.GetProperty("requiredCapabilities").EnumerateArray().Select(c => c.GetString()!).ToArray(), a.TryGetProperty("id", out var id) ? id.GetString() : null);
    }
}
public sealed partial class SessionToolExecutor
{
    private async Task<ToolExecutionResult> ExecuteSkillManagementAsync(AgentDefinition definition, ModelToolCall call, JsonElement args,
        ToolExecutionAdmission? admission, int budget, CancellationToken ct)
    {
        if (instanceSkills is null || admission?.AgentInstanceId is not Guid id || !admission.SupportsTools)
            return TextResult(Error("forbidden", "Trusted Agent Instance context is required."));
        try
        {
            using var schema = JsonDocument.Parse(ToolRegistry.Get(call.Name).ModelDefinition.ParametersJson);
            var allowed = schema.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
            if (args.EnumerateObject().Any(p => !allowed.Contains(p.Name))) throw AgentCoreErrors.Validation("Unknown Skill argument.");
            foreach (var r in schema.RootElement.GetProperty("required").EnumerateArray())
                if (!args.TryGetProperty(r.GetString()!, out _)) throw AgentCoreErrors.Validation($"{r.GetString()} is required.");
            var op = call.Name[7..];
            object result;
            if (op == "list") result = (await instanceSkills.ListAsync(id, definition, ct)).Select(s => new { s.Key, s.Origin, s.Name, s.Description, s.Projection, s.Enabled, s.Revision, s.RequiredCapabilities, s.MissingCapabilities, s.SourceDefinitionId, s.SourceDefinitionVersion, s.SourceDefinitionSkillId }).ToArray();
            else if (op == "inspect") result = await instanceSkills.InspectAsync(id, args.GetProperty("key").GetString()!, definition, ct);
            else if (op == "customize") result = await instanceSkills.CustomizeAsync(id, args.GetProperty("key").GetString()!,
                args.GetProperty("expectedRevision").GetInt64(), SkillAuthor.Agent, definition, ct);
            else result = await instanceSkills.WriteAsync(id, op, args.TryGetProperty("key", out var key) ? key.GetString() : null,
                args.TryGetProperty("expectedRevision", out var rev) ? rev.GetInt64() : null,
                op is "create" or "update" ? InstanceSkillTools.ReadInput(args) : null,
                args.TryGetProperty("enabled", out var enabled) ? enabled.GetBoolean() : null, SkillAuthor.Agent, definition, ct);
            return FitResult(budget, JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        }
        catch (AgentCoreException e) { return TextResult(Error(e.Code, e.Message)); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { return TextResult(Error("invalid", "Skill arguments do not match the schema.")); }
    }
}
