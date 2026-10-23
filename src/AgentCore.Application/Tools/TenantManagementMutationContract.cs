using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tools;

public sealed record TenantManagementMutationArguments(string Tool, string Json, long ExpectedInstanceRevision, JsonElement Value, PreparedHarnessCommand? Harness = null, TenantManagementMutationIntent? Requested = null, PreparedConfigurationRemedy? Remedy = null)
{ public string AuthorityTool => Harness?.ToolName ?? (Remedy is not null ? "configuration.propose" : Requested?.Tool ?? Tool); }
public sealed record TenantManagementMutationIntent(string Tool, string ArgumentsJson)
{
    public bool Matches(PreparedTenantOperation operation) => operation.ManagementMutation is { } change && (change.Harness is { } harness ? harness.ToolName == Tool && harness.ArgumentsJson == ArgumentsJson : change.Remedy is { } remedy ? Tool == "configuration.propose" && remedy.ArgumentsJson == ArgumentsJson : change.ToolName == Tool && change.ArgumentsJson == ArgumentsJson)
        || operation.Preferences is { } preferences && TenantManagementMutationContract.Preference(Tool, ArgumentsJson) is { } patch
            && TenantPreferenceToolContract.Parameters(patch) == preferences && TenantManagementMutationContract.Parse(Tool, ArgumentsJson).ExpectedInstanceRevision == operation.ExpectedInstanceRevision;
}
public static class TenantManagementMutationContract
{
    public static readonly IReadOnlyList<string> Tools = Array.AsReadOnly(new[] { "settings.patch", "management.policy.update", "management.freeze", "automation.preferences.patch", "skills.create", "skills.update", "skills.set_enabled", "skills.reset_enabled", "skills.delete", "skills.customize", "resources.upsert", "resources.copy", "resources.set_enabled", "resources.delete", "harness.knowledge.upsert", "harness.knowledge.remove", "harness.instructions.update", "harness.tool.select", "harness.tool.configure", "configuration.propose", "credentials.binding.propose", "credentials.binding.remove" });
    public static bool IsTool(string name) => Tools.Contains(name);
    public static bool Allows(AgentDefinition definition, string tool) => IsTool(tool) && (HarnessChatTools.Operations.ContainsKey(tool) || RolePermissions.AllowsTool(definition, tool))
        && definition.SelfManagement is { } policy && ManagementTools.Enabled(policy)
        && (ManagementTools.Area(tool) is not { } scope || policy.Allows(scope));
    public static IReadOnlyList<string> Allowed(AgentDefinition definition, AgentContext? context) => context is { ModelSupportsTools: true, AgentInstanceId: not null, OwnedSessionId: not null, ExecutionContext: { } execution, Harness: { } live }
        && execution.SessionId == context.OwnedSessionId && TenantManagementReadContract.Shape(execution, context.OutputContract, context.RunTaskIntent)
        ? Tools.Where(tool => Allows(definition, tool) && ManagementTools.Allows(tool, definition, live)).ToArray() : [];
    public static TenantManagementMutationArguments Parse(string tool, string json, PreparedHarnessCommand? harness = null, string? authorityTool = null, PreparedConfigurationRemedy? remedy = null)
    {
        if (!IsTool(tool) || Encoding.UTF8.GetByteCount(json) > (harness is not null || authorityTool is not null || TenantManagementResourceRules.IsTool(tool) || HarnessChatTools.Operations.ContainsKey(tool) ? 262144 : 24000)) throw new ArgumentException("Use an offered bounded exact management command.");
        using var doc = JsonDocument.Parse(json); if (HarnessChatTools.Operations.ContainsKey(tool))
        {
            ConfigurationTools.Validate(tool, doc.RootElement);
            TenantHarnessCommandRules.ValidateArguments(tool, doc.RootElement);
        }
        else if (TenantManagementSkillRules.IsTool(tool))
        {
            var root = doc.RootElement;
            using var schema = JsonDocument.Parse(ToolRegistry.Get(tool).ModelDefinition.ParametersJson);
            var allowed = schema.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count()
                || root.EnumerateObject().Any(p => !allowed.Contains(p.Name))) throw new ArgumentException("Use exact Skill fields.");
            foreach (var field in schema.RootElement.GetProperty("required").EnumerateArray())
                if (!root.TryGetProperty(field.GetString()!, out _)) throw new ArgumentException("A required Skill field is missing.");
            if (root.TryGetProperty("key", out var key) && (key.ValueKind != JsonValueKind.String || !SkillKeys.IsValid(key.GetString()!))) throw new ArgumentException("Use an origin-qualified Skill key.");
            if (root.GetProperty("expectedInstanceRevision").ValueKind != JsonValueKind.Number || !root.GetProperty("expectedInstanceRevision").TryGetInt64(out var revision) || revision < 1
                || root.TryGetProperty("expectedRevision", out var childRevision) && (childRevision.ValueKind != JsonValueKind.Number || !childRevision.TryGetInt64(out var number) || number < 1)) throw new ArgumentException("Use current positive revisions.");
            if (root.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("Use a boolean choice.");
            if (tool is "skills.create" or "skills.update")
            {
                foreach (var (field, limit) in new[] { ("name", 80), ("description", 240), ("procedure", 4000) })
                    if (root.GetProperty(field).ValueKind != JsonValueKind.String || root.GetProperty(field).GetString() is not { Length: > 0 } text || text.Length > limit)
                        throw new ArgumentException("Use bounded Skill text fields.");
                var caps = root.GetProperty("requiredCapabilities");
                if (caps.ValueKind != JsonValueKind.Array || caps.GetArrayLength() > 8 || caps.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.String || c.GetString() is not { Length: > 0 and <= 64 })
                    || caps.EnumerateArray().Select(c => c.GetString()).Distinct(StringComparer.Ordinal).Count() != caps.GetArrayLength()) throw new ArgumentException("Use distinct bounded capability names.");
                if (root.TryGetProperty("id", out var id) && (id.ValueKind != JsonValueKind.String || id.GetString()!.Length > 64)) throw new ArgumentException("Use a bounded Skill identity.");
                var projection = root.GetProperty("projection");
                if (projection.ValueKind != JsonValueKind.String || projection.GetString() is not ("Always" or "OnDemand")) throw new ArgumentException("Use an offered Skill projection.");
                _ = InstanceSkillTools.ReadInput(root);
            }
        }
        else
        {
            ConfigurationTools.Validate(tool, doc.RootElement);
            if (TenantManagementResourceRules.IsTool(tool))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("key", out var key))
                {
                    var parsed = AgentCore.Application.Admin.InstanceResourceRules.ParseKey(key.GetString()!);
                    if (tool is "resources.upsert" or "resources.delete" && parsed.Origin != "instance"
                        || tool == "resources.copy" && parsed.Origin != "definition") throw new ArgumentException("Use the offered Resource origin.");
                }
                if (tool == "resources.upsert" && root.TryGetProperty("key", out _) != root.TryGetProperty("expectedResourceRevision", out _))
                    throw new ArgumentException("Resource replacement requires its exact current child revision.");
            }
        }
        if (tool == "settings.patch")
        {
            var set = doc.RootElement.GetProperty("set").EnumerateObject().Select(p => p.Name).ToArray(); var clear = doc.RootElement.GetProperty("clear").EnumerateArray().Select(p => p.GetString()!).ToArray();
            if (set.Length + clear.Length == 0 || set.Intersect(clear, StringComparer.Ordinal).Any()) throw new ArgumentException("Use distinct nonempty setting fields.");
        }
        if (tool == "settings.patch" && doc.RootElement.GetProperty("section").GetString() == "sandboxPolicy"
            && doc.RootElement.GetProperty("set").EnumerateObject().Select(p => p.Name).Concat(doc.RootElement.GetProperty("clear").EnumerateArray().Select(p => p.GetString()!)).Any(field => field != "profileId"))
            throw new ArgumentException("Sandbox host setup and limits require authorized Admin setup.");
        if (tool is "credentials.binding.propose" or "credentials.binding.remove")
        {
            var field = tool == "credentials.binding.propose" ? "credentialId" : "bindingId";
            if (!Guid.TryParseExact(doc.RootElement.GetProperty(field).GetString(), "D", out var id) || id == Guid.Empty)
                throw new ArgumentException("Use the exact owned credential/binding identity.");
            if (tool == "credentials.binding.propose" && doc.RootElement.GetProperty("reference").GetString() is { } reference
                && reference != AgentCore.Domain.Credentials.CredentialRules.Reference(reference)) throw new ArgumentException("Use a canonical binding reference.");
        }
        JsonNode? Canonical(JsonElement value) => value.ValueKind switch {
            JsonValueKind.Object => new JsonObject(value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new KeyValuePair<string,JsonNode?>(p.Name,Canonical(p.Value)))),
            JsonValueKind.Array => new JsonArray(value.EnumerateArray().Select(Canonical).ToArray()), _ => JsonNode.Parse(value.GetRawText()) };
        var canonical = Canonical(doc.RootElement)!.ToJsonString();
        return new(tool, canonical, doc.RootElement.GetProperty("expectedInstanceRevision").GetInt64(), doc.RootElement.Clone(), Harness: harness, Remedy: remedy);
    }
    public static string SemanticArguments(string arguments)
    {
        var body = JsonNode.Parse(arguments)!.AsObject(); body.Remove("expectedInstanceRevision"); body.Remove("expectedRevision"); body.Remove("expectedResourceRevision"); return body.ToJsonString();
    }
    public static TenantPreferencePatch? Preference(string tool, string arguments)
    {
        if (tool != "settings.patch") return null;
        var args = Parse(tool, arguments).Value;
        if (args.GetProperty("section").GetString() != "conversationPolicy") return null;
        try { return TenantPreferenceToolContract.Parse(JsonSerializer.Serialize(new { set = args.GetProperty("set"), clear = args.GetProperty("clear") })); }
        catch (ArgumentException) { return null; }
    }
    public static ModelToolDefinition ForModel(ModelToolDefinition tool) => tool with { Description = tool.Name switch {
        "configuration.propose" => "Apply only the complete exact remedy returned by this Run’s configuration.inspect, with its option, target, goal, fields and Instance revision. Core validates current sources, typed settings and authority. Material changes require mutate_management exact consent; the three existing Managed preferences keep their ordinary policy. A verified safe remedy may admit one bounded fresh Run; original task writes require new exact authorization. No prior task effect is replayed.",
        _ when TenantManagementBindingRules.IsTool(tool.Name) => "Propose only this Agent’s exact existing credential binding through mutate_management. Current Connections delegation, raw Agent/credential reads and agents.update remain necessary. Consent covers only the primary credentials.bind action; no secret, OAuth, provider setup or downstream use is granted. Inspect configuration first and preserve exact identities/revisions. Changes apply to new Runs.",
        _ when TenantHarnessCommandRules.IsTool(tool.Name) => "Customize only this Agent through its existing Resource/settings owners under the admitted and current Harness areas. Use harness.inspect version, policyRevision and Instance revision. Preserve complete intended instructions and tool selection. expected/observed/limitation are an Agent assessment, never proof of execution. Knowledge requires actual current-task material and independent Resource/source reads. Managed knowledge can apply only under current Allowed IAM/policy; Assisted knowledge, instructions and all tool changes require mutate_management exact consent. Current Runs remain pinned; no shared Definition publication or authority is granted.",
        _ when TenantManagementResourceRules.IsTool(tool.Name) => "Change only this Agent's Resource through its existing configuration owner. Inspect current item and Instance revisions first. Upsert retains a reviewed literal text draft and evidence of actual current-task material; source names alone grant no authority. Definition copy verifies original bytes and keeps independent provenance. Enable accepts null only to reset inheritance. Writes require mutate_management exact consent, independent source reads and current typed Resource permissions; deletion requires resources.delete. Changes affect future Runs and grant no read, capability or execution authority.",
        _ when TenantManagementSkillRules.IsTool(tool.Name) => "Change only this Agent’s exact Skill through its existing configuration owner. Inspect first and use current item and Instance revisions. Definition/Built-in procedures are immutable; customization retains source provenance. Managed eligible Skill changes can apply under current Allowed policy; deletion, customization and Assisted changes require mutate_management exact consent. Changes affect future Runs and grant no capability, source read or execution authority.",
        "settings.patch" => "Patch only this Agent's exact inspected setting section with expectedInstanceRevision, set and clear. Core validates Definition/host constraints. Only the three Managed conversation preferences may auto-apply under current Allowed policy. Other changes require mutate_management exact consent. Cleared fields restore Definition defaults. Changes apply to new Runs; no tool effect is replayed.",
        "management.freeze" => "Propose freezing this Agent's active management with its exact inspected Instance revision. Requires mutate_management exact consent and current authority. Management revokes immediately; ordinary tasks and retained content remain. This tool cannot unfreeze or enable management.",
        "management.policy.update" => "Propose exact mode, scopes and Managed allowances for this Agent's inspected revision through mutate_management. Always requires exact qualified review. Cannot enable management from Off or unfreeze. New grants apply to new Runs; removals revoke immediately. No IAM permission is granted.",
        _ => "Change this Agent's unattended model and reasoning overrides for future Runs, using the exact inspected Instance revision and configured model choices. Null restores inheritance. Requires mutate_management exact consent; current Runs stay pinned and no Automation is admitted or executed."
    } };
    public static JsonNode Schema(IReadOnlyList<string> tools) => new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
        ["properties"] = new JsonObject { ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("mutate_management") },
            ["change"] = new JsonObject { ["oneOf"] = new JsonArray(tools.Select(tool => (JsonNode)new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JsonObject { ["tool"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(tool) }, ["arguments"] = JsonNode.Parse(ToolRegistry.Get(tool).ModelDefinition.ParametersJson) },
                ["required"] = new JsonArray("tool", "arguments") }).ToArray()) } }, ["required"] = new JsonArray("operation", "change") };
    public static TenantManagementMutationIntent Intent(JsonElement root)
    {
        TenantApprovalToolContract.Require(root, ["operation", "change"]); var change = root.GetProperty("change"); TenantApprovalToolContract.Require(change, ["tool", "arguments"]);
        var args = Parse(change.GetProperty("tool").GetString()!, change.GetProperty("arguments").GetRawText()); return new(args.Tool, args.Json);
    }
}
