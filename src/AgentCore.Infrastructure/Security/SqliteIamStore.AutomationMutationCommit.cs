using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantOperationReceipt?> TryCommitTenantAutomationMutationAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantAutomationMutationCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, next.Tenant.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == commit.RunId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        var old = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == next.Tenant.TenantId && r.RunId == row.AgentRunId && r.SourceCallId == commit.SourceCallId, ct).ConfigureAwait(false);
        if (old is not null) return TenantLocalEffectRules.MatchDirectReceipt(old.Domain(), run, commit.SourceCallId, commit.ParametersHash);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == next.Tenant.TenantId, ct).ConfigureAwait(false);
        if (tenant is null || tenant.Revision != expectedSecurityRevision || run.Revision != commit.ExpectedRunRevision) return null;
        var before = (await ReadTenantAsync(db, tenant.TenantId, ct).ConfigureAwait(false))!;
        var source = await ObserveAutomationMutation(db, run, commit.Prepared.ToolName, commit.Prepared.ArgumentsJson, commit.Prepared, null, ct).ConfigureAwait(false);
        var now = (clock ?? TimeProvider.System).GetUtcNow(); var at = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        var prepared = TenantAutomationMutationCommitRules.Require(source, commit, now);
        await TenantAutomationMutationCommitRules.AuthorityAsync(source, commit.Prepared, new IamAuthorizer(this), ct, noReview: true, at: now).ConfigureAwait(false);
        await RequireAutomationMutationCapacity(db, source, commit.Prepared, ct).ConfigureAwait(false);
        var previous = await RootApprovalAsync(db, before.Tenant.TenantId, run.Admission.Activation.RootActivationId.ToString("D"), source.Reader.Agent.PrincipalId, prepared, ct).ConfigureAwait(false);
        if (previous is not null) throw new AccessDeniedException(TenantApprovalRules.DuplicateReason(previous.Domain()));
        TenantAutomationMutationCommitRules.Transaction(next, expectedSecurityRevision, source, commit.Prepared, audit);
        IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); await EnsureResourceOwnershipAsync(db, next, ct).ConfigureAwait(false);
        await RequireAutomationInvocation(db, source, commit.Prepared, commit.InvocationAdmission, ct).ConfigureAwait(false);
        var parent = await OwnedAutomations(db, tenant.TenantId).SingleOrDefaultAsync(a => a.AutomationId == commit.Prepared.Proposed.AutomationId.ToString("D"), ct).ConfigureAwait(false);
        StageAutomation(db, parent, commit.Prepared.Proposed);
        var savedRun = run.RecordDirectLocalEffect(run.Revision, commit.Generation, commit.OperationId, commit.ActionHash, now); AgentRunStoreMapping.Apply(row, savedRun);
        try
        {
            if (commit.InvocationAdmission is { } admitted) { await StageTenantAutomationGraph(db, admitted, ct).ConfigureAwait(false); db.TriggerOccurrences.Add(TriggerStoreMapping.ToRecord(admitted.Occurrence)); }
            await db.SaveChangesAsync(ct).ConfigureAwait(false); // Applied parent precedes its receipt guard within this transaction.
            var receipt = new TenantOperationReceipt(commit.OperationId, null, tenant.TenantId, run.AgentRunId, run.SessionId, run.Admission.ExecutionContext!.RootActivationId,
                source.Reader.Agent.PrincipalId, run.AgentInstanceId, commit.ActionHash, TenantOperationOutcome.Completed, TenantAutomationMutationRules.Completed,
                source.Reader.Agent.Instance.Revision, source.Reader.Agent.Instance.PersonaRevision, at, commit.SourceCallId, commit.ParametersHash, AutomationMutation: TenantAutomationMutationCommitRules.Evidence(commit.Prepared));
            db.Add(TenantOperationReceiptRecord.From(receipt)); db.Entry(tenant).CurrentValues.SetValues(next.Tenant);
            await ApplyTenantChildrenAsync(db, next, ct).ConfigureAwait(false); db.Add(TenantOperationAuditRules.WithLocalEffectResult(audit, before, run, commit.OperationId) with { At = at });
            await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return receipt;
        }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return null; }
    }
    private static async ValueTask RequireAutomationMutationCapacity(AgentCoreDbContext db, TenantAutomationMutationObservation source, PreparedAutomationMutation value, CancellationToken ct)
    {
        if (value.Proposed.Status == AutomationStatus.Active && source.Current?.Status != AutomationStatus.Active
            && await OwnedAutomations(db, source.Reader.Security.Tenant.TenantId).CountAsync(a => a.AgentInstanceId == source.Reader.Run.AgentInstanceId.ToString("D") && a.Status == (int)AutomationStatus.Active, ct).ConfigureAwait(false) >= (source.Reader.Current.TriggerPolicy?.MaxActiveRegistrations ?? 0))
            throw new AccessDeniedException("automation_limit_reached");
    }
    private async Task<TenantAutomationMutationObservation> RequireAutomationMutationApproval(AgentCoreDbContext db, TenantApprovalRequest request, AgentCore.Domain.Conversation.AgentRun run, CancellationToken ct)
    {
        var value = request.Operation.AutomationMutation!; var source = await ObserveAutomationMutation(db, run, value.ToolName, value.ArgumentsJson, value, null, ct).ConfigureAwait(false);
        await TenantAutomationMutationCommitRules.AuthorityAsync(source, value, new IamAuthorizer(this), ct, request.Kind == ApprovalKind.Review, at: (clock ?? TimeProvider.System).GetUtcNow()).ConfigureAwait(false);
        await RequireAutomationMutationCapacity(db, source, value, ct).ConfigureAwait(false);
        if (value.Invocation is not null && await TenantAutomationBusy(db, request.TenantId, value.Proposed.AutomationId, ct).ConfigureAwait(false)) throw new SecurityConflictException("automation_in_use");
        return source;
    }
}
