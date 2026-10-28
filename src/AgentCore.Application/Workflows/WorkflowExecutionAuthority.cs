using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public static class WorkflowExecutionAuthority
{
    public static async ValueTask RequireSourceAsync(TenantSecurityState state, WorkflowExecution execution, WorkflowVersion version,
        IIamStore security, ITenantSessionStore sessions, IamAuthorizer iam, DateTimeOffset now, CancellationToken ct, bool dispatch = true)
    {
        WorkflowExecutionRules.Validate(execution, version);
        if (state.Tenant.TenantId != execution.TenantId || state.Tenant.Status != IdentityStatus.Active || dispatch && (execution.CancellationRequested || execution.SourceClosure is not null
            || WorkflowExecutionRules.Terminal(execution.Status) || execution.DeadlineAt <= now)) throw new AccessDeniedException("workflow_source_unavailable");
        var scope = WorkflowCatalogAuthority.Scope(execution.TenantId, execution.WorkflowId);
        if (!(await iam.CheckOwnedTenantAsync(state, execution.Source.PrincipalId, PermissionCatalog.Get("workflows.start"), scope, ct)).Allowed)
            throw new AccessDeniedException("workflow_source_unavailable");
        if (execution.Source.Kind == WorkflowInvocationKind.HumanManual)
        {
            if (execution.Source.SourceId != execution.ExecutionId || execution.Source.AccountId is not { } account
                || !state.Users.Any(u => u.PrincipalId == execution.Source.PrincipalId && u.AccountId == account && u.Status == IdentityStatus.Active)
                || await security.ReadAccountAsync(account, ct) is not { Status: IdentityStatus.Active }) throw new AccessDeniedException("workflow_source_unavailable");
        }
        else if (security is ITenantWorkflowInvocationStore invocations) await invocations.RequireWorkflowInvocationSourceAsync(execution, ct);
        else throw new AccessDeniedException("workflow_source_not_qualified");
        if (execution.Source.SessionId is { } sessionId)
        {
            var session = await sessions.FindTenantSessionAsync(execution.TenantId, sessionId, ct);
            if (session is null || SessionRetention.IsDue(session.Snapshot, now)
                || !(await iam.CheckOwnedTenantAsync(state, execution.Source.PrincipalId, PermissionCatalog.Get("sessions.read"), TenantSessionRules.Scope(execution.TenantId, sessionId), ct)).Allowed)
                throw new AccessDeniedException("workflow_source_unavailable");
        }
    }
    public static void ExecutionChange(WorkflowExecution before, WorkflowExecution next, WorkflowVersion version)
    {
        WorkflowExecutionRules.Validate(next, version);
        if (next.Revision != before.Revision + 1 || next with { Revision = before.Revision, Status = before.Status,
                CancellationRequested = before.CancellationRequested, ReasonCode = before.ReasonCode, TerminalAt = before.TerminalAt,
                Cancellation = before.Cancellation, SourceClosure = before.SourceClosure } != before
            || WorkflowExecutionRules.Terminal(before.Status) || before.CancellationRequested && !next.CancellationRequested
            || before.Cancellation is not null && next.Cancellation != before.Cancellation
            || before.SourceClosure is not null && next.SourceClosure != before.SourceClosure)
            throw new ArgumentException("Workflow business binding is immutable and terminal projection is monotonic.");
    }
    public static void StepChange(WorkflowExecution execution, WorkflowVersion version, WorkflowStepOperation before, WorkflowStepOperation next)
    {
        WorkflowExecutionRules.Operation(execution, next, version);
        if (next.Revision != before.Revision + 1 || next.Status < before.Status || before.Status == WorkflowOperationStatus.SignalConsumed
            || next with { Revision = before.Revision, Status = before.Status, TaskId = before.TaskId, WorkerRunId = before.WorkerRunId,
                EvaluationAttemptId = before.EvaluationAttemptId, DecisionActorId = before.DecisionActorId, Outcome = before.Outcome,
                ProofAccepted = before.ProofAccepted, SelectedEdgeId = before.SelectedEdgeId, ReasonCode = before.ReasonCode, SettledAt = before.SettledAt } != before
            || before.Status >= WorkflowOperationStatus.Settled && next with { Status = before.Status, Revision = before.Revision } != before)
            throw new ArgumentException("Step receipts are monotonic and settled evidence is immutable.");
    }
    public static void TaskGraph(WorkflowTaskAdmission command, WorkflowExecution execution, WorkflowStepOperation operation, WorkflowVersion version, TenantAgent worker)
    {
        var task = command.Task; var source = task.Source; source.Validate();
        var node = version.Graph.Read().Nodes.Single(n => n.Id == operation.NodeId);
        if (source.ActorKind != TaskActorKind.WorkflowContinuation || source.Workflow is not { } witness || node.Task is not { } binding
            || source.TenantId != execution.TenantId || source.InstanceId != execution.CoordinatorInstanceId || source.PrincipalId == worker.PrincipalId && source.InstanceId != worker.Instance.InstanceId
            || source.OriginalInitiatorPrincipalId != execution.Source.OriginalHumanPrincipalId || source.RootActivationId != command.Run.ActivationId
            || witness.WorkflowId != execution.WorkflowId || witness.ExecutionId != execution.ExecutionId || witness.Version != execution.Version
            || witness.GraphHash != execution.GraphHash || witness.NodeId != operation.NodeId || witness.OperationId != operation.OperationId
            || witness.OriginalSessionId != execution.Source.SessionId
            || witness.Causation != execution.Source.Causation
            || task.Request.DependsOnTaskIds.Count != 0 || task.Request.RetryOfTaskId is not null || task.Request.ReportOnCompletion
            || task.Request.Objective != WorkflowTaskRules.Objective(execution, node) || task.Request.Title != node.Name
            || task.Request.Eligible || task.Request.CandidateInstanceIds.Count != 1 || task.Assignment.InstanceId != binding.TargetInstanceId
            || task.Assignment.SpaceId != binding.SpaceId || task.Assignment.EnvironmentId != binding.EnvironmentId
            || task.State != DelegatedTaskState.Queued || task.Revision != 1 || task.TaskId != operation.TaskId || command.Run.AgentRunId != operation.WorkerRunId
            || task.WorkerSessionId != command.Session.SessionId || task.InitialWorkerRunId != command.Run.AgentRunId || !command.Run.IsInitialQueued)
            throw new AccessDeniedException("workflow_task_admission_mismatch");
        var expected = TaskAdmissionFactory.Worker(task, worker, new(command.Run.Admission.Configuration!, worker.Instance.Persona, command.Run.PinnedSkillCatalog, worker.Instance.ExecutionBudgets),
            task.Assignment.Model!, command.Session.SessionId, command.Session.Entries.Single().EntryId, command.Run.ActivationId,
            command.Run.AgentRunId, command.Run.ResponseId!.Value, task.CreatedAt);
        if (System.Text.Json.JsonSerializer.Serialize(expected.Run.Admission) != System.Text.Json.JsonSerializer.Serialize(command.Run.Admission)
            || System.Text.Json.JsonSerializer.Serialize(expected.Session) != System.Text.Json.JsonSerializer.Serialize(command.Session))
            throw new AccessDeniedException("workflow_task_admission_mismatch");
    }
}
