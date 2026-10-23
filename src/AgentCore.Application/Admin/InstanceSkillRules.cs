using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
namespace AgentCore.Application.Admin;

public sealed record InstanceSkillInput(string Name, string Description, string Procedure, SkillProjection Projection,
    bool Enabled, IReadOnlyList<string> RequiredCapabilities, string? Id = null);
public sealed record InstanceSkillView(string Key, SkillOrigin Origin, string Name, string Description, string Procedure,
    SkillProjection Projection, bool Enabled, IReadOnlyList<string> RequiredCapabilities, long Revision,
    int? DefinitionVersion, string? SourceDefinitionId, int? SourceDefinitionVersion, string? SourceDefinitionSkillId,
    IReadOnlyList<string> MissingCapabilities, SkillAuthor? CreatedBy = null, DateTimeOffset? CreatedAt = null, DateTimeOffset? UpdatedAt = null, bool? EnabledOverride = null, long? InstanceRevision = null, bool? EnabledChoice = null);

public sealed record DefinitionSkillMutationState(string Key, bool Enabled, long Revision);
public sealed record InstanceSkillCustomization(InstanceSkillView InstanceSkill, DefinitionSkillMutationState DefinitionSkill);

internal sealed record PreparedInstanceSkill(SkillMutation? Mutation, InstanceSkillSnapshot Next,
    InstanceSkillView Skill, DefinitionSkillMutationState? DefinitionSkill);

