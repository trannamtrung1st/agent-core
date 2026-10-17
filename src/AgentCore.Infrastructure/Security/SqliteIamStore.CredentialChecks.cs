using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantCredentialCheckStore
{
    private static IQueryable<AgentRunRecord> RetainedTenantOperationRuns(AgentCoreDbContext db, Guid tenant) => db.AgentRuns.Where(r => r.TenantId == tenant
        && db.Set<TenantSessionRecord>().Any(s => s.TenantId == tenant && s.SessionId == r.SessionId && s.AgentInstanceId == r.AgentInstanceId && s.ExecutorPrincipalId == r.ExecutorPrincipalId));
    public async ValueTask<TenantCredentialCheckDispatch?> FindCredentialCheckDispatchAsync(Guid tenantId, Guid operationId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.Set<TenantCredentialCheckDispatchRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.OperationId == operationId, ct).ConfigureAwait(false))?.Domain();
    }
    public async ValueTask<IReadOnlyList<TenantCredentialCheckDispatch>> RecoverableCredentialChecksAsync(DateTimeOffset at, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await (from d in db.Set<TenantCredentialCheckDispatchRecord>().AsNoTracking()
            join r in db.AgentRuns.AsNoTracking() on d.RunId equals r.AgentRunId
            where d.CompletedAt == null && (r.Status != (int)AgentRunStatus.Running || r.LeaseExpiresAtUtc == null || r.LeaseExpiresAtUtc <= at.ToUnixTimeMilliseconds())
            orderby d.StartedAt, d.OperationId select d).Take(Math.Clamp(limit, 1, 100)).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(r => r.Domain()).ToArray();
    }

    public async ValueTask<TenantCredentialCheckStart?> TryStartCredentialCheckAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var old = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.OperationId == commit.OperationId, ct).ConfigureAwait(false);
        if (old is not null) return new(null, TenantLocalEffectRules.MatchingReceipt(old.Domain(), next.Tenant.TenantId, commit));
        if (await db.Set<TenantCredentialCheckDispatchRecord>().AnyAsync(r => r.OperationId == commit.OperationId, ct).ConfigureAwait(false)) return null;
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Revision != expectedSecurityRevision) return null;
        var before = (await ReadTenantAsync(db, tenant.TenantId, ct).ConfigureAwait(false))!;
        var requestRow = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == commit.RequestId, ct).ConfigureAwait(false);
        var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == commit.RequestId, ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, tenant.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == commit.RunId.ToString("D"), ct).ConfigureAwait(false);
        if (requestRow is null || decision is null || row is null || row.Revision != commit.ExpectedRunRevision) return null;
        var request = requestRow.Domain(); var run = AgentRunStoreMapping.ToDomain(row);
        var sourceRow = request.RunId == run.AgentRunId ? null : await OwnedRuns(db, request.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false);
        var source = sourceRow is null ? null : AgentRunStoreMapping.ToDomain(sourceRow);
        var grant = await db.Set<TenantOperationGrant>().SingleOrDefaultAsync(g => g.TenantId == tenant.TenantId && g.RequestId == request.RequestId, ct).ConfigureAwait(false);
        TenantCredentialCheckRules.Start(request, decision, grant, run, commit, source);
        TenantCredentialCheckRules.Transaction(next, expectedSecurityRevision, request, audit, commit.Outcome == TenantOperationOutcome.Completed);
        var at = ApprovalNow;
        if (run.Claim!.LeaseExpiresAtUtc <= at) throw new SecurityConflictException("execution_claim_expired");
        var owned = await OwnedAgents(db, tenant.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct).ConfigureAwait(false);
        var agent = owned is null ? null : Owned(owned);
        var reason = commit.Outcome == TenantOperationOutcome.Denied ? commit.ReasonCode : request.ExpiresAt <= at ? "approval_expired"
            : grant is not null && grant.ExpiresAt <= at ? "grant_expired" : null;
        if (reason is null)
        {
            TenantCredentialBinding? binding = null;
            try { binding = await CredentialOperationTarget(db, request, ct).ConfigureAwait(false); }
            catch (AccessDeniedException e) { reason = e.ReasonCode; }
            reason ??= TenantCredentialCheckRules.TargetReason(request, run, agent, binding, source);
        }
        if (reason is null && await db.Set<TenantCredentialCheckDispatchRecord>().AnyAsync(d => d.TenantId == request.TenantId
            && d.CredentialId == request.Operation.CredentialCheck!.CredentialId.ToString("D") && d.CompletedAt == null, ct).ConfigureAwait(false)) reason = "credential_check_in_progress";
        if (reason is null)
        {
            try { TenantSessionRules.RequireExecutor(before, agent!); await ValidateTenantActorAsync(db, before, request.ExecutorPrincipalId, ct).ConfigureAwait(false); }
            catch (AccessDeniedException) { reason = "executor_unavailable"; }
            try { await ValidateTenantActorAsync(db, before, decision.HumanPrincipalId, ct).ConfigureAwait(false); }
            catch (AccessDeniedException) { reason ??= "grantor_authority_lost"; }
            if (request.OriginalInitiatorPrincipalId is { } requester)
                try { await ValidateTenantActorAsync(db, before, requester, ct).ConfigureAwait(false); } catch (AccessDeniedException) { reason ??= "requester_unavailable"; }
            if (before.Tenant.Status != IdentityStatus.Active) reason = "tenant_unavailable";
            var root = await RootApproval(db, request.TenantId, request.RootActivationId.ToString("D"), request.ExecutorPrincipalId, request.Operation).AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (root?.RequestId != request.RequestId) reason ??= "root_operation_conflict";
        }
        var started = reason is null ? run.BeginApprovedExternalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, at)
            : run.RecordApprovedLocalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, false, at);
        var dispatch = reason is null ? TenantCredentialCheckRules.Dispatch(request, started, at) : null;
        var receipt = reason is null ? null : TenantCredentialCheckRules.Receipt(request, run, agent, TenantOperationOutcome.Denied, reason, at);
        audit = audit with { Action = dispatch is null ? TenantCredentialCheckRules.Action : TenantCredentialCheckRules.DispatchAction,
            At = at, Decision = reason is null ? "allowed" : "denied", ReasonCode = reason ?? "credential_check_dispatched" };
        audit = TenantOperationAuditRules.WithEffectResult(audit, before, run, request, grant?.GrantId);
        if (dispatch is not null) audit = audit with { Execution = audit.Execution! with { Purpose = ExecutionAuthorizationPurpose.EffectEligibility } };
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        AgentRunStoreMapping.Apply(row, started);
        if (grant is not null) db.Entry(grant).CurrentValues.SetValues(grant with { Status = dispatch is null ? OperationGrantStatus.Revoked : OperationGrantStatus.Claimed, Revision = checked(grant.Revision + 1) });
        if (dispatch is not null) db.Add(TenantCredentialCheckDispatchRecord.From(dispatch));
        if (receipt is not null) db.Add(TenantOperationReceiptRecord.From(receipt));
        db.Entry(tenant).CurrentValues.SetValues(next.Tenant); await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return new(dispatch, receipt); }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return null; }
    }

    public async ValueTask<TenantOperationReceipt?> TrySettleCredentialCheckAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantCredentialCheckDispatch dispatch, CredentialOAuthCheck? check, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var old = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.OperationId == dispatch.OperationId, ct).ConfigureAwait(false);
        if (old is not null) return CredentialCheckReceipt(old.Domain(), dispatch);
        var ledger = await db.Set<TenantCredentialCheckDispatchRecord>().SingleOrDefaultAsync(r => r.OperationId == dispatch.OperationId, ct).ConfigureAwait(false);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == dispatch.TenantId, ct).ConfigureAwait(false);
        if (ledger?.Domain() != dispatch || dispatch.CompletedAt is not null || tenant is null || tenant.Revision != expectedSecurityRevision) return null;
        var before = (await ReadTenantAsync(db, tenant.TenantId, ct).ConfigureAwait(false))!;
        var requestRow = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == dispatch.RequestId, ct).ConfigureAwait(false);
        var row = await RetainedTenantOperationRuns(db, tenant.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == dispatch.RunId.ToString("D"), ct).ConfigureAwait(false);
        if (requestRow is null || row is null) return null;
        var request = requestRow.Domain(); var run = AgentRunStoreMapping.ToDomain(row);
        var sourceRow = request.RunId == run.AgentRunId ? null : await RetainedTenantOperationRuns(db, tenant.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false);
        var grant = await db.Set<TenantOperationGrant>().SingleOrDefaultAsync(g => g.TenantId == tenant.TenantId && g.RequestId == request.RequestId, ct).ConfigureAwait(false);
        TenantCredentialCheckRules.RequireDispatch(dispatch, request, run, grant, sourceRow is null ? null : AgentRunStoreMapping.ToDomain(sourceRow));
        TenantCredentialCheckRules.Transaction(next, expectedSecurityRevision, request, audit, false);
        var at = ApprovalNow;
        if (at < dispatch.StartedAt || recovery && (!TenantCredentialCheckRules.Recoverable(dispatch, run, at) || check is not null)) return null;
        var credential = await OwnedCredentials(db, tenant.TenantId).Include(c => c.EmailAccount).Include(c => c.DriveAccount)
            .SingleOrDefaultAsync(c => c.CredentialId == dispatch.CredentialId.ToString("D"), ct).ConfigureAwait(false);
        var value = credential is null ? null : SqliteCredentialStore.Map(credential);
        var result = TenantCredentialCheckRules.Completion(request, value, check, at);
        var owned = await OwnedAgents(db, tenant.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == dispatch.InstanceId.ToString("D"), ct).ConfigureAwait(false);
        var agent = owned is null ? null : Owned(owned);
        var receipt = TenantCredentialCheckRules.Receipt(request, run, agent, result.Outcome, result.Reason, at);
        var saved = TenantCredentialCheckRules.SettleRun(run, dispatch, result.Outcome, at);
        audit = TenantOperationAuditRules.WithEffectResult(audit with { At = at, Decision = TenantCredentialCheckRules.AuditDecision(result.Outcome), ReasonCode = result.Reason }, before, run, request, grant?.GrantId);
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        if (result.Apply is { } apply)
        {
            SqliteCredentialStore.StageValue(db, credential!, TenantCredentialOAuthRules.Checked(value!, apply, at), tenant.TenantId);
            if (apply.ReconnectRequired) await SupersedeOAuthAsync(db, tenant.TenantId, dispatch.CredentialId, at, ct).ConfigureAwait(false);
        }
        if (saved.Revision != run.Revision) AgentRunStoreMapping.Apply(row, saved);
        if (grant is not null) db.Entry(grant).CurrentValues.SetValues(grant with { Status = result.Outcome == TenantOperationOutcome.Completed ? OperationGrantStatus.Consumed : OperationGrantStatus.Indeterminate, Revision = checked(grant.Revision + 1) });
        db.Entry(ledger!).CurrentValues.SetValues(TenantCredentialCheckDispatchRecord.From(dispatch with { CompletedAt = TenantCredentialCheckRules.Milliseconds(at) }));
        db.Add(TenantOperationReceiptRecord.From(receipt)); db.Entry(tenant).CurrentValues.SetValues(next.Tenant);
        await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return receipt; }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return null; }
    }

    private static TenantOperationReceipt CredentialCheckReceipt(TenantOperationReceipt receipt, TenantCredentialCheckDispatch dispatch)
    {
        if (receipt.OperationId != dispatch.OperationId || receipt.TenantId != dispatch.TenantId || receipt.RequestId != dispatch.RequestId
            || receipt.RunId != dispatch.RunId || receipt.SessionId != dispatch.SessionId || receipt.RootActivationId != dispatch.RootActivationId
            || receipt.ExecutorPrincipalId != dispatch.ExecutorPrincipalId || receipt.InstanceId != dispatch.InstanceId || receipt.ActionHash != dispatch.ActionHash)
            throw new SecurityConflictException("operation_mismatch");
        return receipt;
    }
}
