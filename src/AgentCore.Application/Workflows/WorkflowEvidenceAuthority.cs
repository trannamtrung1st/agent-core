using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Goals;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public enum WorkflowEvidenceStatus { Pending, Passed, Failed, Inconclusive }
public sealed record WorkflowEvidence(WorkflowEvidenceStatus Status, string? ReasonCode = null);

/// <summary>Reads only incumbent saved evidence. It never changes Goal state or treats a process decision as IAM approval.</summary>
public sealed class WorkflowEvidenceAuthority(ITenantWorkflowExecutionStore processes,
    ITenantEvaluationStore evaluations, ITenantSessionGoalStore goals, ITenantRunStore runs, ITenantSessionStore sessions,
    ITenantTaskStore tasks, ITenantAgentStore agents, IamAuthorizer iam, TimeProvider? time = null)
{
    public static string EvaluationKey(WorkflowStepOperation operation) => "workflow:" + operation.OperationId.ToString("D");
    public async ValueTask<Guid> EvaluatorAsync(WorkflowExecution execution, CancellationToken ct)
    {
        if (execution.CoordinatorInstanceId is not { } id) throw new AccessDeniedException("workflow_coordinator_unavailable");
        return (await agents.FindTenantAgentAsync(execution.TenantId, id, ct) ?? throw new AccessDeniedException("workflow_coordinator_unavailable")).PrincipalId;
    }
    private async ValueTask Require(TenantSecurityState state, Guid actor, string permission, AccessScope scope, CancellationToken ct)
    { if (!(await iam.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct)).Allowed) throw new AccessDeniedException("workflow_evidence_unavailable"); }
    public async ValueTask<WorkflowEvidence> VerificationAsync(TenantSecurityState state, WorkflowExecution execution,
        WorkflowVersion version, WorkflowNode node, WorkflowStepOperation operation, IReadOnlyList<WorkflowStepOperation> steps, CancellationToken ct)
    {
        var binding = node.Verification ?? throw new ArgumentException("An exact verification binding is required.");
        if (binding.Kind == WorkflowVerificationKind.GoalCheckpoint)
        {
            var actor = execution.CoordinatorInstanceId is null ? execution.Source.PrincipalId : await EvaluatorAsync(execution, ct);
            var goal = await ReadGoalBindingAsync(state, actor, binding.SessionId!.Value, binding.GoalId!.Value, binding.PlanRevision!.Value, ct);
            var checkpoint = goal.Checkpoints.SingleOrDefault(c => c.CheckpointId == binding.CheckpointId) ?? throw new AccessDeniedException("workflow_goal_unavailable");
            return checkpoint.Status == GoalCheckpointStatus.Verified ? new(WorkflowEvidenceStatus.Passed)
                : checkpoint.Status == GoalCheckpointStatus.Skipped || goal.Status == GoalStatus.Cancelled ? new(WorkflowEvidenceStatus.Failed, "goal_checkpoint_not_verified")
                : new(WorkflowEvidenceStatus.Pending, "goal_checkpoint_verification_pending");
        }
        if (binding.Kind != WorkflowVerificationKind.Evaluation || operation.EvaluationAttemptId is not { } attemptId) return new(WorkflowEvidenceStatus.Pending, "evaluation_pending");
        var source = steps.SingleOrDefault(s => s.NodeId == binding.TaskNodeId && s.Kind == WorkflowNodeKind.AgentTask && s.Status >= WorkflowOperationStatus.Settled && s.Outcome == "succeeded")
            ?? throw new AccessDeniedException("workflow_task_evidence_unavailable");
        var task = source.TaskId is { } taskId ? await tasks.FindTaskAsync(execution.TenantId, taskId, ct) : null;
        var worker = task?.TerminalRunId is { } runId ? await runs.FindTenantRunAsync(execution.TenantId, runId, ct) : null;
        if (task is not { State: DelegatedTaskState.Succeeded } || worker is null || !worker.IsTerminal || worker.RequiresEffectReconciliation
            || worker.AgentRunId != source.WorkerRunId || task.Source.Workflow?.ExecutionId != execution.ExecutionId || task.Source.Workflow.NodeId != binding.TaskNodeId)
            throw new AccessDeniedException("workflow_task_evidence_unavailable");
        await tasks.RequireTaskOutcomeSourcesAsync(worker, ct);
        var actorId = await EvaluatorAsync(execution, ct);
        await SuiteBinding(state, actorId, binding, ct);
        await Require(state, actorId, "agent-runs.read", TenantRunRules.Scope(execution.TenantId, worker.AgentRunId), ct);
        await Require(state, actorId, "sessions.read", TenantSessionRules.Scope(execution.TenantId, worker.SessionId), ct);
        await Require(state, actorId, "agents.read", AccessScope.ForResource(execution.TenantId, "agent", worker.AgentInstanceId.ToString("D")), ct);
        var attempt = await evaluations.FindAttemptAsync(execution.TenantId, attemptId, ct) ?? throw new AccessDeniedException("workflow_evaluation_unavailable");
        if (attempt.ActorId != actorId || attempt.DedupKey != EvaluationKey(operation) || attempt.SubjectKind != EvaluationSubjectKind.AgentRun
            || attempt.SubjectId != worker.AgentRunId || attempt.SubjectRevision != worker.Revision
            || attempt.SourceFingerprint != EvaluationRules.Hash(new { worker.AgentRunId, worker.Revision, worker.Status })
            || attempt.SuiteId != binding.SuiteId || attempt.SuiteVersion != binding.SuiteVersion || attempt.SuiteHash != binding.SuiteHash || attempt.Hook != EvaluationHook.Manual)
            throw new AccessDeniedException("workflow_evaluation_mismatch");
        if (attempt.Status == EvaluationStatus.Running && (time ?? TimeProvider.System).GetUtcNow() > attempt.CreatedAt.AddSeconds(EvaluationRules.TimeoutSeconds + 5))
            return new(WorkflowEvidenceStatus.Inconclusive, "evaluation_interrupted");
        if (attempt.Status is EvaluationStatus.Running or EvaluationStatus.Pending) return new(WorkflowEvidenceStatus.Pending,
            attempt.Findings.Any(f => f.ReasonCode == "sandbox_approval_required") ? "evaluation_operation_approval_pending"
                : attempt.Findings.Any(f => f.ReasonCode == "review_required") ? "evaluation_review_pending" : "evaluation_pending");
        if (attempt.Status != EvaluationStatus.Completed || attempt.Outcome == EvaluationOutcome.Inconclusive) return new(WorkflowEvidenceStatus.Inconclusive, "verification_inconclusive");
        return attempt.Outcome == EvaluationOutcome.Passed ? new(WorkflowEvidenceStatus.Passed) : new(WorkflowEvidenceStatus.Failed, "verification_failed");
    }
    private async ValueTask SuiteBinding(TenantSecurityState state, Guid actor, WorkflowVerification binding, CancellationToken ct)
    {
        var suiteScope = AccessScope.ForResource(state.Tenant.TenantId, "evaluation-suite", binding.SuiteId!.Value.ToString("D"));
        await Require(state, actor, "evaluations.read", suiteScope, ct); await Require(state, actor, "evaluations.run", suiteScope, ct);
        var suite = await evaluations.FindSuiteAsync(state.Tenant.TenantId, binding.SuiteId.Value, binding.SuiteVersion, ct);
        var current = await evaluations.FindSuiteAsync(state.Tenant.TenantId, binding.SuiteId.Value, ct: ct);
        if (suite is null || suite.Archived || current?.Archived != false || suite.ContentHash != binding.SuiteHash || suite.DraftId is not null)
            throw new AccessDeniedException("workflow_suite_unavailable");
        foreach (var reference in suite.Checks.Select(c => c.Script).OfType<ScriptReference>())
        {
            var script = await evaluations.FindScriptAsync(state.Tenant.TenantId, reference.ScriptId, reference.Version, ct);
            var latest = await evaluations.FindScriptAsync(state.Tenant.TenantId, reference.ScriptId, ct: ct);
            if (script is null || script.Archived || latest?.Archived != false || script.ContentHash != reference.ContentHash || !script.Hooks.Contains(EvaluationHook.Manual)) throw new AccessDeniedException("workflow_script_unavailable");
            await Require(state, actor, "scripts.read", AccessScope.ForResource(state.Tenant.TenantId, "script", reference.ScriptId.ToString("D")), ct);
            await Require(state, actor, "scripts.execute", AccessScope.ForResource(state.Tenant.TenantId, "script", reference.ScriptId.ToString("D")), ct);
        }
    }
    public async ValueTask RequireVerificationBindingsAsync(TenantSecurityState state, Guid actor, WorkflowGraph graph, CancellationToken ct)
    {
        foreach (var binding in graph.Nodes.Select(n => n.Verification).OfType<WorkflowVerification>())
            if (binding.Kind == WorkflowVerificationKind.Evaluation) await SuiteBinding(state, actor, binding, ct);
            else _ = await ReadGoalBindingAsync(state, actor, binding.SessionId!.Value, binding.GoalId!.Value, binding.PlanRevision!.Value, ct);
        if (graph.SuccessGoal is { } goal) _ = await ReadGoalBindingAsync(state, actor, goal.SessionId, goal.GoalId, goal.PlanRevision, ct);
    }
    public async ValueTask<SessionGoal> ReadGoalBindingAsync(TenantSecurityState state, Guid actor, Guid sessionId, Guid goalId, long planRevision, CancellationToken ct)
    {
        await Require(state, actor, "sessions.read", TenantSessionRules.Scope(state.Tenant.TenantId, sessionId), ct);
        await Require(state, actor, "goals.read", TenantSessionRules.Scope(state.Tenant.TenantId, sessionId), ct);
        var session = await sessions.FindTenantSessionAsync(state.Tenant.TenantId, sessionId, ct);
        var goal = await goals.FindGoalAsync(state.Tenant.TenantId, sessionId, goalId, ct);
        if (session is null || goal is null || goal.PlanRevision != planRevision
            || sessions is not ITenantSpaceStore spaces || await spaces.FindSpaceAsync(state.Tenant.TenantId, goal.SpaceId, ct) is not { DeletedAt: null }) throw new AccessDeniedException("workflow_goal_unavailable");
        await Require(state, actor, "spaces.read", TenantSpaceService.Scope(goal.TenantId, goal.SpaceId), ct);
        return goal;
    }
    public async ValueTask<bool> GoalSucceededAsync(TenantSecurityState state, WorkflowExecution execution, WorkflowVersion version, CancellationToken ct)
    {
        if (version.Graph.Read().SuccessGoal is not { } binding) return true;
        var actor = execution.CoordinatorInstanceId is null ? execution.Source.PrincipalId : await EvaluatorAsync(execution, ct);
        var goal = await ReadGoalBindingAsync(state, actor, binding.SessionId, binding.GoalId, binding.PlanRevision, ct);
        return goal.Status == GoalStatus.Completed && GoalRules.Verified(goal);
    }
    public async ValueTask<WorkflowProof> FinalProofAsync(TenantSecurityState state, WorkflowExecution execution, WorkflowVersion version,
        IReadOnlyList<WorkflowStepOperation> steps, CancellationToken ct)
    {
        var proof = WorkflowProofRules.Evaluate(version, steps, await GoalSucceededAsync(state, execution, version, ct));
        if (!proof.CanSucceed) return proof;
        var nodes = version.Graph.Read().Nodes.ToDictionary(n => n.Id);
        foreach (var id in proof.RouteNodes)
        {
            if (nodes[id].Kind == WorkflowNodeKind.Verify)
            {
                var saved = steps.Single(s => s.NodeId == id);
                if ((await VerificationAsync(state, execution, version, nodes[id], saved, steps, ct)).Status != WorkflowEvidenceStatus.Passed)
                    return proof with { CanSucceed = false, ReasonCode = "workflow_verification_changed" };
            }
            if (nodes[id].Kind == WorkflowNodeKind.HumanDecision)
            {
                var saved = steps.Single(s => s.NodeId == id);
                var receipt = await processes.FindWorkflowDecisionAsync(execution.TenantId, execution.ExecutionId, id, ct);
                if (receipt is not { Approved: true } || receipt.GraphHash != execution.GraphHash || receipt.OperationId != saved.OperationId
                    || receipt.ReviewerPrincipalId != nodes[id].Decision!.ReviewerPrincipalId || saved.DecisionActorId != receipt.ReviewerPrincipalId)
                    return proof with { CanSucceed = false, ReasonCode = "workflow_human_evidence_missing" };
            }
        }
        return proof;
    }
    public async ValueTask RequireStepWriteAsync(TenantSecurityState state, WorkflowExecution execution, WorkflowVersion version,
        WorkflowStepOperation before, WorkflowStepOperation next, IReadOnlyList<WorkflowStepOperation> steps, CancellationToken ct)
    {
        WorkflowExecutionAuthority.StepChange(execution, version, before, next);
        if (next.Status == WorkflowOperationStatus.SignalConsumed && before.Status == WorkflowOperationStatus.Settled) return;
        var node = version.Graph.Read().Nodes.Single(n => n.Id == next.NodeId);
        if (node.Kind == WorkflowNodeKind.Verify)
        {
            var evidence = await VerificationAsync(state, execution, version, node, next, steps, ct);
            if (next.Status == WorkflowOperationStatus.Admitted && before.Status == WorkflowOperationStatus.Reserved && next.EvaluationAttemptId is not null
                && next.Outcome is null && !next.ProofAccepted && next.DecisionActorId is null && next.TaskId is null && next.WorkerRunId is null) return;
            if (next.Status == WorkflowOperationStatus.Settled && next.ProofAccepted == (evidence.Status == WorkflowEvidenceStatus.Passed)
                && next.Outcome == (evidence.Status == WorkflowEvidenceStatus.Passed ? "passed" : "failed")
                && evidence.Status is WorkflowEvidenceStatus.Passed or WorkflowEvidenceStatus.Failed && next.TaskId is null && next.WorkerRunId is null && next.DecisionActorId is null) return;
        }
        if (node.Kind == WorkflowNodeKind.Branch && before.Status == WorkflowOperationStatus.Reserved && next.Status == WorkflowOperationStatus.Settled
            && !next.ProofAccepted && next.Outcome == WorkflowProofRules.BranchOutcome(node, steps)
            && next.SelectedEdgeId == version.Graph.Read().Edges.Single(e => e.From == node.Id && e.Outcome == next.Outcome).Id
            && next.TaskId is null && next.WorkerRunId is null && next.EvaluationAttemptId is null && next.DecisionActorId is null) return;
        if (node.Kind == WorkflowNodeKind.EndSuccess && before.Status == WorkflowOperationStatus.Reserved && next.Status == WorkflowOperationStatus.Settled
            && next.Outcome == "complete" && next.ProofAccepted && next.TaskId is null && next.WorkerRunId is null && next.EvaluationAttemptId is null && next.DecisionActorId is null
            && (await FinalProofAsync(state, execution, version, steps, ct)).CanSucceed) return;
        throw new AccessDeniedException("workflow_step_evidence_unavailable");
    }
}
