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
    private async ValueTask RequireAccountReadSources(AgentCoreDbContext db, AgentRun run, CancellationToken ct)
    {
        foreach (var reference in TenantAccountReadReferences.Read(run))
        {
            var tenant = run.Owner.TenantId!.Value; var state = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!; var iam = new IamAuthorizer(this);
            var request = (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.RequestId == reference.RequestId, ct).ConfigureAwait(false))?.Domain();
            var receipt = (await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.OperationId == reference.OperationId, ct).ConfigureAwait(false))?.Domain();
            var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.RequestId == reference.RequestId, ct).ConfigureAwait(false);
            if (request?.Operation.AccountRead is null || receipt is null || decision is null) throw new AccessDeniedException("account_read_unavailable");
            var parent = await OwnedAgents(db, tenant).AsNoTracking().SingleOrDefaultAsync(a => a.Instance.InstanceId == run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false);
            if (parent is null) throw new AccessDeniedException("executor_unavailable"); var agent = Owned(parent);
            var bound = await RequireAccountReadOperation(db, request, run, agent, ct, exactSecret: false).ConfigureAwait(false);
            TenantAccountReadReferences.Require(reference, request, bound, agent);
            var grant = await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == tenant && g.RequestId == reference.RequestId, ct).ConfigureAwait(false);
            TenantAccountReadRules.Receipt(request, run, receipt, decision, grant);
            await RequireTenantExecutionSourcesAsync(db, run, state, iam, this, ct).ConfigureAwait(false);
            await TenantAccountReadRules.ReviewerAsync(state, request, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false);
        }
    }
    private async ValueTask<TenantCredentialBinding> RequireAccountReadOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run,
        TenantAgent agent, CancellationToken ct, bool exactSecret = true)
    {
        var read = request.Operation.AccountRead!;
        var session = await ReadSandboxSession(db, request.TenantId, read.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var bound = await CredentialOperationTarget(db, request.TenantId, agent.Instance.InstanceId, read.Credential.BindingId, read.Credential.CredentialId, ct).ConfigureAwait(false);
        TenantAccountReadRules.Require(read, run, agent, session, await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), bound, exactSecret);
        if (read.Source is { } original)
        {
            var originalRequest = (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == request.TenantId && r.RequestId == original.RequestId, ct).ConfigureAwait(false))?.Domain();
            var originalReceipt = (await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == request.TenantId && r.OperationId == original.OperationId, ct).ConfigureAwait(false))?.Domain();
            var originalRun = await OwnedRuns(db, request.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == original.RunId.ToString("D"), ct).ConfigureAwait(false);
            TenantAccountOperationReadRules.Require(read, request.TenantId, agent.Instance.InstanceId, agent.PrincipalId, originalRequest, originalReceipt,
                originalRun is null ? null : AgentRunStoreMapping.ToDomain(originalRun), await ReadSandboxSession(db, request.TenantId, original.SessionId, ct).ConfigureAwait(false));
        }
        await TenantAccountReadRules.PermissionsAsync((await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!, read, agent.PrincipalId,
            new IamAuthorizer(this), request.Kind == ApprovalKind.Review, ct).ConfigureAwait(false);
        if (request.Snapshot?.Credential != TenantAccountReadRules.Snapshot(agent, bound).Credential)
            throw new SecurityConflictException("account_source_changed");
        return bound;
    }
    public async ValueTask<bool> TryCommitRuntimeAccountReadAsync(TenantSecurityState next, long expectedSecurityRevision, TenantRuntimeAccountReadProof proof,
        CredentialOAuthCheck? maintenance, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Revision != expectedSecurityRevision) return false;
        var before = (await ReadTenantAsync(db, tenant.TenantId, ct).ConfigureAwait(false))!;
        var request = (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == proof.RequestId, ct).ConfigureAwait(false))?.Domain();
        var row = await OwnedRuns(db, tenant.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == proof.Frame.RunId.ToString("D"), ct).ConfigureAwait(false);
        var receipt = (await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.OperationId == proof.OperationId, ct).ConfigureAwait(false))?.Domain();
        var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == proof.RequestId, ct).ConfigureAwait(false);
        if (request?.Operation.AccountRead is null || row is null || receipt is null || decision is null) return false;
        var run = AgentRunStoreMapping.ToDomain(row);
        var parent = await OwnedAgents(db, tenant.TenantId).AsNoTracking().SingleOrDefaultAsync(a => a.Instance.InstanceId == run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false);
        if (parent is null) return false; var agent = Owned(parent);
        var bound = await RequireAccountReadOperation(db, request, run, agent, ct, exactSecret: false).ConfigureAwait(false);
        var session = await ReadSandboxSession(db, tenant.TenantId, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantRuntimeRules.Require(proof.Frame, session.Owner, run, OAuthNow);
        if (run.Revision != proof.RunRevision || bound.Credential.Value.Revision != proof.CredentialRevision || bound.Credential.Value.SecretRevision != proof.SecretRevision) return false;
        var grant = await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == tenant.TenantId && g.RequestId == request.RequestId, ct).ConfigureAwait(false);
        TenantAccountReadRules.Receipt(request, run, receipt, decision, grant); TenantSessionRules.RequireExecutor(before, agent);
        await RequireTenantExecutionSourcesAsync(db, run, before, new IamAuthorizer(this), this, ct).ConfigureAwait(false);
        await TenantAccountReadRules.ReviewerAsync(before, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
        TenantRuntimeAccountReadRules.Transaction(next, expectedSecurityRevision, proof, request, maintenance, audit);
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        await EnsureActiveAccountOwnerAsync(db, next, null, ct).ConfigureAwait(false);
        if (maintenance is not null)
        {
            var credential = await OAuthCredentialAsync(db, request.TenantId, bound.Credential.Value.CredentialId, ct).ConfigureAwait(false);
            if (credential is null || credential.Revision != proof.CredentialRevision || credential.SecretRevision != proof.SecretRevision) return false;
            SqliteCredentialStore.StageValue(db, credential, TenantCredentialOAuthRules.Checked(SqliteCredentialStore.Map(credential), maintenance, OAuthNow), request.TenantId);
            if (maintenance.ReconnectRequired) await SupersedeOAuthAsync(db, request.TenantId, bound.Credential.Value.CredentialId, OAuthNow, ct).ConfigureAwait(false);
        }
        TenantRuntimeRules.Require(proof.Frame, session.Owner, run, OAuthNow);
        audit = TenantOperationAuditRules.WithEffectResult(audit, before, run, request, grant?.GrantId);
        db.Entry(tenant).CurrentValues.SetValues(next.Tenant); await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return false; }
        catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6) { return false; }
    }
}
