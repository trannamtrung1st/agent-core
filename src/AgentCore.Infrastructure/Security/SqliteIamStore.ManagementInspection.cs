using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async Task<TenantManagementReader> ManagementSkillReader(AgentCoreDbContext db, TenantManagementReader reader, CancellationToken ct)
    {
        var skills = await SqliteAgentInstanceStore.ReadSkillsAsync(db, reader.Run.AgentInstanceId, ct).ConfigureAwait(false);
        return reader with { Inspection = new(TenantManagementInspectionSources.SkillItems(reader, skills)) };
    }
    private async Task<TenantManagementReader> ManagementInspectionReader(AgentCoreDbContext db, TenantManagementReader reader, CancellationToken ct)
    {
        var tenant = reader.Security.Tenant.TenantId; var owner = reader.Agent.Instance;
        var snapshot = new TenantAgentConfigurationSnapshot(reader.Agent,
            await SqliteAgentInstanceStore.ReadSkillsAsync(db, owner.InstanceId, ct).ConfigureAwait(false),
            await SqliteAgentInstanceStore.ReadResourcesAsync(db, owner.InstanceId, ct).ConfigureAwait(false),
            await ReadIsolationMetadataAsync(db, owner.InstanceId.ToString("D"), ct).ConfigureAwait(false));
        var inherited = await ManagementPublishedResources(db, reader, ct).ConfigureAwait(false);
        var credentials = new List<TenantCredential>();
        foreach (var row in await OwnedCredentials(db, tenant).Include(c => c.EmailAccount).Include(c => c.DriveAccount).AsNoTracking().ToArrayAsync(ct).ConfigureAwait(false))
            if (await CredentialView(db, tenant, row, ct).ConfigureAwait(false) is { } value) credentials.Add(value);
        var byId = credentials.ToDictionary(c => c.Value.CredentialId);
        var bindings = (await OwnedCredentialBindings(db, tenant, owner.InstanceId).AsNoTracking().ToArrayAsync(ct).ConfigureAwait(false))
            .Where(b => byId.ContainsKey(Guid.Parse(b.CredentialId))).Select(b => new TenantCredentialBinding(MapBinding(b), byId[Guid.Parse(b.CredentialId)])).ToArray();
        return reader with { Inspection = TenantManagementInspectionSources.Catalog(reader, snapshot, inherited, bindings, credentials) };
    }
    private static IQueryable<TenantApprovalRecord> HiddenManagementSkillChanges(AgentCoreDbContext db, TenantManagementApprovalVisibility visibility)
    {
        var readAll = visibility.Skills is { Agents: null }; var writeAll = visibility.SkillChanges is { Agents: null };
        var readAgents = System.Text.Json.JsonSerializer.Serialize(visibility.Skills?.Agents?.Select(id => id.ToString("D")) ?? []);
        var writeAgents = System.Text.Json.JsonSerializer.Serialize(visibility.SkillChanges?.Agents?.Select(id => id.ToString("D")) ?? []);
        var readChildren = System.Text.Json.JsonSerializer.Serialize(visibility.Skills?.Children ?? []);
        var writeChildren = System.Text.Json.JsonSerializer.Serialize(visibility.SkillChanges?.Children ?? []);
        return db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE json_type(PayloadJson,'$.Operation.ManagementMutation.Skill')='object'
            AND (NOT ({readAll} AND {writeAll}) AND NOT EXISTS (
             SELECT 1 FROM AgentInstances owner WHERE owner.InstanceId=TenantApprovalRequests.InstanceId AND
              (owner.Revision=json_extract(TenantApprovalRequests.PayloadJson,'$.Operation.ExpectedInstanceRevision') OR EXISTS (
               SELECT 1 FROM TenantOperationReceipts receipt WHERE TenantApprovalRequests.Status=1 AND receipt.RequestId=TenantApprovalRequests.RequestId
                AND receipt.TenantId=TenantApprovalRequests.TenantId AND receipt.InstanceId=TenantApprovalRequests.InstanceId
                AND receipt.ActionHash=TenantApprovalRequests.Fingerprint AND receipt.Outcome=0 AND receipt.ReasonCode='management_mutation_completed'
                AND receipt.ManagementSkillJson IS NOT NULL AND receipt.InstanceRevision=owner.Revision)))
             OR NOT (CASE WHEN json_extract(PayloadJson,'$.Operation.ManagementMutation.Skill.CreateScope')=1 THEN {readAll} AND {writeAll}
             ELSE ({readAll} OR InstanceId IN (SELECT value FROM json_each({readAgents})) OR ResourceId IN (SELECT value FROM json_each({readChildren})))
              AND ({writeAll} OR InstanceId IN (SELECT value FROM json_each({writeAgents})) OR ResourceId IN (SELECT value FROM json_each({writeChildren}))) END))
            """);
    }
    private static IQueryable<TenantApprovalRecord> HiddenManagementChildren(AgentCoreDbContext db, TenantManagementApprovalVisibility visibility)
    {
        var skillAll = visibility.Skills is { Agents: null }; var resourceAll = visibility.Resources is { Agents: null };
        var skillAgents = System.Text.Json.JsonSerializer.Serialize(visibility.Skills?.Agents?.Select(id => id.ToString("D")) ?? []);
        var skillChildren = System.Text.Json.JsonSerializer.Serialize(visibility.Skills?.Children ?? []);
        var resourceAgents = System.Text.Json.JsonSerializer.Serialize(visibility.Resources?.Agents?.Select(id => id.ToString("D")) ?? []);
        var resourceChildren = System.Text.Json.JsonSerializer.Serialize(visibility.Resources?.Children ?? []);
        var credentialAll = visibility.Credentials is null;
        var credentials = System.Text.Json.JsonSerializer.Serialize(visibility.Credentials?.Select(id => id.ToString("D")) ?? []);
        return db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE EXISTS
            (SELECT 1 FROM json_each(COALESCE(json_extract(PayloadJson,'$.Operation.ManagementRead.SourcesJson'),'[]')) source
             WHERE CASE json_extract(source.value,'$.Kind')
             WHEN 'skill' THEN NOT ({skillAll} OR substr(json_extract(source.value,'$.ResourceId'),1,36) IN (SELECT value FROM json_each({skillAgents}))
                OR json_extract(source.value,'$.ResourceId') IN (SELECT value FROM json_each({skillChildren})))
             WHEN 'skillProcedure' THEN NOT ({skillAll} OR substr(json_extract(source.value,'$.ResourceId'),1,36) IN (SELECT value FROM json_each({skillAgents}))
                OR json_extract(source.value,'$.ResourceId') IN (SELECT value FROM json_each({skillChildren})))
             WHEN 'resource' THEN NOT ({resourceAll} OR substr(json_extract(source.value,'$.ResourceId'),1,36) IN (SELECT value FROM json_each({resourceAgents}))
                OR json_extract(source.value,'$.ResourceId') IN (SELECT value FROM json_each({resourceChildren})))
             WHEN 'credential' THEN NOT ({credentialAll} OR json_extract(source.value,'$.ResourceId') IN (SELECT value FROM json_each({credentials})))
             WHEN 'binding' THEN NOT ({credentialAll} OR json_extract(source.value,'$.ResourceId') IN (SELECT value FROM json_each({credentials})))
             ELSE 1 END)
            """);
    }

}
