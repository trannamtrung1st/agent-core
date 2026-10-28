using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Security;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public sealed record WorkflowGoalLink(Guid SessionId, Guid GoalId, long PlanRevision);
public sealed record WorkflowStepView(string NodeId, string Name, string Kind, string Requirement, string Status,
    string? Outcome, bool Proven, bool Applicable, long Revision, Guid? TaskId, Guid? WorkerRunId, Guid? EvaluationAttemptId,
    string? Prompt, Guid? ReviewerPrincipalId, bool CanDecide, DateTimeOffset? DecisionExpiresAt, DateTimeOffset? StartedAt, DateTimeOffset? SettledAt, WorkflowGoalLink? GoalLink);
public sealed record WorkflowSourceLinks(Guid? SessionId, Guid? RunId, Guid? AutomationId, Guid? InstanceId);
public sealed record WorkflowExecutionView(Guid ExecutionId, Guid WorkflowId, int Version, string GraphHash, string Status,
    string SourceKind, string? ReasonCode, long Revision, long SecurityRevision, DateTimeOffset CreatedAt, DateTimeOffset DeadlineAt,
    DateTimeOffset? TerminalAt, bool CancellationRequested, bool CanCancel, int RequiredCount, int ProvenCount,
    IReadOnlyList<WorkflowStepView> Steps, WorkflowSourceLinks SourceLinks, WorkflowGoalLink? SuccessGoal, string WorkflowName);

