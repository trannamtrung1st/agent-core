using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using Microsoft.EntityFrameworkCore;
namespace AgentCore.Infrastructure.Persistence;
public sealed partial class SqliteAgentInstanceStore
{
    private static async Task InitializeSkillsAsync(AgentCoreDbContext db, Guid id, IReadOnlyList<SkillSpec>? skills, DateTimeOffset at, CancellationToken ct)
    {
        if (skills is null) return;
        var owner = id.ToString("D");
        var states = await db.AgentDefinitionSkillStates.Where(s => s.AgentInstanceId == owner).ToArrayAsync(ct);
        var locals = await db.AgentInstanceSkills.Where(s => s.AgentInstanceId == owner).ToArrayAsync(ct);
        try { SkillPolicy.ValidateAlwaysBudget(skills.Select(s => (s.Projection, states.SingleOrDefault(x => x.DefinitionSkillId == s.Id)?.Enabled ?? s.DefaultEnabled, s.Procedure))
            .Concat(locals.Select(s => ((SkillProjection)s.Projection, s.Enabled, s.Procedure)))); }
        catch (ArgumentException e) { throw AgentCoreErrors.Validation(e.Message); }
        var known = states.Select(s => s.DefinitionSkillId).ToArray();
        foreach (var s in skills.Where(s => !known.Contains(s.Id))) db.AgentDefinitionSkillStates.Add(new()
            { AgentInstanceId = owner, DefinitionSkillId = s.Id, Enabled = s.DefaultEnabled, Revision = 1, UpdatedAtUtc = at.ToUnixTimeMilliseconds() });
    }
    public async ValueTask<InstanceSkillSnapshot> ReadSkillsAsync(Guid instanceId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var id = instanceId.ToString("D");
        var states = await db.AgentDefinitionSkillStates.AsNoTracking().Where(s => s.AgentInstanceId == id).ToArrayAsync(ct);
        var skills = await db.AgentInstanceSkills.AsNoTracking().Where(s => s.AgentInstanceId == id).ToArrayAsync(ct);
        return new(states.Select(s => new AgentDefinitionSkillState(instanceId, s.DefinitionSkillId, s.Enabled, s.Revision, DateTimeOffset.FromUnixTimeMilliseconds(s.UpdatedAtUtc))).ToArray(),
            skills.Select(s => new AgentInstanceSkill(s.SkillId, instanceId, s.Name, s.Description, s.Procedure,
                (SkillProjection)s.Projection, s.Enabled, JsonSerializer.Deserialize<string[]>(s.RequiredCapabilitiesJson, Json) ?? throw AgentCoreErrors.Persistence("Invalid stored Skill requirements."),
                s.Revision, DateTimeOffset.FromUnixTimeMilliseconds(s.CreatedAtUtc), DateTimeOffset.FromUnixTimeMilliseconds(s.UpdatedAtUtc),
                (SkillAuthor)s.CreatedBy, s.SourceDefinitionId, s.SourceDefinitionVersion, s.SourceDefinitionSkillId)).ToArray());
    }
    public async ValueTask MutateSkillsAsync(SkillMutation m, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ownerId = m.InstanceId.ToString("D");
        var owner = await db.AgentInstances.SingleOrDefaultAsync(s => s.InstanceId == ownerId, ct) ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (owner.Lifecycle != "Active") throw AgentCoreErrors.Validation("Archived instances cannot change Skills.");
        if (owner.Revision != m.ExpectedInstanceRevision) throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
        if (m.DefinitionState is { } state)
        {
            var row = await db.AgentDefinitionSkillStates.SingleOrDefaultAsync(s => s.AgentInstanceId == ownerId && s.DefinitionSkillId == state.DefinitionSkillId, ct);
            if (row?.Revision != m.ExpectedStateRevision) throw AgentCoreErrors.Conflict("Definition Skill state revision is stale.");
            if (row is null) throw AgentCoreErrors.Persistence("Definition Skill state is missing.");
            row.Enabled = state.Enabled; row.Revision = state.Revision; row.UpdatedAtUtc = state.UpdatedAt.ToUnixTimeMilliseconds();
        }
        var skillId = m.DeleteSkillId ?? m.InstanceSkill?.SkillId;
        if (skillId is string id)
        {
            var key = id;
            var row = await db.AgentInstanceSkills.SingleOrDefaultAsync(s => s.AgentInstanceId == ownerId && s.SkillId == key, ct);
            if (row?.Revision != m.ExpectedSkillRevision) throw AgentCoreErrors.Conflict("Instance Skill revision is stale.");
            if (m.DeleteSkillId is not null) { if (row is null) throw AgentCoreErrors.NotFound("Instance Skill was not found."); db.AgentInstanceSkills.Remove(row); }
            else if (m.InstanceSkill is { } skill)
            {
                if (row is null) { row = new() { AgentInstanceId = ownerId, SkillId = key }; db.AgentInstanceSkills.Add(row); }
                row.Revision = skill.Revision; row.UpdatedAtUtc = skill.UpdatedAt.ToUnixTimeMilliseconds(); row.Name = skill.Name; row.Description = skill.Description; row.Procedure = skill.Procedure;
                row.Projection = (int)skill.Projection; row.Enabled = skill.Enabled;
                row.RequiredCapabilitiesJson = JsonSerializer.Serialize(skill.RequiredCapabilities, Json);
                row.CreatedAtUtc = skill.CreatedAt.ToUnixTimeMilliseconds(); row.CreatedBy = (int)skill.CreatedBy;
                row.SourceDefinitionId = skill.SourceDefinitionId; row.SourceDefinitionVersion = skill.SourceDefinitionVersion;
                row.SourceDefinitionSkillId = skill.SourceDefinitionSkillId;
            }
        }
        owner.Revision++;
        if (m.History is not null) AdminEventPersistence.StageAppend(db, m.History, ids.NewId());
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw AgentCoreErrors.Conflict("Skill revision is stale."); }
        catch (DbUpdateException) { throw AgentCoreErrors.Conflict("Skill mutation conflicted with another write."); }
    }
}
