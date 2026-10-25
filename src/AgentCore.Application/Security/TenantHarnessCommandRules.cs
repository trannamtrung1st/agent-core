using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Contextual Harness vocabulary compiles to incumbent Resource/settings changes under its own delegation.
public static class TenantHarnessCommandRules
{
    public static bool IsTool(string tool) => HarnessChatTools.Operations.ContainsKey(tool);
    public static bool AutoApplies(TenantManagementReader reader, PreparedManagementMutation value) => value.Harness is { } harness && IsResource(harness.ToolName)
        && reader.Run.Admission.Configuration!.Definition.SelfManagement?.Mode == HarnessManagementMode.Managed
        && ManagementPolicyResolver.Resolve(reader.Baseline, reader.Agent.Instance).Mode == HarnessManagementMode.Managed;
    public static bool IsResource(string tool) => tool is "harness.knowledge.upsert" or "harness.knowledge.remove";
    public static void ValidateArguments(string tool, JsonElement args)
    {
        if (!IsTool(tool)) throw new ArgumentException("Use an offered Harness command.");
        string Text(string key) => args.GetProperty(key).GetString()!;
        if (args.GetProperty("expectedVersion").GetInt32() < 1 || args.GetProperty("policyRevision").GetInt64() < 1) throw new ArgumentException("Use current Harness witnesses.");
        if (IsResource(tool))
        {
            var id = Text("id");
            if (args.TryGetProperty("source", out var provenance) && Uri.TryCreate(provenance.GetString(), UriKind.Absolute, out var uri)
                && uri.Scheme is "https" or "http" && !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("Source URLs cannot contain credentials.");
            if (tool == "harness.knowledge.remove" && id.Contains(':')) _ = InstanceResourceRules.ParseKey(id);
            else if (tool == "harness.knowledge.upsert" && id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) throw new ArgumentException("Use a simple knowledge name.");
        }
        if (tool == "harness.tool.select" && (Text("id") == ToolCatalog.AttachmentsRead || !ToolRegistry.TryGet(Text("id"), out var descriptor) || !descriptor.DefinitionAuthorizable))
            throw new ArgumentException("Use an eligible tool selection.");
        foreach (var field in new[] { "content", "expected", "observed", "limitation", "source" })
            if (args.TryGetProperty(field, out var value)) DefinitionResourcePolicies.RejectSecretTokens(value.GetString()!);
    }
    public static TenantManagementMutationArguments Compile(TenantManagementSource source, TenantManagementMutationArguments requested,
        IReadOnlyList<AgentDefinitionPublicationResource> published, PreparedHarnessCommand? prepared = null)
    {
        var reader = source.Reader; var owner = reader.Agent.Instance; var args = requested.Value; var tool = requested.Tool;
        TenantManagementMutationRules.Eligible(reader, tool);
        if (!IsTool(tool) || requested.ExpectedInstanceRevision != owner.Revision || args.GetProperty("expectedVersion").GetInt32() != owner.ActiveVersion
            || args.GetProperty("policyRevision").GetInt64() != (owner.HarnessManagement?.PolicyRevision ?? 1)) throw new SecurityConflictException("management_source_changed");
        object command; string canonical;
        if (IsResource(tool))
        {
            var id = args.GetProperty("id").GetString()!;
            var views = InstanceResourceRules.Views(reader.Current, published, source.Snapshot.Resources, source.Snapshot.Skills);
            if (tool == "harness.knowledge.upsert")
            {
                var previous = views.SingleOrDefault(r => r.Origin == "Instance" && r.LogicalPath == "knowledge/" + id);
                canonical = "resources.upsert";
                var body = new Dictionary<string, object?> { ["expectedInstanceRevision"] = owner.Revision, ["logicalPath"] = "knowledge/" + id,
                    ["kind"] = "Knowledge", ["mediaType"] = "text/markdown", ["content"] = args.GetProperty("content").GetString(), ["source"] = args.GetProperty("source").GetString() };
                if (previous is not null) { body["key"] = previous.Key; body["expectedResourceRevision"] = previous.Revision; }
                command = body;
            }
            else
            {
                var knowledge = DefinitionExecutionPolicies.Of(reader.Baseline).KnowledgeList.SingleOrDefault(k => k.Identity == id);
                var item = views.SingleOrDefault(r => r.Key == id) ?? views.SingleOrDefault(r => r.Origin == "Instance" && r.LogicalPath == "knowledge/" + id)
                    ?? (knowledge is null ? null : views.SingleOrDefault(r => r.Origin == "Definition" && r.LogicalPath == KnowledgeSourcePaths.ResolveBackingPath(knowledge)))
                    ?? throw new AccessDeniedException("resource_read_required");
                canonical = item.Origin == "Instance" ? "resources.delete" : "resources.set_enabled";
                command = item.Origin == "Instance" ? (object)new { key = item.Key, expectedResourceRevision = item.Revision, expectedInstanceRevision = owner.Revision }
                    : new { key = item.Key, expectedResourceRevision = item.Revision, expectedInstanceRevision = owner.Revision, enabled = false };
            }
        }
        else
        {
            canonical = "settings.patch"; string section; object set;
            if (tool == "harness.instructions.update") { section = "instructions"; set = new { agentInstructions = args.GetProperty("content").GetString() }; }
            else
            {
                var id = args.GetProperty("id").GetString()!; var policy = ManagementPolicyResolver.Resolve(reader.Baseline, owner);
                if (!policy.EligibleTools.Contains(id, StringComparer.Ordinal)
                    || reader.Run.Admission.Configuration?.Definition.SelfManagement?.EligibleTools.Contains(id, StringComparer.Ordinal) != true
                    || !RolePermissions.AllowsTool(reader.Baseline, id)) throw new AccessDeniedException("tool_selection_unavailable");
                section = "capabilities";
                set = tool == "harness.tool.configure" ? (object)new { allowUnreadUnsupportedTypes = args.GetProperty("allowUnreadUnsupportedTypes").GetBoolean() }
                    : new { selected = (args.GetProperty("enabled").GetBoolean() ? DefinitionExecutionPolicies.Of(reader.Current).ToolList.Append(id)
                        : DefinitionExecutionPolicies.Of(reader.Current).ToolList.Where(t => t != id)).Distinct(StringComparer.Ordinal).ToArray() };
            }
            command = new { expectedInstanceRevision = owner.Revision, section, set, clear = Array.Empty<string>() };
        }
        var result = TenantManagementMutationContract.Parse(canonical, JsonSerializer.Serialize(command), prepared, authorityTool: tool);
        return result with { Harness = prepared, Requested = new(tool, requested.Json) };
    }
    public static HarnessInstanceChange Assessment(TenantManagementReader reader, TenantManagementMutationArguments requested, Guid id, DateTimeOffset at)
    {
        var value = requested.Value; var next = checked(reader.Agent.Instance.Revision + 1);
        return new(id, HarnessChatTools.Operations[requested.Tool].Kind, next, reader.Agent.Instance.ActiveVersion,
            reader.Agent.Instance.HarnessManagement?.PolicyRevision ?? 1, null, ToolActionHash.Compute(requested.Tool, value),
            value.TryGetProperty("source", out var source) ? source.GetString() : null, at,
            new("Agent", next, "Instance self-management", HarnessEvidenceStatus.PartiallyVerified,
                value.GetProperty("expected").GetString()!, value.GetProperty("observed").GetString()!,
                value.TryGetProperty("limitation", out var limit) ? limit.GetString()! : "Agent assessment is partial; production effects and subjective quality require external evidence."));
    }
    public static HarnessManagementState Append(AgentInstance owner, HarnessInstanceChange assessment, HarnessManagementPolicy? inherited = null)
    {
        var state = owner.HarnessManagement ?? new(inherited ?? throw new AccessDeniedException("management_unavailable"), Overrides: new());
        return state with { InstanceChanges = [.. (state.InstanceChanges ?? []).TakeLast(63), assessment] };
    }
    public static TenantAgentConfigurationChange Attach(TenantManagementMutationArguments args, TenantManagementReader reader, TenantAgentConfigurationChange change) => args.Harness is not { Changed: true } ? change : change with { HarnessAssessment = Append(reader.Agent.Instance, args.Harness.Assessment, ManagementPolicyResolver.Resolve(reader.Baseline, reader.Agent.Instance)) };
    public static TenantManagementMutationArguments Mark(TenantManagementMutationArguments args, TenantManagementReader reader, TenantAgentConfigurationChange? change) => args.Harness is null ? args : args with {
        Harness = args.Harness with { Changed = change is not null && (!change.SetSettingsOverrides
            || JsonSerializer.Serialize(change.SettingsOverrides) != JsonSerializer.Serialize(reader.Agent.Instance.SettingsOverrides ?? new())) }
    };
    public static IReadOnlyList<TenantApprovalFieldChange> Review(PreparedHarnessCommand? command) => command is null ? [] : [
        new("Harness command", null, command.ToolName), new("Agent assessment (partially verified)", null, command.Assessment.Evidence) ];
    public static void Revalidate(TenantManagementSource source, PreparedManagementMutation value, IReadOnlyList<AgentDefinitionPublicationResource> published)
    {
        if (value.Harness is not { } command) return;
        var expected = TenantManagementMutationContract.Parse(command.ToolName, command.ArgumentsJson);
        var compiled = Compile(source, expected, published, command);
        if (compiled.Tool != value.ToolName || compiled.Json != value.ArgumentsJson
            || JsonSerializer.Serialize(command.Assessment) != JsonSerializer.Serialize(Assessment(source.Reader, expected, command.Assessment.OperationId, command.Assessment.SavedAt)))
            throw new AccessDeniedException("management_source_changed");
    }
    public static void Validate(PreparedManagementMutation value)
    {
        if (value.Harness is not { } command) return;
        var requested = TenantManagementMutationContract.Parse(command.ToolName, command.ArgumentsJson);
        if (requested.Json != command.ArgumentsJson || command.Assessment.OperationId == Guid.Empty || command.Assessment.ApprovalId is not null
            || command.Assessment.Operation != HarnessChatTools.Operations[command.ToolName].Kind || command.Assessment.InstanceRevision != requested.ExpectedInstanceRevision + 1
            || command.Assessment.ActionHash != ToolActionHash.Compute(command.ToolName, requested.Value)
            || command.Assessment.Evidence.Status != HarnessEvidenceStatus.PartiallyVerified
            || command.Assessment.DefinitionVersion != requested.Value.GetProperty("expectedVersion").GetInt32()
            || command.Assessment.PolicyRevision != requested.Value.GetProperty("policyRevision").GetInt64()
            || command.Assessment.Evidence.Actor != "Agent" || command.Assessment.Evidence.DraftRevision != command.Assessment.InstanceRevision
            || command.Assessment.Evidence.Expected != requested.Value.GetProperty("expected").GetString()
            || command.Assessment.Evidence.Observed != requested.Value.GetProperty("observed").GetString()
            || command.Assessment.Source != (requested.Value.TryGetProperty("source", out var source) ? source.GetString() : null)
            || command.Assessment.Evidence.Limitation != (requested.Value.TryGetProperty("limitation", out var limitation) ? limitation.GetString() : "Agent assessment is partial; production effects and subjective quality require external evidence.")
            || (value.Resource is not null) != IsResource(command.ToolName) || value.Skill is not null
            || !IsResource(command.ToolName) && value.ToolName != "settings.patch") throw new ArgumentException("Use the exact contextual Harness command and partial assessment.");
    }
}
