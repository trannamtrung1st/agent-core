using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class SqliteAgentInstanceStore
{
    private static AgentInstanceResource MapResource(AgentInstanceResourceRecord row)
    {
        var value = JsonSerializer.Deserialize<AgentInstanceResource>(row.PayloadJson, Json) ?? throw AgentCoreErrors.Persistence("Resource metadata is missing.");
        if (value.InstanceId.ToString("D") != row.InstanceId || value.ResourceId.ToString("D") != row.ResourceId || value.Revision != row.Revision || value.LogicalPath != row.LogicalPath)
            throw AgentCoreErrors.Persistence("Resource identity disagrees with its stored metadata.");
        return value;
    }
    public async ValueTask<InstanceResourceSnapshot> ReadResourcesAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var owner = id.ToString("D");
        var resources = await db.AgentInstanceResources.AsNoTracking().Where(r => r.InstanceId == owner).ToArrayAsync(ct);
        var states = await db.AgentDefinitionResourceStates.AsNoTracking().Where(r => r.InstanceId == owner).ToArrayAsync(ct);
        return new(states.Select(r => new AgentDefinitionResourceState(id, Guid.Parse(r.ResourceId), r.EnabledOverride, r.Revision, DateTimeOffset.FromUnixTimeMilliseconds(r.UpdatedAtUtc))).ToArray(), resources.Select(MapResource).ToArray());
    }
    public async ValueTask MutateResourcesAsync(InstanceResourceMutation m, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var id = m.InstanceId.ToString("D");
        var owner = await db.AgentInstances.SingleOrDefaultAsync(r => r.InstanceId == id, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
        if (owner.Lifecycle != "Active") throw AgentCoreErrors.Validation("Archived instances are read-only.");
        if (owner.Revision != m.ExpectedInstanceRevision) throw AgentCoreErrors.Conflict("Instance revision is stale.");
        var collection = await db.AgentInstanceResources.Where(r => r.InstanceId == id).ToArrayAsync(ct);
        var resourceId = m.DeleteResourceId ?? m.Resource?.ResourceId;
        var row = collection.SingleOrDefault(r => r.ResourceId == resourceId?.ToString("D"));
        if (resourceId is not null && row?.Revision != m.ExpectedResourceRevision) throw AgentCoreErrors.Conflict("Resource revision is stale.");
        if (m.Resource is { } resource)
        {
            if (resource.InstanceId != m.InstanceId || resource.ResourceId == Guid.Empty) throw AgentCoreErrors.Validation("Resource owner/identity is invalid.");
            InMemoryAgentInstanceStore.ValidateResourceCollection(collection.Where(r => r.ResourceId != resource.ResourceId.ToString("D")).Select(MapResource).Append(resource));
            if (row is null) { row = new() { InstanceId = id, ResourceId = resource.ResourceId.ToString("D") }; db.AgentInstanceResources.Add(row); }
            row.LogicalPath = resource.LogicalPath; row.Revision = resource.Revision; row.PayloadJson = JsonSerializer.Serialize(resource, Json);
        }
        if (m.DeleteResourceId is not null)
        {
            if (row is null) throw AgentCoreErrors.NotFound("Instance resource was not found.");
            db.AgentInstanceResources.Remove(row);
        }
        if (m.DefinitionState is { } state)
        {
            if (state.InstanceId != m.InstanceId || state.ResourceId == Guid.Empty) throw AgentCoreErrors.Validation("Resource state owner/identity is invalid.");
            var key = state.ResourceId.ToString("D");
            var stateRow = await db.AgentDefinitionResourceStates.SingleOrDefaultAsync(r => r.InstanceId == id && r.ResourceId == key, ct);
            if (stateRow?.Revision != m.ExpectedStateRevision) throw AgentCoreErrors.Conflict("Resource state revision is stale.");
            if (stateRow is null) { stateRow = new() { InstanceId = id, ResourceId = key }; db.AgentDefinitionResourceStates.Add(stateRow); }
            stateRow.EnabledOverride = state.EnabledOverride; stateRow.Revision = state.Revision; stateRow.UpdatedAtUtc = state.UpdatedAt.ToUnixTimeMilliseconds();
        }
        owner.Revision++;
        if (m.History is not null) owner.UpdatedAtUtc = m.History.OccurredAt.ToUnixTimeMilliseconds();
        if (m.HarnessManagement is not null) owner.HarnessManagementJson = JsonSerializer.Serialize(m.HarnessManagement, Json);
        if (m.History is not null) AdminEventPersistence.StageAppend(db, m.History, ids.NewId());
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        catch (DbUpdateException) { throw AgentCoreErrors.Conflict("Resource mutation conflicted with another write."); }
    }
}
