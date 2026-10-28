using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public static class WorkflowAutomationRules
{
    public static async ValueTask DestinationAsync(TenantSecurityState state, Automation automation, WorkflowVersion version,
        Guid actor, IamAuthorizer iam, CancellationToken ct)
    {
        var pin = automation.ExecutionTarget.Workflow ?? throw new AccessDeniedException("workflow_destination_unavailable");
        var graph = version.Graph.Read();
        using var inputs = JsonDocument.Parse(pin.InputsJson);
        if (version.TenantId != automation.Owner.TenantId || version.WorkflowId != pin.WorkflowId || version.Version != pin.Version || version.Graph.Hash != pin.GraphHash
            || !graph.AllowedOrigins.Contains(WorkflowInvocationKind.AutomationOccurrence)
            || graph.Nodes.Any(n => n.Task is not null) && !graph.CoordinatorInstanceIds.Contains(automation.Owner.AgentInstanceId)
            || WorkflowExecutionRules.Inputs(graph, inputs.RootElement) != pin.InputsJson)
            throw new AccessDeniedException("workflow_destination_unavailable");
        foreach (var key in new[] { "workflows.read", "workflows.start" })
            await TaskAuthority.RequireAsync(state, actor, key, WorkflowCatalogAuthority.Scope(version.TenantId, version.WorkflowId), iam, ct);
    }
    public static async ValueTask AuthorityAsync(TenantSecurityState state, Automation automation, TenantAgent agent, AgentDefinition current,
        Guid? requester, PreparedEventAdmissionSource? eventSource, IamAuthorizer iam, CancellationToken ct, bool admission = true, IReadOnlyList<Guid>? emailAccounts = null)
    {
        TenantAutomationRules.Owner(automation, agent); TenantSessionRules.RequireExecutor(state, agent);
        if (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active || !RolePermissions.AllowsTool(current, "workflows.start"))
            throw new AccessDeniedException("workflow_source_unavailable");
        if (admission) TenantAutomationRules.AdmissionSource(state, automation, agent, current, requester, eventSource?.SourceKind);
        var pin = automation.ExecutionTarget.Workflow ?? throw new AccessDeniedException("workflow_destination_unavailable");
        var targets = new[] { ("automations.run", TenantAutomationRules.Scope(agent.TenantId, automation.AutomationId)),
            ("workflows.start", WorkflowCatalogAuthority.Scope(agent.TenantId, pin.WorkflowId)) }.Concat(TenantAutomationAdmissionApprovalRules.SourceTargets(eventSource, emailAccounts));
        foreach (var (key, scope) in targets)
        {
            await TaskAuthority.RequireAsync(state, agent.PrincipalId, key, scope, iam, ct);
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, key, scope, iam, ct))
                throw new AccessDeniedException("workflow_review_required");
        }
    }
    public static WorkflowExecution Create(TriggerOccurrence initial, WorkflowVersion version, Guid id)
    {
        var pin = initial.ExecutionTarget.Workflow ?? throw new AccessDeniedException("workflow_destination_unavailable"); var graph = version.Graph.Read();
        var hasTasks = graph.Nodes.Any(n => n.Task is not null); var at = initial.AdmittedAtUtc;
        var source = new WorkflowInvocationSource(WorkflowInvocationKind.AutomationOccurrence, initial.OccurrenceId, initial.Owner.ExecutorPrincipalId!.Value,
            initial.WorkflowInitiatorPrincipalId, AutomationId: initial.AutomationId, InvokingInstanceId: initial.Owner.AgentInstanceId,
            Causation: WorkflowCausationRules.FromEvidence(initial.EvidenceJson));
        var value = new WorkflowExecution(initial.Owner.TenantId!.Value, id, pin.WorkflowId, pin.Version, pin.GraphHash, source,
            hasTasks ? initial.Owner.AgentInstanceId : null, pin.InputsJson, WorkflowExecutionRules.Hash(pin.InputsJson), "automation-occurrence",
            WorkflowExecutionRules.Hash(JsonSerializer.Serialize(new { initial.OccurrenceId, pin })), at, at.AddDays(30), 1,
            SourceBudget: hasTasks ? new(graph.MaxTotalTaskSteps, graph.MaxTotalTaskSeconds) : null);
        WorkflowExecutionRules.Validate(value, version); return value;
    }
    public static async ValueTask AcceptedAsync(TenantSecurityState state, WorkflowExecution execution, TriggerOccurrence occurrence,
        Automation automation, TenantAgent agent, AgentDefinition policy, IamAuthorizer iam, CancellationToken ct)
    {
        var pin = occurrence.ExecutionTarget.Workflow;
        if (pin is null || occurrence.AcceptedWorkflowExecutionId != execution.ExecutionId || occurrence.Disposition != OccurrenceRoutingDisposition.AcceptedDurable
            || occurrence.Owner.TenantId != execution.TenantId || occurrence.AutomationId != execution.Source.AutomationId || occurrence.OccurrenceId != execution.Source.SourceId
            || occurrence.Owner.ExecutorPrincipalId != execution.Source.PrincipalId || occurrence.Owner.AgentInstanceId != execution.Source.InvokingInstanceId
            || occurrence.WorkflowInitiatorPrincipalId != execution.Source.OriginalHumanPrincipalId || occurrence.Owner != automation.Owner
            || execution.Source.SessionId is not null || execution.Source.RootActivationId is not null || execution.Source.RunId is not null
            || pin.WorkflowId != execution.WorkflowId || pin.Version != execution.Version || pin.GraphHash != execution.GraphHash || pin.InputsJson != execution.InputsJson
            || execution.RequestKey != "automation-occurrence" || execution.Source.Causation != WorkflowCausationRules.FromEvidence(occurrence.EvidenceJson)
            || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active
            || !RolePermissions.AllowsTool(policy, "workflows.start")) throw new AccessDeniedException("workflow_source_unavailable");
        TenantSessionRules.RequireExecutor(state, agent);
        if (occurrence.SourceKind != TriggerSourceKind.ManualInvocation && !OccurrenceCompatibility.Allows(policy, occurrence.SourceKind)) throw new AccessDeniedException("event_policy_unavailable");
        foreach (var (key, scope) in new[] { ("automations.run", TenantAutomationRules.Scope(execution.TenantId, automation.AutomationId)),
            ("workflows.start", WorkflowCatalogAuthority.Scope(execution.TenantId, execution.WorkflowId)) }.Concat(TenantAutomationAdmissionApprovalRules.SourceTargets(EventSource(occurrence), EmailAccounts(automation, occurrence))))
        {
            await TaskAuthority.RequireAsync(state, agent.PrincipalId, key, scope, iam, ct);
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, occurrence.WorkflowInitiatorPrincipalId, key, scope, iam, ct)) throw new AccessDeniedException("workflow_review_required");
        }
        if (occurrence.WorkflowInitiatorPrincipalId is { } human && !state.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active))
            throw new AccessDeniedException("workflow_source_unavailable");
    }
    public static IReadOnlyList<Guid> EmailAccounts(Automation automation, TriggerOccurrence occurrence) =>
        automation.Triggers.SingleOrDefault(t => t.TriggerId == occurrence.TriggerId)?.Configuration is CoreEventTrigger { CoreEventKey: "email.message.received" }
            && EventSource(occurrence) is { } source ? AgentCore.Domain.Email.EmailEventReferences.AccountIdsFromEvidence(occurrence.EvidenceJson, source.IsBucket) : [];
    public static PreparedEventAdmissionSource? EventSource(TriggerOccurrence occurrence)
    {
        if (occurrence.SourceKind is not (TriggerSourceKind.CoreEvent or TriggerSourceKind.ApplicationEvent)) return null;
        using var json = JsonDocument.Parse(occurrence.EvidenceJson);
        return json.RootElement.GetProperty("triggerContext").GetProperty("admissionSource").Deserialize<PreparedEventAdmissionSource>()
            ?? throw new AccessDeniedException("event_source_unavailable");
    }
    public static void Admission(AutomationWorkflowAdmission command, TenantAgent agent, AgentDefinition policy, WorkflowVersion version, Guid? human)
    {
        var current = command.Current; var initial = command.InitialOccurrence; var at = initial.AdmittedAtUtc;
        TenantAutomationRules.Owner(current, agent); TenantAutomationRules.Owner(command.Next, agent);
        if (initial.Owner != current.Owner || initial.AutomationId != current.AutomationId || initial.ExecutionTarget != current.ExecutionTarget
            || initial.Disposition != OccurrenceRoutingDisposition.Pending || initial.RoutingRevision != 0 || initial.ModelPin is not null
            || initial.WorkflowInitiatorPrincipalId != human || (initial.SourceKind == TriggerSourceKind.ManualInvocation) != (human is not null)
            || command.ExpectedInstanceRevision != agent.Instance.Revision) throw new AccessDeniedException("workflow_source_mismatch");
        Automation next; TriggerOccurrence expected;
        if (human is not null)
        { expected = AutomationRules.ManualOccurrence(current, null, at).WithWorkflowInitiator(human); next = current.WithScheduleAdvance(current.Status, current.NextOccurrenceAtUtc, current.OccurrenceCount, current.Revision + 1, at, null); }
        else if (command.EventSource is { } eventSource)
        { TenantEventAdmissionRules.Shape(current, initial, eventSource); expected = initial; next = TenantEventAdmissionRules.Next(current, at); }
        else
        { var decision = TriggerScheduleAdmission.Decide(current, at); if (decision.Kind != ScheduleAdmissionKind.Admit) throw new AccessDeniedException("automation_not_due"); expected = TriggerScheduleAdmission.CreateOccurrence(current, decision, at); next = TriggerScheduleAdmission.Advance(current, decision, at); }
        if (!TenantAutomationRules.Same(next, command.Next) || !TenantAutomationAdmissionApprovalRules.SameOccurrence(initial, expected)
            || Create(initial, version, command.Execution.ExecutionId) != command.Execution) throw new AccessDeniedException("workflow_source_mismatch");
        var cap = ExecutionBudgetResolver.Resolve(policy, agent.Instance.ExecutionBudgets, false);
        if (command.Execution.SourceBudget is { } budget && (budget.MaxSteps > cap.Profile.MaxSteps || budget.DurationSeconds > cap.Profile.DurationSeconds))
            throw new AccessDeniedException("workflow_source_budget_unavailable");
    }
}
