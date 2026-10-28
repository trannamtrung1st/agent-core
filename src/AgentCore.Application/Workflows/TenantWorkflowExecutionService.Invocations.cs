using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public sealed partial class TenantWorkflowExecutionService
{
    public async ValueTask<WorkflowExecution> StartClaimedAsync(TenantRuntimeFrame frame, Guid workflow, WorkflowStart request, CancellationToken ct = default)
    {
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var state = await security.ReadTenantAsync(tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var run = await runs.FindTenantRunAsync(tenant, frame.RunId, ct) ?? throw new AccessDeniedException("run_unavailable");
        var session = await sessions.FindTenantSessionAsync(tenant, frame.SessionId, ct) ?? throw new AccessDeniedException("session_unavailable");
        var (agent, current) = await configurations.CurrentPolicyAsync(tenant, frame.Owner.AgentInstanceId, ct);
        await TaskAuthority.SourceAsync(state, new(Agent: frame, Tool: "workflows.start"), session, agent, run, current, iam, Now,
            "workflows.start", true, ct, WorkflowCatalogAuthority.Scope(tenant, workflow));
        _ = WorkflowCatalogRules.RequestKey(request.RequestKey);
        var previous = await processes.FindWorkflowStartRequestAsync(tenant, "run:" + run.AgentRunId.ToString("D"), request.RequestKey, ct);
        if (previous is not null && (previous.WorkflowId != workflow || previous.Version != request.Version)) throw new SecurityConflictException("workflow_request_conflict");
        var version = previous is null ? await catalogs.EligibleAgentVersionAsync(tenant, workflow, request.Version, agent.PrincipalId, ct)
            : await catalog.FindWorkflowVersionAsync(tenant, workflow, request.Version, ct) ?? throw new AccessDeniedException("workflow_version_unavailable");
        var graph = version.Graph.Read(); var hasTasks = graph.Nodes.Any(n => n.Kind == WorkflowNodeKind.AgentTask);
        if (!graph.AllowedOrigins.Contains(WorkflowInvocationKind.ClaimedAgent)
            || hasTasks && !graph.CoordinatorInstanceIds.Contains(agent.Instance.InstanceId)
            || request.CoordinatorInstanceId is { } coordinator && coordinator != agent.Instance.InstanceId
            || request.SourceSessionId is { } selectedSession && selectedSession != frame.SessionId)
            throw new AccessDeniedException("workflow_invocation_unavailable");
        var inputs = WorkflowExecutionRules.Inputs(graph, request.Inputs); _ = WorkflowCatalogRules.RequestKey(request.RequestKey);
        var fingerprint = WorkflowExecutionRules.Hash(JsonSerializer.Serialize(new { workflow, request.Version, version.Graph.Hash, inputs,
            frame.RunId, frame.SessionId, Coordinator = hasTasks ? (Guid?)agent.Instance.InstanceId : null, request.DeadlineAt }));
        var root = await TenantExecutionLineage.RootAsync(run, runs, ct);
        var now = Now; var value = new WorkflowExecution(tenant, ids.NewId(), workflow, version.Version, version.Graph.Hash,
            new(WorkflowInvocationKind.ClaimedAgent, run.AgentRunId, agent.PrincipalId, run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId,
                SessionId: run.SessionId, RunId: run.AgentRunId, InvokingInstanceId: agent.Instance.InstanceId, RootActivationId: run.Admission.Activation.RootActivationId,
                Causation: WorkflowCausationRules.FromRoot(root)), hasTasks ? agent.Instance.InstanceId : null,
            inputs, WorkflowExecutionRules.Hash(inputs), request.RequestKey, fingerprint, now, request.DeadlineAt ?? now.AddDays(30), 1,
            SourceBudget: hasTasks ? new(graph.MaxTotalTaskSteps, graph.MaxTotalTaskSeconds) : null);
        WorkflowExecutionRules.Validate(value, version);
        if (security is not ITenantWorkflowInvocationStore invocations) throw new AccessDeniedException("workflow_source_unavailable");
        // The owning transaction rechecks the real claim and returns an existing request before reserving additional credit.
        if (previous is null && !await runtime.IsReadyAsync(ct)) throw new AccessDeniedException("workflow_runtime_unavailable");
        foreach (var node in graph.Nodes.Where(n => n.Kind == WorkflowNodeKind.AgentTask)) _ = WorkflowTaskRules.Objective(value, node);
        return await invocations.StartClaimedWorkflowAsync(new(frame, value, request.ExpectedSecurityRevision, ids.NewId()), ct);
    }
}
