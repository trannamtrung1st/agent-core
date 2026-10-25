using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask<bool> HasSpaceWork(AgentCoreDbContext db, Guid tenantId, Guid spaceId, Guid? instanceId, CancellationToken ct)
    {
        var tenant = tenantId.ToString("D"); var space = spaceId.ToString("D"); var instance = instanceId?.ToString("D");
        var active = await db.Database.SqlQuery<int>($"""
            SELECT EXISTS(SELECT 1 FROM AgentRuns r WHERE lower(r.TenantId)={tenant}
              AND ({instance} IS NULL OR r.AgentInstanceId={instance})
              AND (json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.spaceId')={space}
                OR json_extract(r.PayloadJson,'$.admission.configuration.spaceContext.privateSpaceId')={space})
              AND (r.Status NOT IN ({(int)AgentRunStatus.Completed},{(int)AgentRunStatus.Failed},{(int)AgentRunStatus.Cancelled})
                OR json_extract(r.PayloadJson,'$.sideEffect.disposition') IN ({(int)AgentRunSideEffectDisposition.InFlight},{(int)AgentRunSideEffectDisposition.Indeterminate}))) AS Value
            """).SingleAsync(ct).ConfigureAwait(false);
        if (active != 0) return true;
        var now = (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        return await db.Database.SqlQuery<int>($"""
            SELECT EXISTS(SELECT 1 FROM TenantApprovalRequests a WHERE lower(a.TenantId)={tenant}
              AND ({instance} IS NULL OR a.InstanceId={instance}) AND json_type(a.PayloadJson,'$.Operation.ArtifactPublication')='object'
              AND a.Status IN ({(int)TenantApprovalStatus.Pending},{(int)TenantApprovalStatus.Approved}) AND a.ExpiresAt>{now}
              AND (json_extract(a.PayloadJson,'$.Operation.SpaceContext.SpaceId')={space}
                OR json_extract(a.PayloadJson,'$.Operation.SpaceContext.PrivateSpaceId')={space})
              AND NOT EXISTS(SELECT 1 FROM TenantOperationReceipts receipt WHERE receipt.OperationId=a.OperationId)) AS Value
            """).SingleAsync(ct).ConfigureAwait(false) != 0;
    }

    private async ValueTask RequireSpaceMemberQuiescent(AgentCoreDbContext db, Guid tenantId, Guid spaceId, Guid instanceId, CancellationToken ct)
    {
        if (await HasSpaceWork(db, tenantId, spaceId, instanceId, ct).ConfigureAwait(false))
            throw new SecurityConflictException("space_membership_has_active_work");
        var tenant = tenantId.ToString("D"); var space = spaceId.ToString("D"); var instance = instanceId.ToString("D");
        var mounted = await db.Database.SqlQuery<int>($"""
            SELECT EXISTS(SELECT 1 FROM SandboxSessionLeases WHERE AgentInstanceId={instance}
              AND State<>{(int)SandboxLeaseState.Terminated} AND (TenantId IS NULL OR (lower(TenantId)={tenant}
                AND (json_type(Json,'$.mountAuthority.workspaceAccess.context') IS NOT 'object'
                  OR json_extract(Json,'$.mountAuthority.workspaceAccess.context.spaceId')={space}
                  OR json_extract(Json,'$.mountAuthority.workspaceAccess.context.privateSpaceId')={space})))) AS Value
            """).SingleAsync(ct).ConfigureAwait(false);
        if (mounted != 0) throw new SecurityConflictException("space_membership_live_sandbox");
    }
}
