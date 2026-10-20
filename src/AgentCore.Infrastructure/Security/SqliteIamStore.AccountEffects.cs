using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantAccountOperationStore
{
    public async ValueTask<TenantAccountEffectStart?> TryStartAccountEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
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
        var grant = await db.Set<TenantOperationGrant>().SingleOrDefaultAsync(g => g.TenantId == tenant.TenantId && g.RequestId == request.RequestId, ct).ConfigureAwait(false);
        TenantAccountEffectRules.Start(request, decision, grant, run, commit, source);
        if (run.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate) return null;
        TenantAccountEffectRules.Transaction(next, expectedSecurityRevision, request, audit, commit.Outcome == TenantOperationOutcome.Completed);
        var at = ApprovalNow;
        if (run.Claim!.LeaseExpiresAtUtc <= at) throw new SecurityConflictException("execution_claim_expired");
        var owned = await OwnedAgents(db, tenant.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct).ConfigureAwait(false);
        var agent = owned is null ? null : Owned(owned);
        var reason = commit.Outcome == TenantOperationOutcome.Denied ? commit.ReasonCode : request.ExpiresAt <= at ? "approval_expired"
            : grant?.ExpiresAt <= at ? "grant_expired" : null;
        if (reason is null)
        {
            try
            {
                if (agent is null) throw new AccessDeniedException("executor_unavailable");
                TenantApprovalRules.Validate(request, run, agent, source: source);
                await RequireAccountActionOperation(db, request, run, agent, ct, source).ConfigureAwait(false);
                await TenantAccountEffectRules.RequireAuthorityAsync(before, request, decision, agent, new IamAuthorizer(this), grant, OperationGrantStatus.Available, at, ct).ConfigureAwait(false);
                if ((await RootApproval(db, request.TenantId, request.RootActivationId.ToString("D"), request.ExecutorPrincipalId, request.Operation).AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false))?.RequestId != request.RequestId)
                    reason = "root_operation_conflict";
            }
            catch (AccessDeniedException e) { reason = TenantAccountEffectRules.Denial(e.ReasonCode); }
            catch (SecurityConflictException e) { reason = TenantAccountEffectRules.Denial(e.ReasonCode); }
        }
        var localDraft = reason is null && request.Operation.AccountAction!.ToolName == AgentCore.Application.Tools.ToolCatalog.EmailCreateDraft;
        var saved = localDraft ? run.RecordApprovedLocalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, true, at)
            : reason is null ? run.BeginApprovedExternalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, at)
            : run.RecordApprovedLocalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, false, at);
        var receipt = localDraft ? TenantAccountEffectRules.Receipt(request, run, TenantOperationOutcome.Completed, TenantAccountEffectRules.Completed, at)
            : reason is null ? null : TenantAccountEffectRules.Receipt(request, run, TenantOperationOutcome.Denied, reason, at);
        audit = TenantOperationAuditRules.WithEffectResult(audit with { Action = reason is null && !localDraft ? TenantAccountEffectRules.DispatchAction : TenantAccountEffectRules.ResultAction,
            At = at, Decision = reason is null ? "allowed" : "denied", ReasonCode = localDraft ? TenantAccountEffectRules.Completed : reason ?? "account_action_dispatched" }, before, run, request, grant?.GrantId);
        if (reason is null && !localDraft) audit = audit with { Execution = audit.Execution! with { Purpose = ExecutionAuthorizationPurpose.EffectEligibility } };
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        if (localDraft)
        {
            var action = request.Operation.AccountAction!;
            var existingDraft = await db.Set<EmailDraftRecord>().AsNoTracking().SingleOrDefaultAsync(d => d.DraftId == action.Draft!.DraftId, ct).ConfigureAwait(false);
            if (existingDraft is not null) TenantAccountActionRules.RequireDraft(action, request.Operation.InstanceId,
                new(Guid.Parse(existingDraft.AgentInstanceId), Guid.Parse(existingDraft.SessionId), System.Text.Json.JsonSerializer.Deserialize<EmailDraftSnapshot>(existingDraft.DraftJson)!,
                    DateTimeOffset.FromUnixTimeMilliseconds(existingDraft.CreatedAtUtc)));
            else db.Add(new EmailDraftRecord { DraftId = action.Draft!.DraftId, AgentInstanceId = request.Operation.InstanceId.ToString("D"), SessionId = request.SessionId.ToString("D"),
                DraftJson = System.Text.Json.JsonSerializer.Serialize(TenantAccountActionRules.DraftSnapshot(action)), CreatedAtUtc = at.ToUnixTimeMilliseconds() });
        }
        AgentRunStoreMapping.Apply(row, saved); if (receipt is not null) db.Add(TenantOperationReceiptRecord.From(receipt));
        if (grant is not null) db.Entry(grant).CurrentValues.SetValues(grant with { Status = localDraft ? OperationGrantStatus.Consumed : reason is null ? OperationGrantStatus.Claimed : OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) });
        db.Entry(tenant).CurrentValues.SetValues(next.Tenant); await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return new(reason is null && !localDraft ? TenantAccountEffectRules.Fence(request, saved) : null, receipt); }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return null; }
    }
    public async ValueTask<IReadOnlyList<TenantAccountDispatch>> RecoverableAccountEffectsAsync(DateTimeOffset at, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var candidates = await db.AgentRuns.FromSqlInterpolated($"""
            SELECT r.* FROM AgentRuns r JOIN TenantApprovalRequests a
                ON lower(a.RequestId) = json_extract(r.PayloadJson, '$.approvalRequestId') AND a.TenantId = r.TenantId
            WHERE json_type(a.PayloadJson, '$.Operation.AccountAction') IS 'object'
                AND json_type(r.PayloadJson, '$.sideEffect.externalEffect') IS 'object'
                AND json_type(r.PayloadJson, '$.sideEffect.externalEffect.directOperation') IS NULL
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
        var result = new List<TenantAccountDispatch>();
        foreach (var row in candidates)
        {
            var run = AgentRunStoreMapping.ToDomain(row);
            var request = (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleAsync(a => a.TenantId == row.TenantId && a.RequestId == run.ApprovalRequestId, ct).ConfigureAwait(false)).Domain();
            result.Add(TenantAccountEffectRules.Fence(request, run));
        }
        return result;
    }
    public async ValueTask<TenantOperationReceipt?> TrySettleAccountEffectAsync(TenantSecurityState next, long expectedSecurityRevision, TenantAccountDispatch fence,
        TenantOperationOutcome outcome, string reason, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default, TenantAccountActionResult? result = null)
    {
        next = IamStateRules.Freeze(next); TenantAccountEffectRules.Outcome(outcome, reason, recovery);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var old = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.OperationId == fence.OperationId, ct).ConfigureAwait(false);
        if (old is not null) return TenantAccountEffectRules.Matching(old.Domain(), fence);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == fence.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Revision != expectedSecurityRevision) return null;
        var before = (await ReadTenantAsync(db, tenant.TenantId, ct).ConfigureAwait(false))!;
        var requestRow = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId && r.RequestId == fence.RequestId, ct).ConfigureAwait(false);
        var row = await RetainedTenantOperationRuns(db, tenant.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == fence.RunId.ToString("D"), ct).ConfigureAwait(false);
        if (requestRow is null || row is null) return null;
        var request = requestRow.Domain(); var run = AgentRunStoreMapping.ToDomain(row);
        var sourceRow = request.RunId == run.AgentRunId ? null : await RetainedTenantOperationRuns(db, tenant.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false);
        var grant = await db.Set<TenantOperationGrant>().SingleOrDefaultAsync(g => g.TenantId == tenant.TenantId && g.RequestId == request.RequestId, ct).ConfigureAwait(false);
        TenantAccountEffectRules.RequireFence(fence, request, run, sourceRow is null ? null : AgentRunStoreMapping.ToDomain(sourceRow), grant);
        TenantAccountEffectRules.Transaction(next, expectedSecurityRevision, request, audit, false);
        var at = ApprovalNow;
        if (at < fence.Origin.StartedAtUtc || recovery && !TenantAccountEffectRules.Recoverable(run, at)) return null;
        if (result is not null)
        {
            try
            {
                var parent = await OwnedAgents(db, request.TenantId).AsNoTracking().SingleOrDefaultAsync(a => a.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct).ConfigureAwait(false)
                    ?? throw new AccessDeniedException("executor_unavailable");
                var agent = Owned(parent);
                await RequireAccountActionOperation(db, request, run, agent, ct, sourceRow is null ? null : AgentRunStoreMapping.ToDomain(sourceRow), exactSecret: false).ConfigureAwait(false);
                var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == request.TenantId && d.RequestId == request.RequestId, ct).ConfigureAwait(false)
                    ?? throw new AccessDeniedException("approval_unavailable");
                await TenantAccountEffectRules.RequireAuthorityAsync(before, request, decision, agent, new IamAuthorizer(this), grant, OperationGrantStatus.Claimed, at, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is AccessDeniedException or SecurityConflictException) { result = null; }
        }
        var saved = TenantAccountEffectRules.SettleRun(run, fence, outcome, at);
        saved = TenantAccountActionContinuationProjection.Save(run, saved, request, outcome, reason, at, result);
        var receipt = TenantAccountEffectRules.Receipt(request, run, outcome, reason, at);
        audit = TenantOperationAuditRules.WithEffectResult(audit with { At = at, Decision = outcome switch { TenantOperationOutcome.Completed => "allowed", TenantOperationOutcome.Denied => "denied", _ => "indeterminate" }, ReasonCode = reason }, before, run, request, grant?.GrantId);
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        if (saved.Revision != run.Revision) AgentRunStoreMapping.Apply(row, saved);
        if (grant is not null) db.Entry(grant).CurrentValues.SetValues(grant with { Status = outcome switch {
            TenantOperationOutcome.Completed => OperationGrantStatus.Consumed, TenantOperationOutcome.Denied => OperationGrantStatus.Revoked, _ => OperationGrantStatus.Indeterminate }, Revision = checked(grant.Revision + 1) });
        db.Add(TenantOperationReceiptRecord.From(receipt)); db.Entry(tenant).CurrentValues.SetValues(next.Tenant);
        await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return receipt; }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return null; }
    }
}
