using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantHumanTaskApprovalStore
{
    private async Task<(TenantSession Source, TenantAgent Worker)> RequireHumanTaskConsentOperation(AgentCoreDbContext db, TenantApprovalRequest request, CancellationToken ct)
    {
        TenantTaskConsentRules.RequireHumanShape(request); var task = request.Operation.TaskDelegation!.Task;
        var state = await ReadTenantAsync(db, request.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var source = await ReadSandboxSession(db, request.TenantId, task.Source.SessionId, ct) ?? throw new AccessDeniedException("task_source_unavailable");
        var agentRow = await OwnedAgents(db, request.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == task.Source.InstanceId.ToString("D"), ct)
            ?? throw new AccessDeniedException("task_source_unavailable");
        await TenantTaskConsentRules.RequireHumanSourceAsync(request, source, Owned(agentRow), state, new(this), TaskNow, ct);
        return (source, await RequireTaskConsentWorker(db, task, state, ct));
    }
    private async Task<TenantAgent> ValidateHumanTaskAdmission(AgentCoreDbContext db, TenantApprovalRequest request, TenantHumanTaskAdmission admission, CancellationToken ct)
    {
        TenantTaskConsentRules.MatchHumanAdmission(request, admission); var task = admission.Task;
        var state = await ReadTenantAsync(db, request.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        if (admission.ExpectedSecurityRevision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        var current = await RequireHumanTaskConsentOperation(db, request, ct);
        if (await db.Set<DelegatedTaskRow>().AnyAsync(t => t.TenantId == request.TenantId && (t.TaskId == task.TaskId
            || t.OperationScope == task.Source.OperationScope && t.RequestKey == task.Request.RequestKey), ct)) throw new SecurityConflictException("task_request_conflict");
        TaskAdmissionFactory.RequireHumanGraph(admission, current.Source, current.Worker, TaskNow);
        var all = (await db.Set<DelegatedTaskRow>().AsNoTracking().Where(t => t.TenantId == request.TenantId).ToArrayAsync(ct)).Select(TaskPersistence.Value).ToArray();
        TaskAuthority.Bounds(task, all, TaskNow, current.Worker.Instance.Cooperation!);
        var definition = admission.Run?.Admission.Configuration?.Definition ?? InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, current.Worker, ct), current.Worker.Instance.SettingsOverrides);
        var budget = ExecutionBudgetResolver.Resolve(definition, current.Worker.Instance.ExecutionBudgets, false).Profile;
        if (TaskBudgetRules.Allocate(null, budget, [], _ => null, TaskNow, reportOnCompletion: task.Request.ReportOnCompletion) != task.Assignment.Budget)
            throw new SecurityConflictException("task_budget_changed");
        foreach (var id in task.Request.DependsOnTaskIds.Concat(task.Request.RetryOfTaskId is { } retry ? new[] { retry } : []))
        {
            var dependency = all.SingleOrDefault(t => t.TaskId == id);
            if (dependency is null || !await OwnedSessions(db, request.TenantId).AnyAsync(s => s.SessionId == dependency.Source.SessionId.ToString("D"), ct)
                || !await TaskAuthority.CanReadHumanCommandAsync(state, task.Source.PrincipalId, dependency, new(this), ct))
                throw new AccessDeniedException("task_dependency_unavailable");
        }
        if (admission.Run is { } run) await RequireSpaceExecutionSource(db, run, state, new(this), ct, admission: true);
        return current.Worker;
    }
    public async ValueTask<TenantApprovalRequest?> TryPrepareHumanTaskApprovalAsync(TaskAdmission preparation, TenantApprovalRequest request, CancellationToken ct = default)
    {
        if (preparation.Actor.Human is not { } human || preparation.Actor.Agent is not null || human.PrincipalId != request.ExecutorPrincipalId
            || request.Status != TenantApprovalStatus.Pending || request.Revision != 1) throw new AccessDeniedException("task_consent_unavailable");
        var state = await ReadTenantAsync(request.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        if (state.Tenant.Revision != preparation.ExpectedSecurityRevision) return null;
        TenantApprovalRequest? result = null;
        var audit = new AuthorizationAudit(preparation.AuditId, request.TenantId, human.PrincipalId, "approvals.request", request.RequestId.ToString("D"), "allowed", "approval_pending", TaskNow);
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var written = await WriteTenantResourceAsync(next, state.Tenant.Revision, audit, async db => {
            await RequireHumanSessionAsync(db, request.TenantId, human, ct);
            _ = await RequireTaskSource(db, request.TenantId, preparation.SourceSessionId, preparation.Actor, "tasks.delegate", ct);
            var scope = preparation.Task.Source.OperationScope; var key = preparation.Request.RequestKey;
            var duplicate = await db.Set<TenantApprovalRecord>().FromSqlInterpolated($"SELECT * FROM TenantApprovalRequests WHERE TenantId={request.TenantId} AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.OperationScope')={scope} AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Request.RequestKey')={key}").AsNoTracking().SingleOrDefaultAsync(ct);
            if (duplicate is not null) { result = duplicate.Domain(); TenantApprovalRules.MatchTaskDelegationRequest(result, preparation.Request); return false; }
            if (request.ExpiresAt <= TaskNow) throw new SecurityConflictException("approval_expired");
            _ = await ValidateHumanTaskAdmission(db, request, new(preparation.Task, preparation.WorkerSession, preparation.WorkerRun, preparation.ExpectedSecurityRevision, preparation.AuditId), ct);
            var current = (await ReadTenantAsync(db, request.TenantId, ct))!;
            if (!await TenantTaskConsentRules.HumanNeedsReviewAsync(current, preparation.Task, new(this), ct)) throw new AccessDeniedException("task_review_not_required");
            var qualified = false;
            foreach (var candidate in current.Users.Where(u => u.Status == IdentityStatus.Active))
            {
                try { await TenantTaskConsentRules.ReviewerAsync(current, request, candidate.PrincipalId, new(this), ct); qualified = true; break; }
                catch (AccessDeniedException) { }
            }
            if (!qualified) throw new AccessDeniedException("no_eligible_approver");
            var cutoff = (TaskNow - TenantApprovalLimits.Window).ToUnixTimeMilliseconds();
            var recentQuery = db.Set<TenantApprovalRecord>().AsNoTracking().Where(r => r.TenantId == request.TenantId && (r.Status == 0 || r.CreatedAt >= cutoff));
            var total = await recentQuery.CountAsync(ct);
            TenantApprovalLimits.Require(total, await recentQuery.CountAsync(r => r.ExecutorPrincipalId == human.PrincipalId, ct),
                await recentQuery.CountAsync(r => r.OriginalInitiatorPrincipalId == human.PrincipalId, ct), 0,
                await db.Set<TenantApprovalRecord>().CountAsync(r => r.TenantId == request.TenantId && r.RootActivationId == request.RootActivationId.ToString("D"), ct), true);
            var recent = await recentQuery.ToArrayAsync(ct);
            TenantApprovalLimits.Require(total, recent.Count(r => r.ExecutorPrincipalId == human.PrincipalId), recent.Count(r => r.OriginalInitiatorPrincipalId == human.PrincipalId),
                recent.Count(r => r.Domain().Fingerprint == request.Fingerprint), recent.Count(r => r.RootActivationId == request.RootActivationId.ToString("D")), true);
            db.Add(TenantApprovalRecord.From(request)); result = request; return true;
        }, ct, human);
        return written || result is not null && result.RequestId != request.RequestId ? result : null;
    }
    public async ValueTask<HumanTaskApprovalOutcome?> FindHumanTaskApprovalOutcomeAsync(Guid tenant, Guid operation, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Set<HumanTaskApprovalOutcomeRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.OperationId == operation, ct);
        return row is null ? null : HumanTaskApprovalPersistence.Value(row);
    }
    public async ValueTask<bool> TrySettleHumanTaskApprovalAsync(ApprovalProcessedSignal signal, TenantHumanTaskAdmission? admission, string? refusalReason,
        long expectedSecurityRevision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var currentSignal = await db.Set<ApprovalProcessedSignal>().SingleOrDefaultAsync(s => s.SignalId == signal.SignalId, ct);
        var requestRow = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == signal.TenantId && r.RequestId == signal.RequestId, ct);
        if (currentSignal != signal || currentSignal?.ProcessedAt is not null || requestRow is null) return false;
        var request = requestRow.Domain();
        if (request.Revision != signal.DecisionRevision || request.RunId != signal.RunId || request.Status == TenantApprovalStatus.Pending) return false;
        TenantTaskConsentRules.RequireHumanShape(request);
        var state = await ReadTenantAsync(db, request.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        if (state.Tenant.Revision != expectedSecurityRevision || await db.Set<HumanTaskApprovalOutcomeRow>().AnyAsync(r => r.TenantId == request.TenantId && r.OperationId == request.Operation.OperationId, ct)) return false;
        var reason = request.Status == TenantApprovalStatus.Approved ? refusalReason : TenantApprovalRules.DuplicateReason(request);
        TenantAgent? worker = null;
        if (request.Status == TenantApprovalStatus.Approved && reason is null)
        {
            if (request.ExpiresAt <= TaskNow) reason = "approval_expired";
            else try {
                var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == request.TenantId && d.RequestId == request.RequestId, ct)
                    ?? throw new AccessDeniedException("task_consent_unavailable");
                if (decision.Status != TenantApprovalStatus.Approved || decision.ActionHash != request.Operation.ActionHash || decision.RequestRevision != request.Revision)
                    throw new AccessDeniedException("task_consent_unavailable");
                await TenantTaskConsentRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new(this), ct);
                worker = await ValidateHumanTaskAdmission(db, request, admission ?? throw new AccessDeniedException("task_admission_unavailable"), ct);
            }
            catch (AccessDeniedException error) { reason = error.ReasonCode; }
            catch (SecurityConflictException error) { reason = error.ReasonCode; }
            catch (AgentCore.Application.Sessions.AgentCoreException error) when (error.Code == "TaskCapacityExceeded") { reason = "task_capacity_exceeded"; }
        }
        var admitted = worker is not null && reason is null; var task = request.Operation.TaskDelegation!.Task;
        var outcome = new HumanTaskApprovalOutcome(request.TenantId, request.RequestId, request.Operation.OperationId, admitted,
            admitted ? "task_created" : reason ?? "task_admission_unavailable", TaskNow,
            admitted ? new(task.TaskId, 1, task.WorkerSessionId, task.InitialWorkerRunId) : null); outcome.Validate();
        if (admitted)
        {
            if (admission!.Run is { } run) StageTaskGraph(db, new(request.TenantId, run.SessionId, worker!.Instance.InstanceId, worker.PrincipalId, task.Source.PrincipalId, task.CreatedAt), admission.Session!, run);
            db.Add(TaskPersistence.Row(task));
            foreach (var dependency in task.Request.DependsOnTaskIds) db.Add(new TaskDependencyRow { TenantId = request.TenantId, TaskId = task.TaskId, PrerequisiteId = dependency });
            // The insert guard observes the actual Task inside the same transaction.
            await db.SaveChangesAsync(ct);
        }
        db.Add(HumanTaskApprovalPersistence.Row(outcome));
        db.Entry(currentSignal!).CurrentValues.SetValues(signal with { Revision = checked(signal.Revision + 1), ProcessedAt = TaskNow });
        await BumpTaskSecurity(db, state, ct);
        db.Add(new AuthorizationAudit(signal.SignalId, request.TenantId, request.ExecutorPrincipalId, TenantTaskConsentRules.AuditAction,
            request.ResourceId, admitted ? "allowed" : "denied", outcome.ReasonCode, TaskNow));
        await SaveTaskChanges(db, ct); await tx.CommitAsync(ct); return true;
    }
}
