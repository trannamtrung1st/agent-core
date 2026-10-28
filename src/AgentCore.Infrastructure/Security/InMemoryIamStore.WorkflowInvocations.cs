using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Application.Workflows;
using AgentCore.Application.Agents;
using AgentCore.Domain.Workflows;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantWorkflowInvocationStore
{
    private void RequireWorkflowParticipants(AgentCore.Domain.Security.TenantSecurityState state, WorkflowExecution execution, WorkflowVersion version, CancellationToken ct)
    {
        var actor = execution.CoordinatorInstanceId is null ? execution.Source.PrincipalId : WorkflowEvidence().EvaluatorAsync(execution, ct).GetAwaiter().GetResult();
        WorkflowEvidence().RequireVerificationBindingsAsync(state, actor, version.Graph.Read(), ct).GetAwaiter().GetResult();
        var nodes = version.Graph.Read().Nodes.Where(n => n.Kind == WorkflowNodeKind.AgentTask).ToArray(); if (nodes.Length == 0) return;
        WorkflowParticipantPolicy.RequireAsync(state, execution, new(this), ct).GetAwaiter().GetResult();
        var coordinator = execution.CoordinatorInstanceId is { } id ? FindTenantAgentAsync(execution.TenantId, id, ct).Result : null;
        if (coordinator is null) throw new AccessDeniedException("workflow_coordinator_unavailable");
        var current = InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(coordinator), coordinator.Instance.SettingsOverrides);
        foreach (var node in nodes)
        {
            var target = FindTenantAgentAsync(execution.TenantId, node.Task!.TargetInstanceId, ct).Result ?? throw new AccessDeniedException("task_target_unavailable");
            WorkflowTaskRules.RequireCoordinatorAsync(state, execution, coordinator, current, target, new(this), ct).GetAwaiter().GetResult();
        }
    }
    public ValueTask RequireWorkflowParticipantsAsync(WorkflowExecution execution, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var state = _tenants.GetValueOrDefault(execution.TenantId) ?? throw new AccessDeniedException("tenant_unavailable");
            var version = workflowVersions.GetValueOrDefault((execution.TenantId, execution.WorkflowId, execution.Version)) ?? throw new AccessDeniedException("workflow_version_unavailable");
            RequireWorkflowParticipants(state, execution, version, ct); return ValueTask.CompletedTask;
        }
    }
    private void RequireClaimedWorkflowOrigin(AgentCore.Domain.Security.TenantSecurityState state, WorkflowExecution execution, CancellationToken ct)
    {
        var source = execution.Source;
        if (source.Kind == WorkflowInvocationKind.AutomationOccurrence) { RequireAutomationWorkflowOrigin(state, execution, ct); return; }
        if (source.Kind != WorkflowInvocationKind.ClaimedAgent || source.RunId is not { } runId || source.InvokingInstanceId is not { } instance)
            throw new AccessDeniedException("workflow_source_not_qualified");
        var run = sessionBacking.ReadTenantRun(execution.TenantId, runId) ?? throw new AccessDeniedException("workflow_source_unavailable");
        var agent = FindTenantAgentAsync(execution.TenantId, instance, ct).Result ?? throw new AccessDeniedException("workflow_source_unavailable");
        var root = TenantExecutionLineage.RootAsync(run, this, ct).GetAwaiter().GetResult();
        WorkflowInvocationAuthority.RequireClaimedRecordAsync(state, execution, run, root, agent,
            InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides), this, new(this), ct).GetAwaiter().GetResult();
    }
    public ValueTask RequireWorkflowInvocationSourceAsync(WorkflowExecution execution, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        { RequireClaimedWorkflowOrigin(_tenants.GetValueOrDefault(execution.TenantId) ?? throw new AccessDeniedException("tenant_unavailable"), execution, ct); return ValueTask.CompletedTask; }
    }
    public ValueTask<WorkflowExecution> StartClaimedWorkflowAsync(ClaimedWorkflowStart command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var value = command.Execution;
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var state = _tenants.GetValueOrDefault(value.TenantId) ?? throw new AccessDeniedException("tenant_unavailable");
            var version = workflowVersions.GetValueOrDefault((value.TenantId, value.WorkflowId, value.Version)) ?? throw new AccessDeniedException("workflow_version_unavailable");
            WorkflowExecutionRules.Validate(value, version);
            if (value.Source.Kind != WorkflowInvocationKind.ClaimedAgent || value.Source.RunId != command.Frame.RunId || value.Source.SessionId != command.Frame.SessionId
                || value.Revision != 1 || value.Status != WorkflowExecutionStatus.PendingAdmission) throw new AccessDeniedException("workflow_source_mismatch");
            var (_, run, _) = RequireTaskSource(value.TenantId, command.Frame.SessionId, new(Agent: command.Frame, Tool: "workflows.start"), "workflows.start", ct,
                WorkflowCatalogAuthority.Scope(value.TenantId, value.WorkflowId));
            RequireClaimedWorkflowOrigin(state, value, ct);
            WorkflowExecutionAuthority.RequireSourceAsync(state, value, version, this, this, new(this), TaskNow, ct).GetAwaiter().GetResult();
            TaskAuthority.RequireAsync(state, value.Source.PrincipalId, "workflows.read", WorkflowCatalogAuthority.Scope(value.TenantId, value.WorkflowId), new(this), ct).GetAwaiter().GetResult();
            var previous = workflowExecutions.Values.SingleOrDefault(e => e.TenantId == value.TenantId && e.Source.RequestScope == value.Source.RequestScope && e.RequestKey == value.RequestKey);
            if (previous is not null) { if (previous.Fingerprint != value.Fingerprint) throw new SecurityConflictException("workflow_request_conflict"); return ValueTask.FromResult(previous); }
            if (workflowDefinitions.GetValueOrDefault((value.TenantId, value.WorkflowId)) is not { Retired: false }) throw new AccessDeniedException("workflow_version_unavailable");
            RequireWorkflowParticipants(state, value, version, ct);
            if (state.Tenant.Revision != command.ExpectedSecurityRevision) throw new SecurityConflictException("stale_revision");
            if (workflowExecutions.Values.Count(e => e.TenantId == value.TenantId && !WorkflowExecutionRules.Terminal(e.Status)) >= 32
                || workflowExecutions.Values.Count(e => e.TenantId == value.TenantId && e.Source.RunId == run!.AgentRunId) >= AgentCore.Domain.Tasks.TaskLimits.MaxChildrenPerRun)
                throw new SecurityConflictException("workflow_capacity_exceeded");
            if (value.SourceBudget is not null)
                WorkflowBudgetRules.RequireReservation(value, run!, TaskBudgetRules.Allowance(run!, delegatedTasks.Values.Where(t => t.Source.TenantId == value.TenantId).ToArray(),
                    id => sessionBacking.ReadRetainedTenantOperationRun(value.TenantId, id), WorkflowBudgetRules.Charge(run!, workflowExecutions.Values)), TaskNow);
            workflowExecutions.Add((value.TenantId, value.ExecutionId), value); BumpTaskSecurity(state);
            _audit.Add(new(command.AuditId, value.TenantId, value.Source.PrincipalId, "workflows.start", value.ExecutionId.ToString("D"), "allowed", "claimed_run", TaskNow));
            return ValueTask.FromResult(value);
        }
    }
}
