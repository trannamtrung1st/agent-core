using AgentCore.Domain.Security;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private static async Task PruneSpaceExclusion(AgentCoreDbContext db, Guid tenant, Guid instance, IReadOnlySet<string> removedKeys, AuthorizationAudit cause, CancellationToken ct)
    {
        if (removedKeys.Count == 0) return;
        var instanceId = instance.ToString("D"); var keys = removedKeys.ToArray();
        var rows = await db.Set<SpaceDisabledBindingRecord>().Where(d => d.TenantId == tenant && d.InstanceId == instanceId && keys.Contains(d.BindingKey)).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var group in rows.GroupBy(r => r.SpaceId))
        {
            var row = group.First();
            var member = await db.Set<SpaceMembershipRecord>().SingleAsync(m => m.TenantId == tenant && m.SpaceId == row.SpaceId && m.InstanceId == instanceId, ct).ConfigureAwait(false);
            var space = await db.Set<SpaceRecord>().SingleAsync(s => s.TenantId == tenant && s.SpaceId == row.SpaceId, ct).ConfigureAwait(false);
            db.Entry(member).CurrentValues.SetValues(member with { Revision = checked(member.Revision + 1), UpdatedAt = cause.At });
            db.Entry(space).CurrentValues.SetValues(space with { Revision = checked(space.Revision + 1), UpdatedAt = cause.At });
            db.RemoveRange(group);
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"AgentCore.SpaceReconciliation\0{cause.AuditId:D}\0{row.SpaceId:D}\0{instance:D}"));
            db.Add(new AuthorizationAudit(new Guid(hash.AsSpan(0, 16)), tenant, cause.ActorPrincipalId, "space-memberships.reconcile", row.SpaceId.ToString("D"),
                "allowed", "inherited_binding_removed", cause.At));
        }
    }
    private async Task ReconcileResourceSpaceBindings(AgentCoreDbContext db, Guid tenant, TenantAgentConfigurationChange change, AuthorizationAudit cause, CancellationToken ct)
    {
        var removed = TenantSpaceBindingReconciliation.RemovedResourceKeys(change,
            change.ActiveVersion is null ? null : await ChildCatalogAsync(db, tenant, change.InstanceId, null, ct).ConfigureAwait(false),
            change.ActiveVersion is null ? null : await ChildCatalogAsync(db, tenant, change.InstanceId, change, ct).ConfigureAwait(false));
        await PruneSpaceExclusion(db, tenant, change.InstanceId, removed, cause, ct).ConfigureAwait(false);
    }

}
