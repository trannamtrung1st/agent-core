using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantWorkspaceOperationStore
{
    public async ValueTask<TenantWorkspaceEffectStart?> TryStartWorkspaceEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantWorkspaceEffectCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, next.Tenant.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == commit.RunId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        var run = AgentRunStoreMapping.ToDomain(row);
        var old = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == next.Tenant.TenantId && r.RunId == row.AgentRunId && r.SourceCallId == commit.Operation.SourceCallId, ct).ConfigureAwait(false);
        if (old is not null) return new(null, TenantWorkspaceEffectRules.Match(old.Domain(), run, commit.Operation.SourceCallId, commit.Operation.ParametersHash));
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Revision != expectedSecurityRevision || run.Revision != commit.ExpectedRunRevision) return null;
        var before = (await ReadTenantAsync(db, tenant.TenantId, ct).ConfigureAwait(false))!;
        var owned = await OwnedAgents(db, tenant.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == row.AgentInstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("executor_unavailable");
        var agent = Owned(owned); TenantSessionRules.RequireExecutor(before, agent); await ValidateTenantActorAsync(db, before, agent.PrincipalId, ct).ConfigureAwait(false);
        if (run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId is { } human) await ValidateTenantActorAsync(db, before, human, ct).ConfigureAwait(false);
        await TenantExecutionLineage.RequireRequesterAsync(run, before, new IamAuthorizer(this), this, ct).ConfigureAwait(false);
        TenantWorkspaceEffectRules.Session(run, agent, await ReadSandboxSession(db, tenant.TenantId, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable"));
        var definition = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), agent.Instance.SettingsOverrides);
        var at = ApprovalNow; var args = TenantWorkspaceEffectRules.Validate(run, agent, definition, commit.Operation, commit.Generation, at);
        if (commit.ActionHash != TenantWorkspaceMutationContract.ActionHash(run.SessionId, args.ParametersHash)) throw new SecurityConflictException("operation_mismatch");
        var correlated = await RootApproval(db, before.Tenant.TenantId, run.Admission.Activation.RootActivationId.ToString("D"), agent.PrincipalId,
            TenantWorkspaceMutationOperationRules.Correlation(run, args)).AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (correlated is not null) throw new AccessDeniedException(TenantApprovalRules.DuplicateReason(correlated.Domain()));
        await TenantWorkspaceEffectRules.RequireAuthority(before, run, args, new IamAuthorizer(this), ct).ConfigureAwait(false);
        TenantWorkspaceEffectRules.Transaction(next, expectedSecurityRevision, run, audit, args, true);
        var saved = run.BeginDirectExternalEffect(run.Revision, commit.Generation, commit.Operation, commit.ActionHash, at);
        audit = TenantWorkspaceEffectRules.Audit(audit with { At = at, Decision = "allowed", ReasonCode = "workspace_mutation_dispatched" }, before, run, args, commit.Operation.OperationId, true);
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        AgentRunStoreMapping.Apply(row, saved); db.Entry(tenant).CurrentValues.SetValues(next.Tenant); await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return new(TenantWorkspaceEffectRules.Fence(saved), null); }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return null; }
    }
    public async ValueTask<IReadOnlyList<TenantWorkspaceDispatch>> RecoverableWorkspaceEffectsAsync(DateTimeOffset at, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Set<AgentRunRecord>().FromSqlInterpolated($"""
            SELECT r.* FROM AgentRuns r WHERE r.TenantId IS NOT NULL
            AND json_type(r.PayloadJson, '$.sideEffect.externalEffect.directOperation') IS 'object'
            AND json_extract(r.PayloadJson, '$.sideEffect.externalEffect.directOperation.toolName') IN ('workspace.write','workspace.patch','workspace.mkdir','workspace.copy','workspace.move','workspace.delete','workspace.batch','office.document.create','office.document.edit','office.spreadsheet.create','office.spreadsheet.edit')
            AND json_extract(r.PayloadJson, '$.sideEffect.disposition') IN (2,5)
            AND (r.Status <> {(int)AgentRunStatus.Running} OR r.LeaseExpiresAtUtc IS NULL OR r.LeaseExpiresAtUtc <= {at.ToUnixTimeMilliseconds()}
                OR json_extract(r.PayloadJson, '$.sideEffect.disposition') = 5
                OR json_extract(r.PayloadJson, '$.claim.generation') IS NOT json_extract(r.PayloadJson, '$.sideEffect.externalEffect.generation'))
            AND EXISTS (SELECT 1 FROM TenantSessions s WHERE s.TenantId = r.TenantId AND s.SessionId = r.SessionId AND s.AgentInstanceId = r.AgentInstanceId AND s.ExecutorPrincipalId = r.ExecutorPrincipalId)
            AND NOT EXISTS (SELECT 1 FROM TenantOperationReceipts o WHERE lower(o.OperationId) = json_extract(r.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId'))
            """).AsNoTracking().OrderBy(r => r.UpdatedAtUtc).ThenBy(r => r.AgentRunId).Take(Math.Clamp(limit, 1, 100)).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(AgentRunStoreMapping.ToDomain).Where(r => TenantWorkspaceMutationContract.IsMutation(r.SideEffect.ExternalEffect!.DirectOperation!.ToolName)).Select(TenantWorkspaceEffectRules.Fence).ToArray();
    }
    public async ValueTask<TenantOperationReceipt?> TrySettleWorkspaceEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantWorkspaceDispatch fence, TenantOperationOutcome outcome, string reason, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); TenantWorkspaceEffectRules.Outcome(outcome, reason, recovery);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var op = fence.Origin.DirectOperation ?? throw new SecurityConflictException("operation_mismatch");
        var row = await RetainedTenantOperationRuns(db, fence.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == fence.RunId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        var run = AgentRunStoreMapping.ToDomain(row);
        var old = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.OperationId == op.OperationId, ct).ConfigureAwait(false);
        if (old is not null) return TenantWorkspaceEffectRules.Match(old.Domain(), run, op.SourceCallId, op.ParametersHash);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == fence.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Revision != expectedSecurityRevision) return null;
        var before = (await ReadTenantAsync(db, fence.TenantId, ct).ConfigureAwait(false))!;
        TenantWorkspaceEffectRules.RequireFence(run, fence); var at = ApprovalNow;
        if (at < fence.Origin.StartedAtUtc || recovery && !TenantWorkspaceEffectRules.Recoverable(run, at)) return null;
        var args = TenantWorkspaceMutationContract.Parse(run.SessionId, op.ToolName, op.ArgumentsJson);
        TenantWorkspaceEffectRules.Transaction(next, expectedSecurityRevision, run, audit, args, false);
        var saved = run.Status == AgentRunStatus.Running && !run.CancellationRequested && run.Claim?.Generation == fence.Origin.Generation
            && run.SideEffect.Disposition == AgentRunSideEffectDisposition.InFlight ? run.MarkSideEffect(run.Revision, fence.Origin.Generation,
                outcome == TenantOperationOutcome.Completed ? AgentRunSideEffectDisposition.Succeeded : outcome == TenantOperationOutcome.Denied ? AgentRunSideEffectDisposition.DefinitelyFailed : AgentRunSideEffectDisposition.Indeterminate,
                run.SideEffect.ToolCallId!, fence.ActionHash, at) : run;
        var receipt = TenantWorkspaceEffectRules.Receipt(run, fence, outcome, reason, at);
        audit = TenantWorkspaceEffectRules.Audit(audit with { At = at, Decision = outcome == TenantOperationOutcome.Completed ? "allowed" : outcome == TenantOperationOutcome.Denied ? "denied" : "indeterminate", ReasonCode = reason }, before, run, args, op.OperationId, false);
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        if (saved.Revision != run.Revision) AgentRunStoreMapping.Apply(row, saved);
        db.Add(TenantOperationReceiptRecord.From(receipt)); db.Entry(tenant).CurrentValues.SetValues(next.Tenant); await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return receipt; }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return null; }
    }
}