internal static class InstanceSkillRules
{
    internal static PreparedInstanceSkill Prepare(AgentInstance owner, AgentDefinition definition, InstanceSkillSnapshot snapshot,
        string operation, string? key, long? expectedRevision, InstanceSkillInput? input, bool? enabled, SkillAuthor actor, DateTimeOffset now)
    {
        var id = owner.InstanceId;
        AgentInstanceSkill? local = null;
        AgentDefinitionSkillState? state = null;
        string? delete = null;
        long? oldSkillRevision = null;
        long? oldStateRevision = null;
        var current = operation == "create" ? null : (Views(definition, snapshot).SingleOrDefault(s => s.Key == key)
            ?? throw AgentCoreErrors.NotFound("Skill was not found."));
        if (current is not null && current.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Skill revision is stale.");
        switch (operation)
        {
            case "create":
                if (input is null) throw AgentCoreErrors.Validation("Skill content is required.");
                Validate(input, definition);
                var requestedId = input.Id?.Trim();
                var skillId = string.IsNullOrEmpty(requestedId) ? SkillIds.FromName(input.Name, snapshot.InstanceSkills.Select(s => s.SkillId)) : requestedId;
                if (!SkillIds.IsValid(skillId)) throw AgentCoreErrors.Validation("Skill ID must start with a lowercase letter and use lowercase letters, digits, dots, underscores or hyphens, up to 64 characters.");
                if (snapshot.InstanceSkills.Any(s => s.SkillId == skillId)) throw AgentCoreErrors.Conflict("This Skill ID already exists in this Agent Instance.");
                local = new(skillId, id, input.Name, input.Description, input.Procedure, input.Projection, input.Enabled,
                    input.RequiredCapabilities.ToArray(), 1, now, now, actor);
                break;
            case "update":
            case "delete":
                if (current!.Origin != SkillOrigin.Instance) throw AgentCoreErrors.Validation("Definition Skill content is read-only; use Customize or change enabled state.");
                var existing = snapshot.InstanceSkills.Single(s => "instance:" + s.SkillId == key);
                oldSkillRevision = existing.Revision;
                if (operation == "delete") { delete = existing.SkillId; break; }
                if (input is null) throw AgentCoreErrors.Validation("Skill content is required.");
                Validate(input, definition);
                if (!string.IsNullOrWhiteSpace(input.Id) && input.Id != existing.SkillId) throw AgentCoreErrors.Validation("Skill ID cannot change after creation.");
                local = existing with { Name = input.Name, Description = input.Description, Procedure = input.Procedure,
                    Projection = input.Projection, Enabled = input.Enabled, RequiredCapabilities = input.RequiredCapabilities.ToArray(),
                    Revision = existing.Revision + 1, UpdatedAt = now };
                break;
            case "set_enabled":
                if (enabled is null) throw AgentCoreErrors.Validation("Enabled is required.");
                if (current!.Origin == SkillOrigin.Instance)
                {
                    var previous = snapshot.InstanceSkills.Single(s => "instance:" + s.SkillId == key);
                    oldSkillRevision = previous.Revision;
                    local = previous with { Enabled = enabled.Value, Revision = previous.Revision + 1, UpdatedAt = now };
                }
                else
                {
                    var previous = snapshot.DefinitionStates.Single(s => "definition:" + s.DefinitionSkillId == key);
                    oldStateRevision = previous.Revision;
                    state = previous with { EnabledOverride = enabled.Value, Revision = previous.Revision + 1, UpdatedAt = now };
                }
                break;
            case "reset_enabled":
                if (current!.Origin != SkillOrigin.Definition) throw AgentCoreErrors.Validation("Reset requires a Definition Skill.");
                var inherited = snapshot.DefinitionStates.Single(s => "definition:" + s.DefinitionSkillId == key);
                if (inherited.EnabledOverride is null) return new(null, snapshot, current, null);
                oldStateRevision = inherited.Revision;
                state = inherited with { EnabledOverride = null, Revision = inherited.Revision + 1, UpdatedAt = now };
                break;
            case "customize":
                if (current!.Origin != SkillOrigin.Definition) throw AgentCoreErrors.Validation("Customize requires a Definition Skill.");
                var source = definition.SkillList.Single(s => "definition:" + s.Id == key);
                if (source.ResourcePaths.Count > 0) throw AgentCoreErrors.Validation("This Skill binds Definition resources; an independent Instance copy cannot preserve those bindings.");
                Validate(new(source.Name, source.Description, source.Procedure, source.Projection, true, source.RequiredCapabilities), definition);
                local = new(SkillIds.FromName(source.Name, snapshot.InstanceSkills.Select(s => s.SkillId)), id, source.Name, source.Description, source.Procedure, source.Projection, true,
                    source.RequiredCapabilities.ToArray(), 1, now, now, actor, definition.Id, definition.Version, source.Id);
                var old = snapshot.DefinitionStates.Single(s => s.DefinitionSkillId == source.Id);
                oldStateRevision = old.Revision;
                state = old with { EnabledOverride = false, Revision = old.Revision + 1, UpdatedAt = now };
                break;
            default: throw AgentCoreErrors.Validation("Unknown Skill operation.");
        }
        var next = new InstanceSkillSnapshot(snapshot.DefinitionStates.Select(s => state?.DefinitionSkillId == s.DefinitionSkillId ? state : s).ToArray(),
            snapshot.InstanceSkills.Where(s => s.SkillId != delete && s.SkillId != local?.SkillId).Concat(local is null ? [] : new[] { local }).ToArray());
        var written = delete is not null ? current! : Views(definition, next).Single(s => s.Key == (local is not null ? "instance:" + local.SkillId : key));
        return new(new(id, owner.Revision, state, local, delete, oldSkillRevision, oldStateRevision), next, written,
            operation == "customize" ? new("definition:" + state!.DefinitionSkillId, state.EnabledOverride!.Value, state.Revision) : null);
    }

    internal static IReadOnlyList<InstanceSkillView> BuiltIns(AgentDefinition definition, HarnessManagementPolicy policy, AgentInstance owner)
    {
        var templates = ManagementBuiltInSkills.Catalog;
        return templates.Select(s => new InstanceSkillView(s.Key, SkillOrigin.BuiltIn, s.Name, s.Description, s.Procedure, s.Projection,
            ManagementTools.Enabled(policy) && (s.Key == ManagementBuiltInSkills.ConfigurationKey ? policy.BuiltInSkills?.ConfigurationAssistance != false : policy.BuiltInSkills?.HarnessAuthoring != false)
                && (s.Key != ManagementBuiltInSkills.HarnessKey || policy.Scopes.Any(a => a is HarnessManagementScope.KnowledgeResources or HarnessManagementScope.Instructions or HarnessManagementScope.ToolSelection))
                && s.RequiredCapabilities.All(c => RolePermissions.AllowsTool(definition, c)),
            s.RequiredCapabilities, owner.HarnessManagement?.PolicyRevision ?? 1, null, null, null, s.SourceId,
            s.RequiredCapabilities.Where(c => !RolePermissions.AllowsTool(definition, c)).ToArray(),
            EnabledOverride: s.Key == ManagementBuiltInSkills.ConfigurationKey ? owner.HarnessManagement?.Overrides?.ConfigurationAssistance?.Value : owner.HarnessManagement?.Overrides?.HarnessAuthoring?.Value,
            InstanceRevision: owner.Revision, EnabledChoice: s.Key == ManagementBuiltInSkills.ConfigurationKey ? policy.BuiltInSkills?.ConfigurationAssistance ?? true : policy.BuiltInSkills?.HarnessAuthoring ?? true)).ToArray();
    }
    private static void Validate(InstanceSkillInput input, AgentDefinition definition)
    {
        try { SkillPolicy.Validate(input.Name, input.Description, input.Procedure, input.Projection, input.RequiredCapabilities); }
        catch (ArgumentException e) { throw AgentCoreErrors.Validation(e.Message); }
        foreach (var c in input.RequiredCapabilities)
            if (c != SkillCapabilities.ChatRespond && (!ToolRegistry.TryGet(c, out var descriptor) || !descriptor.DefinitionAuthorizable || !RolePermissions.AllowsTool(definition, c)))
                throw AgentCoreErrors.Validation($"Required capability '{c}' is not authorized by this Definition.");
    }
    internal static IReadOnlyList<InstanceSkillView> Views(AgentDefinition d, InstanceSkillSnapshot snapshot)
    {
        IReadOnlyList<string> Missing(IReadOnlyList<string> required) => required.Where(c => c != SkillCapabilities.ChatRespond && !RolePermissions.AllowsTool(d, c)).ToArray();
        return d.SkillList.Select(s => {
            var state = snapshot.DefinitionStates.SingleOrDefault(x => x.DefinitionSkillId == s.Id) ?? throw AgentCoreErrors.Persistence("Definition Skill state is missing.");
            return new InstanceSkillView("definition:" + s.Id, SkillOrigin.Definition, s.Name, s.Description, s.Procedure, s.Projection,
                state.EnabledOverride ?? s.DefaultEnabled, s.RequiredCapabilities, state.Revision, d.Version, null, null, null, Missing(s.RequiredCapabilities), EnabledOverride: state.EnabledOverride);
        }).Concat(snapshot.InstanceSkills.OrderBy(s => s.CreatedAt).Select(s => new InstanceSkillView("instance:" + s.SkillId,
            SkillOrigin.Instance, s.Name, s.Description, s.Procedure, s.Projection, s.Enabled, s.RequiredCapabilities, s.Revision,
            null, s.SourceDefinitionId, s.SourceDefinitionVersion, s.SourceDefinitionSkillId, Missing(s.RequiredCapabilities), s.CreatedBy, s.CreatedAt, s.UpdatedAt))).ToArray();
    }
}