public sealed partial class TenantWorkflowExecutionService
{
    public async ValueTask<WorkflowExecutionView> ReadClaimedAsync(TenantRuntimeFrame frame, Guid execution, CancellationToken ct = default)
    {
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var state = await security.ReadTenantAsync(tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var value = await processes.FindWorkflowExecutionAsync(tenant, execution, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var run = await runs.FindTenantRunAsync(tenant, frame.RunId, ct) ?? throw new AccessDeniedException("run_unavailable");
        var session = await sessions.FindTenantSessionAsync(tenant, frame.SessionId, ct) ?? throw new AccessDeniedException("session_unavailable");
        var (agent, current) = await configurations.CurrentPolicyAsync(tenant, frame.Owner.AgentInstanceId, ct);
        var actor = new TaskActor(Agent: frame, Tool: "workflows.status");
        await TaskAuthority.SourceAsync(state, actor, session, agent, run, current, iam, Now, "workflows.monitor", false, ct, WorkflowCatalogAuthority.Scope(tenant, value.WorkflowId));
        if (!await WorkflowReadRules.CanReadAsync(state, actor.PrincipalId, value, iam, sessions, runs, ct)) throw new AccessDeniedException("resource_unavailable");
        var view = await View(state, value, actor, ct, run);
        if ((await security.ReadTenantAsync(tenant, ct))?.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        if (!await WorkflowReadRules.CanReadAsync(state, actor.PrincipalId, value, iam, sessions, runs, ct)) throw new AccessDeniedException("resource_unavailable");
        return view;
    }
    public async ValueTask<WorkflowExecutionView> ReadHumanAsync(Guid tenant, Guid execution, HumanSessionReference human, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct);
        var value = await processes.FindWorkflowExecutionAsync(tenant, execution, ct) ?? throw new AccessDeniedException("resource_unavailable");
        if (!await WorkflowReadRules.CanReadAsync(state, human.PrincipalId, value, iam, sessions, runs, ct)) throw new AccessDeniedException("resource_unavailable");
        var result = await View(state, value, new(Human: human), ct);
        if ((await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct)).Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        if (!await WorkflowReadRules.CanReadAsync(state, human.PrincipalId, value, iam, sessions, runs, ct)) throw new AccessDeniedException("resource_unavailable");
        return result;
    }
    public async ValueTask<WorkflowPage<WorkflowExecutionView>> ListHumanAsync(Guid tenant, HumanSessionReference human, int page, int size, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct);
        var rows = await processes.ListWorkflowExecutionsAsync(tenant, null, page, size, ct, human.PrincipalId);
        var values = new List<WorkflowExecutionView>(); foreach (var row in rows.Items) values.Add(await View(state, row, new(Human: human), ct));
        if ((await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct)).Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        foreach (var row in rows.Items) if (!await WorkflowReadRules.CanReadAsync(state, human.PrincipalId, row, iam, sessions, runs, ct)) throw new SecurityConflictException("workflow_sources_changed");
        return new(values, rows.Total, page, size);
    }
    private async ValueTask<WorkflowExecutionView> View(TenantSecurityState state, WorkflowExecution value, TaskActor actor, CancellationToken ct, AgentCore.Domain.Conversation.AgentRun? reader = null)
    {
        var version = await catalog.FindWorkflowVersionAsync(value.TenantId, value.WorkflowId, value.Version, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var steps = await processes.ReadWorkflowStepsAsync(value.TenantId, value.ExecutionId, ct); var graph = version.Graph.Read();
        var proof = WorkflowProofRules.Evaluate(version, steps); var views = new List<WorkflowStepView>();
        async ValueTask<bool> Allowed(string permission, AccessScope scope) => (await iam.CheckOwnedTenantAsync(state, actor.PrincipalId, PermissionCatalog.Get(permission), scope, ct)).Allowed;
        async ValueTask<WorkflowGoalLink?> GoalLink(Guid session, Guid goal, long plan)
        {
            try { _ = await Evidence.ReadGoalBindingAsync(state, actor.PrincipalId, session, goal, plan, ct); return new(session, goal, plan); }
            catch (AccessDeniedException) { return null; }
        }
        foreach (var node in graph.Nodes)
        {
            var step = steps.SingleOrDefault(s => s.NodeId == node.Id); Guid? taskId = null, runId = null, evaluationId = null;
            if (step?.TaskId is { } task && await tasks.FindTaskAsync(value.TenantId, task, ct) is { } saved
                && await TaskAuthority.CanReadAsync(state, actor, saved, iam, ct, reader)) taskId = task;
            if (taskId is not null && step?.WorkerRunId is { } run && await runs.FindTenantRunAsync(value.TenantId, run, ct) is { } worker
                && await Allowed("agent-runs.read", TenantRunRules.Scope(value.TenantId, run))
                && await Allowed("sessions.read", TenantSessionRules.Scope(value.TenantId, worker.SessionId))) runId = run;
            if (step?.EvaluationAttemptId is { } attempt && node.Verification?.SuiteId is { } suite
                && await Allowed("evaluations.read", AccessScope.ForResource(value.TenantId, "evaluation-suite", suite.ToString("D"))))
            {
                // An Evaluation link needs the same independently visible Task/Run sources as its subject.
                var source = steps.SingleOrDefault(s => s.NodeId == node.Verification.TaskNodeId);
                if (source?.TaskId is { } sourceTask && await tasks.FindTaskAsync(value.TenantId, sourceTask, ct) is { } savedTask
                    && await TaskAuthority.CanReadAsync(state, actor, savedTask, iam, ct, reader)
                    && source.WorkerRunId is { } subject && await Allowed("agent-runs.read", TenantRunRules.Scope(value.TenantId, subject))
                    && savedTask.WorkerSessionId is { } session && await Allowed("sessions.read", TenantSessionRules.Scope(value.TenantId, session))) evaluationId = attempt;
            }
            var expiry = node.Decision is { } decision && step is not null ? step.CreatedAt.AddHours(decision.TimeoutHours) : (DateTimeOffset?)null;
            if (expiry > value.DeadlineAt) expiry = value.DeadlineAt;
            var canDecide = actor.Human is not null && node.Decision?.ReviewerPrincipalId == actor.PrincipalId && step?.Status == WorkflowOperationStatus.Reserved
                && !WorkflowExecutionRules.Terminal(value.Status) && value.SourceClosure is null && !value.CancellationRequested && expiry > Now
                && await Allowed("workflows.decide", WorkflowCatalogAuthority.Scope(value.TenantId, value.WorkflowId));
            var applicable = node.Requirement == WorkflowRequirement.Global || proof.ApplicableRequired.Contains(node.Id);
            var proven = step?.ProofAccepted == true || node.Kind == WorkflowNodeKind.AgentTask && step?.Outcome == "succeeded"
                && graph.Nodes.Any(n => n.Verification?.TaskNodeId == node.Id && steps.Any(s => s.NodeId == n.Id && s.ProofAccepted && s.Outcome == "passed"));
            var status = step is null ? WorkflowExecutionRules.Terminal(value.Status) && !proof.RouteNodes.Contains(node.Id) ? "NotTaken" : "NotStarted"
                : step.Status >= WorkflowOperationStatus.Settled ? step.Outcome == "reject" ? "Rejected"
                    : step.Outcome is "failed" ? "Failed" : proven || node.Kind == WorkflowNodeKind.Branch ? "Verified" : "Waiting"
                : node.Kind is WorkflowNodeKind.HumanDecision or WorkflowNodeKind.Verify || value.Status == WorkflowExecutionStatus.WaitingForOperationApproval ? "Waiting"
                : value.Status == WorkflowExecutionStatus.NeedsAttention ? "Unknown" : "Active";
            if (node.Kind == WorkflowNodeKind.Start && value.Status != WorkflowExecutionStatus.PendingAdmission) status = "Verified";
            if (node.Kind == WorkflowNodeKind.EndFailure && value.Status == WorkflowExecutionStatus.Failed && proof.RouteNodes.Contains(node.Id)) status = "Failed";
            views.Add(new(node.Id, node.Name, node.Kind.ToString(), node.Requirement.ToString(), status, step?.Outcome,
                proven, applicable, step?.Revision ?? 0, taskId, runId, evaluationId,
                node.Decision?.Prompt, node.Decision?.ReviewerPrincipalId, canDecide, expiry, step?.CreatedAt, step?.SettledAt,
                node.Verification is { Kind: WorkflowVerificationKind.GoalCheckpoint, SessionId: { } gs, GoalId: { } gid, PlanRevision: { } plan } ? await GoalLink(gs, gid, plan) : null));
        }
        var canCancel = actor.Human is not null && !WorkflowExecutionRules.Terminal(value.Status) && value.SourceClosure is null && !value.CancellationRequested
            && await Allowed("workflows.cancel", WorkflowCatalogAuthority.Scope(value.TenantId, value.WorkflowId));
        Guid? sourceInstance = null;
        if (value.Source.InvokingInstanceId is { } instance && await Allowed("agents.read", AccessScope.ForResource(value.TenantId, "agent", instance.ToString("D")))) sourceInstance = instance;
        else if (value.Source.SessionId is { } sourceSession && await sessions.FindTenantSessionAsync(value.TenantId, sourceSession, ct) is { } source
            && await Allowed("agents.read", AccessScope.ForResource(value.TenantId, "agent", source.Owner.AgentInstanceId.ToString("D")))) sourceInstance = source.Owner.AgentInstanceId;
        var sourceLinks = new WorkflowSourceLinks(value.Source.SessionId, value.Source.RunId, value.Source.AutomationId, sourceInstance);
        return new(value.ExecutionId, value.WorkflowId, value.Version, value.GraphHash, value.Status.ToString(), value.Source.Kind.ToString(), value.ReasonCode,
            value.Revision, state.Tenant.Revision, value.CreatedAt, value.DeadlineAt, value.TerminalAt, value.CancellationRequested, canCancel,
            proof.ApplicableRequired.Count, proof.ProvenRequired.Count, views, sourceLinks, graph.SuccessGoal is { } success ? await GoalLink(success.SessionId, success.GoalId, success.PlanRevision) : null,
            (await catalog.FindWorkflowAsync(value.TenantId, value.WorkflowId, ct))?.Name ?? "Workflow");
    }
}
