using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<SandboxLease?> TryRequestTenantSandboxResetAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSandboxReset reset, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        var tenant = next.Tenant.TenantId; TenantSandboxControlRules.Audit(tenant, reset, human, audit); SandboxLease? requested = null;
        bool written;
        try
        {
            written = await WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
            {
                await RequireAgentHumanAsync(db, tenant, human.PrincipalId, human, ct).ConfigureAwait(false);
                var session = await ReadSandboxSession(db, tenant, reset.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
                var row = await db.Set<SandboxLeaseRow>().SingleOrDefaultAsync(l => l.TenantId == tenant && l.SessionId == reset.SessionId.ToString("D"), ct).ConfigureAwait(false)
                    ?? throw new AccessDeniedException("resource_unavailable");
                var lease = SandboxRuntimePersistence.Value(row);
                var state = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!;
                await TenantSandboxControlRules.RequirePermissionsAsync(state, session.Owner, lease.Profile.Id, human, new IamAuthorizer(this), ct).ConfigureAwait(false);
                TenantSandboxControlRules.Require(reset, session, lease); requested = TenantSandboxControlRules.Apply(lease);
                db.Entry(row).CurrentValues.SetValues(SandboxRuntimePersistence.Row(requested)); return true;
            }, ct).ConfigureAwait(false);
        }
        catch (AccessDeniedException error) when (error.ReasonCode is "principal_unavailable" or "account_unavailable")
        { throw new AccessDeniedException("initiator_unavailable"); }
        return written ? requested : null;
    }
    public async ValueTask<TenantSandboxRuntimeSnapshot?> TryRequestTenantSandboxProcessStopAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSandboxProcessStop stop, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        var tenant = next.Tenant.TenantId; TenantSandboxControlRules.Audit(tenant, stop, human, audit); TenantSandboxRuntimeSnapshot? requested = null;
        bool written;
        try
        {
            written = await WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
            {
                await RequireAgentHumanAsync(db, tenant, human.PrincipalId, human, ct).ConfigureAwait(false);
                var session = await ReadSandboxSession(db, tenant, stop.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
                var row = await db.Set<SandboxLeaseRow>().SingleOrDefaultAsync(l => l.TenantId == tenant && l.SessionId == stop.SessionId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
                var receipt = await db.Set<SandboxExecutionRow>().SingleOrDefaultAsync(e => e.TenantId == tenant && e.SessionId == row.SessionId && e.Id == stop.ExecutionId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
                var lease = SandboxRuntimePersistence.Value(row); var execution = SandboxRuntimePersistence.Value(receipt);
                var state = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!;
                await TenantSandboxControlRules.RequirePermissionsAsync(state, session.Owner, lease.Profile.Id, human, new IamAuthorizer(this), ct).ConfigureAwait(false);
                TenantSandboxControlRules.Require(stop, session, lease, execution);
                var pending = lease with { Revision = checked(lease.Revision + 1), SafeMessage = "Stop requested. Process cleanup is pending; canonical files are retained." };
                var stopped = TenantSandboxControlRules.ApplyStop(execution, audit);
                db.Entry(row).CurrentValues.SetValues(SandboxRuntimePersistence.Row(pending)); db.Entry(receipt).CurrentValues.SetValues(SandboxRuntimePersistence.Row(stopped));
                requested = new(session.Owner, session.Snapshot.Revision, pending, stopped); return true;
            }, ct).ConfigureAwait(false);
        }
        catch (AccessDeniedException error) when (error.ReasonCode is "principal_unavailable" or "account_unavailable")
        { throw new AccessDeniedException("initiator_unavailable"); }
        return written ? requested : null;
    }
    public async ValueTask<SandboxExecution?> TryStartTenantSandboxProcessStopAsync(Guid tenant, Guid session, Guid execution, Guid stopRequestId,
        long expectedExecutionRevision, long expectedLeaseRevision, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var root = await db.Set<TenantSessionRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenant && s.SessionId == session.ToString("D"), ct).ConfigureAwait(false);
        var lease = await db.Set<SandboxLeaseRow>().AsNoTracking().SingleOrDefaultAsync(l => l.TenantId == tenant && l.SessionId == session.ToString("D"), ct).ConfigureAwait(false);
        var row = await db.Set<SandboxExecutionRow>().SingleOrDefaultAsync(e => e.TenantId == tenant && e.SessionId == session.ToString("D") && e.Id == execution.ToString("D"), ct).ConfigureAwait(false);
        if (root is null || lease is null || row is null || root.ExecutorPrincipalId != row.ExecutorPrincipalId) return null;
        var claimed = TenantSandboxControlRules.StartStop(SandboxRuntimePersistence.Value(lease), SandboxRuntimePersistence.Value(row), stopRequestId, expectedExecutionRevision, expectedLeaseRevision, at);
        if (claimed is null) return null;
        db.Entry(row).CurrentValues.SetValues(SandboxRuntimePersistence.Row(claimed)); return await SaveSandbox(db, tx, ct).ConfigureAwait(false) ? claimed : null;
    }

}
