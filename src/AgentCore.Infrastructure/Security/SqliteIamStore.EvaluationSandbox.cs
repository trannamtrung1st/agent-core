using AgentCore.Application.Evaluations;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<EvaluationSandboxExecution?> FindSandboxEvaluationAsync(Guid tenant, Guid executionId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Set<EvaluationSandboxRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.ExecutionId == executionId, ct);
        return row is null ? null : EvaluationSandboxPersistence.Value(row);
    }
    public async ValueTask<IReadOnlyList<EvaluationSandboxExecution>> ListSandboxEvaluationsAsync(int limit, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return (await db.Set<EvaluationSandboxRow>().AsNoTracking().Where(r => r.Phase == EvaluationSandboxPhase.AwaitingReview
                ? r.DueAt <= now.ToUnixTimeMilliseconds() || db.Set<TenantApprovalRecord>().Any(a => a.RequestId == r.ApprovalRequestId && a.Status != (int)TenantApprovalStatus.Pending)
                : r.Phase != EvaluationSandboxPhase.Settled ? r.DueAt <= now.ToUnixTimeMilliseconds()
                : (!r.FindingPublished || r.CleanupPending) && r.DueAt <= now.ToUnixTimeMilliseconds())
            .OrderBy(r => r.DueAt).Take(Math.Clamp(limit, 1, 32)).ToArrayAsync(ct)).Select(EvaluationSandboxPersistence.Value).ToArray();
    }
    public async ValueTask<bool> TryReserveSandboxEvaluationAsync(TenantSecurityState next, long securityRevision, EvaluationSandboxExecution value,
        long attemptRevision, AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default) => await WriteDefinitionAsync(next, securityRevision, audit, async db => {
            if (await db.Set<EvaluationSandboxRow>().AnyAsync(e => e.TenantId == value.TenantId && e.AttemptId == value.AttemptId && e.CheckId == value.CheckId, ct)) return null;
            var row = await db.Set<EvaluationAttemptRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == value.TenantId && r.AttemptId == value.AttemptId, ct);
            var source = await OwnedRuns(db, value.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == value.SubjectRunId.ToString("D"), ct);
            var script = await Script(db, value.TenantId, value.Script.ScriptId, value.Script.Version, ct);
            if (value.TenantId != next.Tenant.TenantId || value.ActorId != audit.ActorPrincipalId || row?.Revision != attemptRevision || source is null || script is null) return null;
            EvaluationSandboxRules.Reservation(value, EvaluationPersistence.Value(row), AgentRunStoreMapping.ToDomain(source), script, ApprovalNow);
            var recent = await db.Set<TenantApprovalRecord>().Where(r => r.TenantId == value.TenantId && (r.Status == (int)TenantApprovalStatus.Pending || r.CreatedAt >= (ApprovalNow - TenantApprovalLimits.Window).ToUnixTimeMilliseconds())).ToArrayAsync(ct);
            TenantApprovalLimits.Require(recent.Length, recent.Count(r => r.ExecutorPrincipalId == value.ExecutorId),
                recent.Count(r => r.OriginalInitiatorPrincipalId == value.Approval.OriginalInitiatorPrincipalId), recent.Count(r => r.Domain().Fingerprint == value.Approval.Fingerprint),
                await db.Set<TenantApprovalRecord>().CountAsync(r => r.TenantId == value.TenantId && r.RootActivationId == value.Approval.RootActivationId.ToString("D"), ct), value.Approval.OriginalInitiatorPrincipalId is not null);
            db.Add(EvaluationSandboxPersistence.Row(value)); db.Add(TenantApprovalRecord.From(value.Approval)); return value;
        }, ct, human) is not null;
    public async ValueTask<bool> TrySaveSandboxEvaluationAsync(EvaluationSandboxExecution value, long expectedRevision, CancellationToken ct = default, long? expectedSecurityRevision = null)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<EvaluationSandboxRow>().SingleOrDefaultAsync(r => r.TenantId == value.TenantId && r.ExecutionId == value.ExecutionId, ct);
        if (row?.Revision != expectedRevision) return false;
        var before = EvaluationSandboxPersistence.Value(row); EvaluationSandboxRules.Change(value, before, ApprovalNow);
        if (expectedSecurityRevision is not null && !await db.Set<Tenant>().AnyAsync(t => t.TenantId == value.TenantId && t.Revision == expectedSecurityRevision, ct)) return false;
        if (value.Outcome is EvaluationOutcome.Passed or EvaluationOutcome.Failed && expectedSecurityRevision is null && before.Phase != EvaluationSandboxPhase.Settled) return false;
        if (before.Phase != value.Phase && value.Phase is EvaluationSandboxPhase.Reserved or EvaluationSandboxPhase.Dispatching
            || before.Phase != EvaluationSandboxPhase.Settled && value.Outcome is EvaluationOutcome.Passed or EvaluationOutcome.Failed)
        {
            if (expectedSecurityRevision is null) return false;
            if (value.Human is { } human)
            {
                try { await RequireAgentHumanAsync(db, value.TenantId, value.ActorId, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId), ct); }
                catch (AccessDeniedException) { return false; }
            }
        }
        if (before.Phase == EvaluationSandboxPhase.AwaitingReview && value.Phase == EvaluationSandboxPhase.Reserved)
        {
            if (expectedSecurityRevision is null || !await db.Set<Tenant>().AnyAsync(t => t.TenantId == value.TenantId && t.Revision == expectedSecurityRevision, ct)) return false;
            var approved = (await db.Set<TenantApprovalRecord>().SingleOrDefaultAsync(r => r.TenantId == value.TenantId && r.RequestId == value.Approval.RequestId, ct))?.Domain();
            if (approved is not { Status: TenantApprovalStatus.Approved } || approved.Operation != value.Approval.Operation
                || !await db.Set<TenantApprovalDecision>().AnyAsync(d => d.TenantId == value.TenantId && d.RequestId == approved.RequestId && d.Status == TenantApprovalStatus.Approved, ct)) return false;
        }
        if (EvaluationSandboxRules.TechnicalAudit(before, value, modelIdSource.NewId(), ApprovalNow) is { } audit) db.Add(audit);
        db.Entry(row).CurrentValues.SetValues(EvaluationSandboxPersistence.Row(value));
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }
    public async ValueTask<bool> TryRecordSandboxSettlementUnavailableAsync(Guid tenant, Guid executionId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var nativeRow = await db.Set<EvaluationSandboxRow>().SingleOrDefaultAsync(r => r.TenantId == tenant && r.ExecutionId == executionId, ct);
        if (nativeRow is null) return false;
        var execution = EvaluationSandboxPersistence.Value(nativeRow);
        var row = await db.Set<EvaluationAttemptRow>().SingleOrDefaultAsync(r => r.TenantId == tenant && r.AttemptId == execution.AttemptId, ct);
        if (execution.Phase != EvaluationSandboxPhase.Settled || row?.Status != EvaluationStatus.Pending) return false;
        var attempt = EvaluationPersistence.Value(row);
        if (!attempt.Findings.Any(f => f.SandboxExecutionId == executionId && f.ReasonCode == "sandbox_approval_required")) return false;
        var state = await db.Set<Tenant>().SingleAsync(t => t.TenantId == tenant, ct); var revision = state.Revision + 1;
        var next = attempt with { Revision = attempt.Revision + 1, CommitSequence = revision, UpdatedAt = ApprovalNow,
            Status = EvaluationStatus.Indeterminate, Outcome = EvaluationOutcome.Inconclusive, ReasonCode = "sandbox_evaluation_settlement_authority_changed",
            Findings = attempt.Findings.Select(f => f.SandboxExecutionId == executionId ? f with { Outcome = EvaluationOutcome.Inconclusive,
                ReasonCode = "sandbox_evaluation_settlement_authority_changed", Summary = "Current source or execution authority was unavailable at settlement. No passing evaluation is recorded; read retained technical execution evidence separately." } : f).ToArray() };
        db.Entry(row).CurrentValues.SetValues(EvaluationPersistence.Row(next));
        db.Entry(nativeRow).CurrentValues.SetValues(EvaluationSandboxPersistence.Row(execution with { FindingPublished = true, Revision = execution.Revision + 1 }));
        db.Entry(state).CurrentValues.SetValues(state with { Revision = revision });
        db.Add(new AuthorizationAudit(modelIdSource.NewId(), tenant, execution.ActorId, "evaluations.recover", attempt.AttemptId.ToString("D"), "recorded", next.ReasonCode, ApprovalNow));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true;
    }

}
