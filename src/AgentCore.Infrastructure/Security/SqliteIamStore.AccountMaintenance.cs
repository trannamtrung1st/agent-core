using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<bool> TryMaintainAccountEffectAsync(TenantSecurityState next, long expectedSecurityRevision, TenantAccountDispatch fence, long credentialRevision, long secretRevision,
        CredentialOAuthCheck maintenance, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Revision != expectedSecurityRevision) return false;
        var before = (await ReadTenantAsync(db, tenant.TenantId, ct).ConfigureAwait(false))!;
        var request = (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == fence.RequestId, ct).ConfigureAwait(false))?.Domain();
        var row = await OwnedRuns(db, tenant.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == fence.RunId.ToString("D"), ct).ConfigureAwait(false);
        var receipt = (await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.OperationId == fence.OperationId, ct).ConfigureAwait(false))?.Domain();
        var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == fence.RequestId, ct).ConfigureAwait(false);
        if (request?.Operation.AccountAction is null || row is null || receipt is not null || decision is null) return false;
        var run = AgentRunStoreMapping.ToDomain(row);
        var parent = await OwnedAgents(db, tenant.TenantId).AsNoTracking().SingleOrDefaultAsync(a => a.Instance.InstanceId == run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false);
        if (parent is null) return false; var agent = Owned(parent);
        var sourceRow = request.RunId == run.AgentRunId ? null : await OwnedRuns(db, request.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false);
        var source = sourceRow is null ? null : AgentRunStoreMapping.ToDomain(sourceRow);
        var bound = await RequireAccountActionOperation(db, request, run, agent, ct, source, exactSecret: false).ConfigureAwait(false);
        var session = await ReadSandboxSession(db, tenant.TenantId, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantRuntimeRules.Require(new(run.Owner, run.SessionId, run.AgentRunId, fence.Origin.Generation), session.Owner, run, OAuthNow);
        if (bound.Credential.Value.Revision != credentialRevision || bound.Credential.Value.SecretRevision != secretRevision) return false;
        var grant = await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == tenant.TenantId && g.RequestId == request.RequestId, ct).ConfigureAwait(false);
        TenantAccountEffectRules.RequireFence(fence, request, run, source, grant); TenantSessionRules.RequireExecutor(before, agent);
        await TenantExecutionLineage.RequireRequesterAsync(run, before, new IamAuthorizer(this), this, ct).ConfigureAwait(false);
        await TenantAccountEffectRules.RequireAuthorityAsync(before, request, decision, agent, new IamAuthorizer(this), grant, OperationGrantStatus.Claimed, OAuthNow, ct).ConfigureAwait(false);
        TenantAccountMaintenanceRules.Transaction(next, expectedSecurityRevision, fence, request, maintenance, audit);
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        await EnsureActiveAccountOwnerAsync(db, next, null, ct).ConfigureAwait(false);
        if (maintenance.RotatedProtectedPayload is not null || maintenance.ReconnectRequired)
        {
            var credential = await OAuthCredentialAsync(db, request.TenantId, bound.Credential.Value.CredentialId, ct).ConfigureAwait(false);
            if (credential is null || credential.Revision != credentialRevision || credential.SecretRevision != secretRevision) return false;
            SqliteCredentialStore.StageValue(db, credential, TenantCredentialOAuthRules.Checked(SqliteCredentialStore.Map(credential), maintenance, OAuthNow), request.TenantId);
            if (maintenance.ReconnectRequired) await SupersedeOAuthAsync(db, request.TenantId, bound.Credential.Value.CredentialId, OAuthNow, ct).ConfigureAwait(false);
        }
        TenantRuntimeRules.Require(new(run.Owner, run.SessionId, run.AgentRunId, fence.Origin.Generation), session.Owner, run, OAuthNow);
        audit = TenantOperationAuditRules.WithEffectResult(audit, before, run, request, grant?.GrantId);
        db.Entry(tenant).CurrentValues.SetValues(next.Tenant); await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return false; }
        catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6) { return false; }
    }
}
