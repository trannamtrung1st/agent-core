using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
namespace AgentCore.Application.Admin;

public sealed record InstanceSkillInput(string Name, string Description, string Procedure, SkillProjection Projection,
    bool Enabled, IReadOnlyList<string> RequiredCapabilities);
public sealed record InstanceSkillView(string Key, SkillOrigin Origin, string Name, string Description, string Procedure,
    SkillProjection Projection, bool Enabled, IReadOnlyList<string> RequiredCapabilities, long Revision,
    int? DefinitionVersion, string? SourceDefinitionId, int? SourceDefinitionVersion, string? SourceDefinitionSkillId,
    IReadOnlyList<string> MissingCapabilities, SkillAuthor? CreatedBy = null, DateTimeOffset? CreatedAt = null, DateTimeOffset? UpdatedAt = null);

public sealed class AgentInstanceSkillService(IAgentInstanceStore instances, IAgentDefinitionStore definitions, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<IReadOnlyList<InstanceSkillView>> ListAsync(Guid instanceId, AgentDefinition? context = null, CancellationToken ct = default)
    {
        var owner = await RequireAsync(instanceId, ct);
        var definition = context ?? await DefinitionAsync(owner, ct);
        var state = await instances.ReadSkillsAsync(instanceId, ct);
        return Views(definition, state);
    }
    public async ValueTask<InstanceSkillView> InspectAsync(Guid instanceId, string key, AgentDefinition? context = null, CancellationToken ct = default) =>
        (await ListAsync(instanceId, context, ct)).SingleOrDefault(s => s.Key == key) ?? throw AgentCoreErrors.NotFound("Skill was not found.");

    public async ValueTask<InstanceSkillView> WriteAsync(Guid id, string operation, string? key = null, long? expectedRevision = null,
        InstanceSkillInput? input = null, bool? enabled = null, SkillAuthor actor = SkillAuthor.Admin, AgentDefinition? context = null, CancellationToken ct = default)
    {
        var owner = await RequireAsync(id, ct);
        if (owner.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Validation("Archived instances cannot change Skills.");
        var definition = context ?? await DefinitionAsync(owner, ct);
        var snapshot = await instances.ReadSkillsAsync(id, ct);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
        AgentInstanceSkill? local = null;
        AgentDefinitionSkillState? state = null;
        Guid? delete = null;
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
                local = new(ids.NewId(), id, input.Name, input.Description, input.Procedure, input.Projection, input.Enabled,
                    input.RequiredCapabilities.ToArray(), 1, now, now, actor);
                break;
            case "update":
            case "delete":
                if (current!.Origin != SkillOrigin.Instance) throw AgentCoreErrors.Validation("Definition Skill content is read-only; use Customize or change enabled state.");
                var existing = snapshot.InstanceSkills.Single(s => "instance:" + s.SkillId.ToString("D") == key);
                oldSkillRevision = existing.Revision;
                if (operation == "delete") { delete = existing.SkillId; break; }
                if (input is null) throw AgentCoreErrors.Validation("Skill content is required.");
                Validate(input, definition);
                local = existing with { Name = input.Name, Description = input.Description, Procedure = input.Procedure,
                    Projection = input.Projection, Enabled = input.Enabled, RequiredCapabilities = input.RequiredCapabilities.ToArray(),
                    Revision = existing.Revision + 1, UpdatedAt = now };
                break;
            case "set_enabled":
                if (enabled is null) throw AgentCoreErrors.Validation("Enabled is required.");
                if (current!.Origin == SkillOrigin.Instance)
                {
                    var previous = snapshot.InstanceSkills.Single(s => "instance:" + s.SkillId.ToString("D") == key);
                    oldSkillRevision = previous.Revision;
                    local = previous with { Enabled = enabled.Value, Revision = previous.Revision + 1, UpdatedAt = now };
                }
                else
                {
                    var previous = snapshot.DefinitionStates.Single(s => "definition:" + s.DefinitionSkillId == key);
                    oldStateRevision = previous.Revision;
                    state = previous with { Enabled = enabled.Value, Revision = previous.Revision + 1, UpdatedAt = now };
                }
                break;
            case "customize":
                if (current!.Origin != SkillOrigin.Definition) throw AgentCoreErrors.Validation("Customize requires a Definition Skill.");
                var source = definition.SkillList.Single(s => "definition:" + s.Id == key);
                if (source.ResourcePaths.Count > 0) throw AgentCoreErrors.Validation("This Skill binds Definition resources; an independent Instance copy cannot preserve those bindings.");
                Validate(new(source.Name, source.Description, source.Procedure, source.Projection, true, source.RequiredCapabilities), definition);
                local = new(ids.NewId(), id, source.Name, source.Description, source.Procedure, source.Projection, true,
                    source.RequiredCapabilities.ToArray(), 1, now, now, actor, definition.Id, definition.Version, source.Id);
                var old = snapshot.DefinitionStates.Single(s => s.DefinitionSkillId == source.Id);
                oldStateRevision = old.Revision;
                state = old with { Enabled = false, Revision = old.Revision + 1, UpdatedAt = now };
                break;
            default: throw AgentCoreErrors.Validation("Unknown Skill operation.");
        }
        var next = new InstanceSkillSnapshot(snapshot.DefinitionStates.Select(s => state?.DefinitionSkillId == s.DefinitionSkillId ? state : s).ToArray(),
            snapshot.InstanceSkills.Where(s => s.SkillId != delete && s.SkillId != local?.SkillId).Concat(local is null ? [] : new[] { local }).ToArray());
        _ = EffectiveSkillCatalogResolver.Resolve(definition, next);
        var history = actor == SkillAuthor.Admin ? new AdminEventAppend(ids.NewId(), now, AdminEventActorKind.LocalOwner,
            AdminEventOperationKind.InstanceSkillsChanged, "agent.instance", id.ToString("D"), owner.Revision + 1,
            definition.Version, JsonSerializer.Serialize(new { instanceId = id.ToString("D"), operation, skillKey = key ?? "instance:" + local!.SkillId.ToString("D") })) : null;
        ct.ThrowIfCancellationRequested();
        await instances.MutateSkillsAsync(new(id, owner.Revision, state, local, delete, oldSkillRevision, oldStateRevision, history), ct);
        if (delete is not null) return current!;
        return Views(definition, next).Single(s => s.Key == (local is not null ? "instance:" + local.SkillId.ToString("D") : key));
    }
    private static void Validate(InstanceSkillInput input, AgentDefinition definition)
    {
        try { SkillPolicy.Validate(input.Name, input.Description, input.Procedure, input.Projection, input.RequiredCapabilities); }
        catch (ArgumentException e) { throw AgentCoreErrors.Validation(e.Message); }
        foreach (var c in input.RequiredCapabilities)
            if (c != SkillCapabilities.ChatRespond && (!ToolRegistry.TryGet(c, out var descriptor) || !descriptor.DefinitionAuthorizable || !RolePermissions.AllowsTool(definition, c)))
                throw AgentCoreErrors.Validation($"Required capability '{c}' is not authorized by this Definition.");
    }
    private static IReadOnlyList<InstanceSkillView> Views(AgentDefinition d, InstanceSkillSnapshot snapshot)
    {
        IReadOnlyList<string> Missing(IReadOnlyList<string> required) => required.Where(c => c != SkillCapabilities.ChatRespond && !RolePermissions.AllowsTool(d, c)).ToArray();
        return d.SkillList.Select(s => {
            var state = snapshot.DefinitionStates.SingleOrDefault(x => x.DefinitionSkillId == s.Id) ?? throw AgentCoreErrors.Persistence("Definition Skill state is missing.");
            return new InstanceSkillView("definition:" + s.Id, SkillOrigin.Definition, s.Name, s.Description, s.Procedure, s.Projection,
                state.Enabled, s.RequiredCapabilities, state.Revision, d.Version, null, null, null, Missing(s.RequiredCapabilities));
        }).Concat(snapshot.InstanceSkills.OrderBy(s => s.CreatedAt).Select(s => new InstanceSkillView("instance:" + s.SkillId.ToString("D"),
            SkillOrigin.Instance, s.Name, s.Description, s.Procedure, s.Projection, s.Enabled, s.RequiredCapabilities, s.Revision,
            null, s.SourceDefinitionId, s.SourceDefinitionVersion, s.SourceDefinitionSkillId, Missing(s.RequiredCapabilities), s.CreatedBy, s.CreatedAt, s.UpdatedAt))).ToArray();
    }
    private async ValueTask<AgentInstance> RequireAsync(Guid id, CancellationToken ct) => await instances.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
    private async ValueTask<AgentDefinition> DefinitionAsync(AgentInstance owner, CancellationToken ct) =>
        await definitions.GetAsync(owner.DefinitionId, owner.ActiveVersion, ct) ?? throw AgentCoreErrors.NotFound("Definition was not found.");
}
