using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantAgentConfigurationSnapshot?> ReadTenantAgentConfigurationAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var key = instanceId.ToString("D");
        var row = await OwnedAgents(db, tenantId).SingleOrDefaultAsync(x => x.Instance.InstanceId == key, ct).ConfigureAwait(false);
        if (row is null) return null;
        return new(Owned(row), await SqliteAgentInstanceStore.ReadSkillsAsync(db, instanceId, ct).ConfigureAwait(false),
            await SqliteAgentInstanceStore.ReadResourcesAsync(db, instanceId, ct).ConfigureAwait(false));
    }

    public async ValueTask<TenantAgent?> TryChangeTenantAgentConfigurationAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantAgentConfigurationChange change, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        IamStateRules.ValidateAgentWrite(next, expectedSecurityRevision, change.InstanceId, AgentInstanceLifecycle.Active, audit);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Revision != expectedSecurityRevision) return null;
        var before = (await ReadTenantAsync(db, tenant.TenantId, ct).ConfigureAwait(false))!;
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next);
        await ValidateTenantActorAsync(db, before, audit.ActorPrincipalId, ct).ConfigureAwait(false);
        await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        var identity = before.SystemIdentities.SingleOrDefault(i => i.AgentInstanceId == change.InstanceId);
        if (identity is null) return null;
        var key = change.InstanceId.ToString("D");
        var row = await db.AgentInstances.SingleOrDefaultAsync(i => i.InstanceId == key, ct).ConfigureAwait(false);
        if (row is null || row.Revision != change.ExpectedInstanceRevision) return null;
        var current = SqliteAgentInstanceStore.Map(row);
        var updated = TenantAgentConfigurationRules.Apply(current, change, at);
        if (next.Assignments.Any(a => TenantAgentChildScopes.BelongsTo(a.Scope, change.InstanceId)))
        {
            var available = await ChildCatalogAsync(db, next.Tenant.TenantId, change.InstanceId, change, ct).ConfigureAwait(false);
            if (next.Assignments.Any(a => TenantAgentChildScopes.BelongsTo(a.Scope, change.InstanceId) && !available.Contains(a.Scope)))
                throw new ArgumentException("Retire grants for removed children in the same Instance transaction.");
        }
        if (change.ActiveVersion is { } version && Guid.TryParse(current.DefinitionId, out var definitionId))
        {
            var id = definitionId.ToString("D");
            var publication = await db.AgentDefinitionPublications.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(p => p.DefinitionId == id && p.Version == version
                && db.Set<TenantDefinitionRecord>().Any(r => r.DefinitionId == id && r.TenantId == next.Tenant.TenantId && r.DeletedAt == null), ct).ConfigureAwait(false);
            var source = publication is null ? null : AgentDefinitionAdminMapping.MapPublication(publication);
            if (source?.Status != DefinitionPublicationStatus.Active || source.MetadataRevision != change.ExpectedPublicationRevision)
                throw new SecurityConflictException("resource_changed");
            change = change with { DefinitionSkills = source.Payload.SkillList };
        }
        row.PersonaJson = JsonSerializer.Serialize(updated.Persona, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (change.Resource?.Resource is { } binding)
        {
            if (definitionContent is null) throw new AccessDeniedException("resource_content_unavailable");
            _ = await AgentCore.Application.Admin.DefinitionResourceStoredContent.RequireForBindingAsync(definitionContent, binding.ContentSha256, binding.ByteLength, binding.MediaType, ct).ConfigureAwait(false);
        }
        if (change.Skill is { } skill) await SqliteAgentInstanceStore.StageSkillsAsync(db, skill, null, ct).ConfigureAwait(false);
        if (change.Resource is { } resource) await SqliteAgentInstanceStore.StageResourcesAsync(db, resource, null, ct).ConfigureAwait(false);
        row.PersonaRevision = updated.PersonaRevision; row.ActiveVersion = updated.ActiveVersion;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        row.SettingsOverridesJson = updated.SettingsOverrides is null ? null : JsonSerializer.Serialize(updated.SettingsOverrides, json);
        row.ExecutionBudgetsJson = updated.ExecutionBudgets is null ? null : JsonSerializer.Serialize(updated.ExecutionBudgets, json);
        row.HarnessManagementJson = updated.HarnessManagement is null ? null : JsonSerializer.Serialize(updated.HarnessManagement, json);
        row.UnattendedModelCatalogKey = updated.UnattendedModelCatalogKey; row.UnattendedReasoningEffort = updated.UnattendedReasoningEffort;
        row.Revision = updated.Revision; row.UpdatedAtUtc = at.ToUnixTimeMilliseconds();
        await SqliteAgentInstanceStore.InitializeSkillsAsync(db, change.InstanceId, change.DefinitionSkills, at, ct).ConfigureAwait(false);
        await SqliteAgentInstanceStore.StageAutomationPolicyAsync(db, new(change.InstanceId, change.ExpectedInstanceRevision,
            AutomationPolicyChanges: change.AutomationPolicyChanges), at, ct).ConfigureAwait(false);
        db.Entry(tenant).CurrentValues.SetValues(next.Tenant);
        await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        // Tenant events need a tenant-aware owner; never fabricate the legacy LocalUserProfile attribution.
        await RequireAgentHumanAsync(db, next.Tenant.TenantId, audit.ActorPrincipalId, change.Initiator, ct).ConfigureAwait(false);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return null; }
        return new(next.Tenant.TenantId, identity.SystemIdentityId, identity.PrincipalId, SqliteAgentInstanceStore.Map(row));
    }
}
