using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Application.Workflows;
using AgentCore.Application.Agents;
using AgentCore.Domain.Workflows;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantWorkflowInvocationStore
{
    private async Task RequireWorkflowParticipants(AgentCoreDbContext db, AgentCore.Domain.Security.TenantSecurityState state, WorkflowExecution execution, WorkflowVersion version, CancellationToken ct)
    {
        var actor = execution.CoordinatorInstanceId is null ? execution.Source.PrincipalId : await WorkflowEvidence().EvaluatorAsync(execution, ct);
        await WorkflowEvidence().RequireVerificationBindingsAsync(state, actor, version.Graph.Read(), ct);
        var nodes = version.Graph.Read().Nodes.Where(n => n.Kind == WorkflowNodeKind.AgentTask).ToArray(); if (nodes.Length == 0) return;
        await WorkflowParticipantPolicy.RequireAsync(state, execution, new(this), ct);
        var coordinatorRow = await OwnedAgents(db, execution.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == execution.CoordinatorInstanceId!.Value.ToString("D"), ct)
            ?? throw new AccessDeniedException("workflow_coordinator_unavailable"); var coordinator = Owned(coordinatorRow);
        var current = InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, coordinator, ct), coordinator.Instance.SettingsOverrides);
        foreach (var node in nodes)
        {
            var targetRow = await OwnedAgents(db, execution.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == node.Task!.TargetInstanceId.ToString("D"), ct)
                ?? throw new AccessDeniedException("task_target_unavailable");
            await WorkflowTaskRules.RequireCoordinatorAsync(state, execution, coordinator, current, Owned(targetRow), new(this), ct);
        }
    }
    public async ValueTask RequireWorkflowParticipantsAsync(WorkflowExecution execution, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var state = await ReadTenantAsync(db, execution.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        await RequireWorkflowParticipants(db, state, execution, await WorkflowVersionAt(db, execution, ct), ct);
    }
    private async Task RequireClaimedWorkflowOrigin(AgentCoreDbContext db, AgentCore.Domain.Security.TenantSecurityState state, WorkflowExecution execution, CancellationToken ct)
    {
        var source = execution.Source;
        if (source.Kind == WorkflowInvocationKind.AutomationOccurrence) { await RequireAutomationWorkflowOrigin(db, state, execution, ct); return; }
        if (source.Kind != WorkflowInvocationKind.ClaimedAgent || source.RunId is not { } runId || source.InvokingInstanceId is not { } instance)
            throw new AccessDeniedException("workflow_source_not_qualified");
        var runRow = await OwnedRuns(db, execution.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == runId.ToString("D"), ct)
            ?? throw new AccessDeniedException("workflow_source_unavailable");
        var run = AgentRunStoreMapping.ToDomain(runRow);
        var agentRow = await OwnedAgents(db, execution.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == instance.ToString("D"), ct)
            ?? throw new AccessDeniedException("workflow_source_unavailable"); var agent = Owned(agentRow);
        var root = await TenantCoreEventProduction.RootAsync(db, run, ct);
        await WorkflowInvocationAuthority.RequireClaimedRecordAsync(state, execution, run, root, agent,
            InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct), agent.Instance.SettingsOverrides), this, new(this), ct);
    }
    public async ValueTask RequireWorkflowInvocationSourceAsync(WorkflowExecution execution, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await RequireClaimedWorkflowOrigin(db, await ReadTenantAsync(db, execution.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable"), execution, ct);
    }
    public async ValueTask<WorkflowExecution> StartClaimedWorkflowAsync(ClaimedWorkflowStart command, CancellationToken ct = default)
    {
        var value = command.Execution; await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var state = await ReadTenantAsync(db, value.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var version = await WorkflowVersionAt(db, value, ct); WorkflowExecutionRules.Validate(value, version);
        if (value.Source.Kind != WorkflowInvocationKind.ClaimedAgent || value.Source.RunId != command.Frame.RunId || value.Source.SessionId != command.Frame.SessionId
            || value.Revision != 1 || value.Status != WorkflowExecutionStatus.PendingAdmission) throw new AccessDeniedException("workflow_source_mismatch");
        var (_, run, _, _) = await RequireTaskSource(db, value.TenantId, command.Frame.SessionId, new(Agent: command.Frame, Tool: "workflows.start"), "workflows.start", ct,
            permissionScope: WorkflowCatalogAuthority.Scope(value.TenantId, value.WorkflowId));
        await RequireClaimedWorkflowOrigin(db, state, value, ct);
        await WorkflowExecutionAuthority.RequireSourceAsync(state, value, version, this, this, new(this), TaskNow, ct);
        await TaskAuthority.RequireAsync(state, value.Source.PrincipalId, "workflows.read", WorkflowCatalogAuthority.Scope(value.TenantId, value.WorkflowId), new(this), ct);
        var previous = await db.Set<WorkflowExecutionRow>().SingleOrDefaultAsync(r => r.TenantId == value.TenantId && r.SourceScope == value.Source.RequestScope && r.RequestKey == value.RequestKey, ct);
        if (previous is not null) { var saved = WorkflowPersistence.Read<WorkflowExecution>(previous.Json); if (saved.Fingerprint != value.Fingerprint) throw new SecurityConflictException("workflow_request_conflict"); return saved; }
        var definition = await db.Set<WorkflowDefinitionRow>().SingleOrDefaultAsync(r => r.TenantId == value.TenantId && r.WorkflowId == value.WorkflowId, ct);
        if (definition is null || WorkflowPersistence.Read<WorkflowDefinition>(definition.Json).Retired) throw new AccessDeniedException("workflow_version_unavailable");
        await RequireWorkflowParticipants(db, state, value, version, ct);
        if (state.Tenant.Revision != command.ExpectedSecurityRevision) throw new SecurityConflictException("stale_revision");
        if (await db.Set<WorkflowExecutionRow>().CountAsync(r => r.TenantId == value.TenantId && r.Status < (int)WorkflowExecutionStatus.Completed, ct) >= 32
            || await db.Set<WorkflowExecutionRow>().CountAsync(r => r.TenantId == value.TenantId && r.SourceScope == value.Source.RequestScope, ct) >= AgentCore.Domain.Tasks.TaskLimits.MaxChildrenPerRun)
            throw new SecurityConflictException("workflow_capacity_exceeded");
        if (value.SourceBudget is not null)
        {
            var facts = await TaskBudgetFacts(db, value.TenantId, run!.Admission.Activation.RootActivationId, ct);
            WorkflowBudgetRules.RequireReservation(value, run, TaskBudgetRules.Allowance(run, facts.Tasks, id => facts.Runs.GetValueOrDefault(id), await WorkflowRootCharge(db, run, ct)), TaskNow);
        }
        db.Add(WorkflowPersistence.Row(value)); await BumpTaskSecurity(db, state, ct);
        db.Add(new AgentCore.Domain.Security.AuthorizationAudit(command.AuditId, value.TenantId, value.Source.PrincipalId, "workflows.start", value.ExecutionId.ToString("D"), "allowed", "claimed_run", TaskNow));
        await SaveTaskChanges(db, ct); await tx.CommitAsync(ct); return value;
    }
}
