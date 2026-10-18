using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantBrowserEffectStart?> TryStartBrowserEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var old = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.OperationId == commit.OperationId, ct).ConfigureAwait(false);
        if (old is not null) return new(null, TenantLocalEffectRules.MatchingReceipt(old.Domain(), next.Tenant.TenantId, commit));
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Revision != expectedSecurityRevision) return null;
        var before = (await ReadTenantAsync(db, tenant.TenantId, ct).ConfigureAwait(false))!;
        var requestRow = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == commit.RequestId, ct).ConfigureAwait(false);
        var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == commit.RequestId, ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, tenant.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == commit.RunId.ToString("D"), ct).ConfigureAwait(false);
        if (requestRow is null || decision is null || row is null || row.Revision != commit.ExpectedRunRevision) return null;
        var request = requestRow.Domain(); var run = AgentRunStoreMapping.ToDomain(row);
        var sourceRow = request.RunId == run.AgentRunId ? null : await OwnedRuns(db, tenant.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false);
        var source = sourceRow is null ? null : AgentRunStoreMapping.ToDomain(sourceRow);
        var grant = await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == tenant.TenantId && g.RequestId == request.RequestId, ct).ConfigureAwait(false);
        TenantBrowserEffectRules.Start(request, decision, grant, run, commit, source);
        if (run.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate) return null;
        TenantBrowserEffectRules.Transaction(next, expectedSecurityRevision, request, audit, commit.Outcome == TenantOperationOutcome.Completed);
        var at = ApprovalNow;
        if (run.Claim!.LeaseExpiresAtUtc <= at) throw new SecurityConflictException("execution_claim_expired");
        var owned = await OwnedAgents(db, tenant.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct).ConfigureAwait(false);
        var agent = owned is null ? null : Owned(owned);
        var reason = commit.Outcome == TenantOperationOutcome.Denied ? commit.ReasonCode : request.ExpiresAt <= at ? "approval_expired" : null;
        if (reason is null)
        {
            try
            {
                if (agent is null) throw new AccessDeniedException("executor_unavailable");
                TenantApprovalRules.Validate(request, run, agent, source: source);
                await RequireBrowserOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantBrowserEffectRules.RequireAuthorityAsync(before, request, decision, run, agent, new IamAuthorizer(this), ct).ConfigureAwait(false);
                if ((await RootApproval(db, request.TenantId, request.RootActivationId.ToString("D"), request.ExecutorPrincipalId, request.Operation).AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false))?.RequestId != request.RequestId)
                    reason = "root_operation_conflict";
            }
            catch (AccessDeniedException e) { reason = TenantBrowserEffectRules.Denial(e.ReasonCode); }
            catch (SecurityConflictException e) { reason = TenantBrowserEffectRules.Denial(e.ReasonCode); }
        }
        var saved = reason is null ? run.BeginApprovedExternalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, at)
            : run.RecordApprovedLocalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, false, at);
        var receipt = reason is null ? null : TenantBrowserEffectRules.Receipt(request, run, TenantOperationOutcome.Denied, reason, at);
        audit = TenantOperationAuditRules.WithEffectResult(audit with { Action = reason is null ? TenantBrowserEffectRules.DispatchAction : TenantBrowserEffectRules.Action,
            At = at, Decision = reason is null ? "allowed" : "denied", ReasonCode = reason ?? "browser_command_dispatched" }, before, run, request, null);
        if (reason is null) audit = audit with { Execution = audit.Execution! with { Purpose = ExecutionAuthorizationPurpose.EffectEligibility } };
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        AgentRunStoreMapping.Apply(row, saved); if (receipt is not null) db.Add(TenantOperationReceiptRecord.From(receipt));
        db.Entry(tenant).CurrentValues.SetValues(next.Tenant); await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return new(reason is null ? TenantBrowserEffectRules.Fence(request, saved) : null, receipt); }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return null; }
    }
    public async ValueTask<IReadOnlyList<TenantBrowserEffectFence>> RecoverableBrowserEffectsAsync(DateTimeOffset at, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var candidates = await db.AgentRuns.FromSqlInterpolated($"""
            SELECT r.* FROM AgentRuns r JOIN TenantApprovalRequests a
                ON lower(a.RequestId) = json_extract(r.PayloadJson, '$.approvalRequestId') AND a.TenantId = r.TenantId
            WHERE a.Permission IN ('browser.read','browser.use')
                AND json_type(r.PayloadJson, '$.sideEffect.externalEffect') IS 'object'
                AND json_extract(r.PayloadJson, '$.sideEffect.disposition') IN (2,5)
                AND json_extract(r.PayloadJson, '$.sideEffect.toolCallId') = 'operation:' || lower(a.OperationId)
                AND json_extract(r.PayloadJson, '$.sideEffect.actionHash') = a.Fingerprint
                AND (r.Status <> {(int)AgentRunStatus.Running} OR r.LeaseExpiresAtUtc IS NULL OR r.LeaseExpiresAtUtc <= {at.ToUnixTimeMilliseconds()}
                    OR json_extract(r.PayloadJson, '$.sideEffect.disposition') = 5
                    OR json_extract(r.PayloadJson, '$.claim.generation') IS NOT json_extract(r.PayloadJson, '$.sideEffect.externalEffect.generation'))
                AND EXISTS (SELECT 1 FROM TenantSessions s WHERE s.TenantId = r.TenantId AND s.SessionId = r.SessionId
                    AND s.AgentInstanceId = r.AgentInstanceId AND s.ExecutorPrincipalId = r.ExecutorPrincipalId)
                AND NOT EXISTS (SELECT 1 FROM TenantOperationReceipts o WHERE o.OperationId = a.OperationId)
            """).AsNoTracking().OrderBy(r => r.UpdatedAtUtc).ThenBy(r => r.AgentRunId).Take(Math.Clamp(limit, 1, 100)).ToArrayAsync(ct).ConfigureAwait(false);
        var result = new List<TenantBrowserEffectFence>();
        foreach (var row in candidates)
        {
            var run = AgentRunStoreMapping.ToDomain(row);
            var request = (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleAsync(a => a.TenantId == row.TenantId && a.RequestId == run.ApprovalRequestId, ct).ConfigureAwait(false)).Domain();
            result.Add(TenantBrowserEffectRules.Fence(request, run));
        }
        return result;
    }
    public async ValueTask<TenantOperationReceipt?> TrySettleBrowserEffectAsync(TenantSecurityState next, long expectedSecurityRevision, TenantBrowserEffectFence fence,
        TenantOperationOutcome outcome, string reason, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default, TenantBrowserEffectObservation? observation = null)
    {
        next = IamStateRules.Freeze(next); TenantBrowserEffectRules.Outcome(outcome, reason, recovery);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var old = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.OperationId == fence.OperationId, ct).ConfigureAwait(false);
        if (old is not null) return TenantBrowserEffectRules.Matching(old.Domain(), fence);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == fence.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Revision != expectedSecurityRevision) return null;
        var before = (await ReadTenantAsync(db, tenant.TenantId, ct).ConfigureAwait(false))!;
        var requestRow = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == fence.RequestId, ct).ConfigureAwait(false);
        var row = await RetainedTenantOperationRuns(db, tenant.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == fence.RunId.ToString("D"), ct).ConfigureAwait(false);
        if (requestRow is null || row is null) return null;
        var request = requestRow.Domain(); var run = AgentRunStoreMapping.ToDomain(row);
        var sourceRow = request.RunId == run.AgentRunId ? null : await RetainedTenantOperationRuns(db, tenant.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false);
        TenantBrowserEffectRules.RequireFence(fence, request, run, sourceRow is null ? null : AgentRunStoreMapping.ToDomain(sourceRow));
        TenantBrowserEffectRules.Transaction(next, expectedSecurityRevision, request, audit, false);
        var at = ApprovalNow;
        if (at < fence.Origin.StartedAtUtc || recovery && !TenantBrowserEffectRules.Recoverable(run, at)) return null;
        var saved = TenantBrowserEffectRules.SettleRun(run, fence, outcome, at);
        if (observation is not null)
        {
            try
            {
                var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleAsync(d => d.TenantId == fence.TenantId && d.RequestId == request.RequestId, ct).ConfigureAwait(false);
                var owned = await OwnedAgents(db, fence.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct).ConfigureAwait(false);
                var agent = owned is null ? throw new AccessDeniedException("executor_unavailable") : Owned(owned);
                TenantApprovalRules.Validate(request, run, agent, source: sourceRow is null ? null : AgentRunStoreMapping.ToDomain(sourceRow));
                await RequireBrowserOperation(db, request, run, agent, ct).ConfigureAwait(false);
                await TenantBrowserEffectRules.RequireAuthorityAsync(before, request, decision, run, agent, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is AccessDeniedException or SecurityConflictException) { observation = null; }
        }
        saved = TenantBrowserContinuationProjection.Save(run, saved, request, outcome, reason, at, observation);
        var receipt = TenantBrowserEffectRules.Receipt(request, run, outcome, reason, at);
        audit = TenantOperationAuditRules.WithEffectResult(audit with { At = at, Decision = outcome switch { TenantOperationOutcome.Completed => "allowed", TenantOperationOutcome.Denied => "denied", _ => "indeterminate" }, ReasonCode = reason }, before, run, request, null);
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        if (saved.Revision != run.Revision) AgentRunStoreMapping.Apply(row, saved);
        db.Add(TenantOperationReceiptRecord.From(receipt)); db.Entry(tenant).CurrentValues.SetValues(next.Tenant);
        await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return receipt; }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return null; }
    }
}
