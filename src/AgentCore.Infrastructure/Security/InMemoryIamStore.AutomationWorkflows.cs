using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Workflows;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private bool WorkflowAutomationBusy(Guid tenant, Guid automation) => workflowExecutions.Values.Any(e => e.TenantId == tenant
        && e.Source.AutomationId == automation && !WorkflowExecutionRules.Terminal(e.Status));
    private void RequireAutomationWorkflowOrigin(AgentCore.Domain.Security.TenantSecurityState state, WorkflowExecution execution, CancellationToken ct)
    {
        var occurrence = automationBacking.ReadTenantOccurrence(execution.TenantId, execution.Source.SourceId) ?? throw new AccessDeniedException("workflow_source_unavailable");
        var automation = automationBacking.ReadTenantAutomation(execution.TenantId, occurrence.AutomationId!.Value) ?? throw new AccessDeniedException("workflow_source_unavailable");
        var agent = FindTenantAgentAsync(execution.TenantId, occurrence.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("workflow_source_unavailable");
        WorkflowAutomationRules.AcceptedAsync(state, execution, occurrence, automation, agent,
            InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides), new(this), ct).GetAwaiter().GetResult();
        if (WorkflowAutomationRules.EventSource(occurrence) is { } source)
            foreach (var id in source.EventIds)
            {
                var delivery = TenantEvent(execution.TenantId, source.SourceKind, id, source.TriggerId) ?? throw new AccessDeniedException("event_source_unavailable");
                if (delivery.Subscription.Owner != occurrence.Owner || delivery.Subscription.AutomationId != occurrence.AutomationId) throw new AccessDeniedException("event_source_unavailable");
                TenantEventAdmissionRules.RawReadAsync(state, agent.PrincipalId, delivery.Subscription, [id], new(this), ct, TenantEventAdmissionRules.EmailAccounts(delivery)).GetAwaiter().GetResult();
                if (delivery.Core?.Event.Key == "email.message.received" && !CanDeliverTenantEmailEventAsync(execution.TenantId, id, ct).GetAwaiter().GetResult()) throw new AccessDeniedException("email_event_source_changed");
                if (source.SourceKind == TriggerSourceKind.ApplicationEvent && webhookBacking.TenantSource(execution.TenantId, source.ResourceId!.Value)?.Status != AgentCore.Domain.Events.WebhookEventStatus.Active) throw new AccessDeniedException("event_source_unavailable");
            }
    }
    public ValueTask<TenantAutomationAdmissionResult?> AdmitAutomationWorkflowAsync(AutomationWorkflowAdmission command, long expectedSecurityRevision,
        HumanSessionReference? human, Guid auditId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var value = command.Execution; var tenant = value.TenantId;
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        lock (automationBacking.AdmissionGate) lock (webhookBacking.TenantGate) lock (sessionBacking.CoreEvents.TenantGate)
        {
            var state = _tenants.GetValueOrDefault(tenant) ?? throw new AccessDeniedException("tenant_unavailable");
            if (human is not null) RequireHumanSession(tenant, human);
            if (automationBacking.ReadTenantOccurrence(tenant, command.InitialOccurrence.OccurrenceId) is { } previous)
                return ValueTask.FromResult<TenantAutomationAdmissionResult?>(TenantAutomationRules.Existing(previous));
            if (state.Tenant.Revision != expectedSecurityRevision || automationBacking.ReadTenantAutomation(tenant, command.Current.AutomationId) is not { } saved
                || !TenantAutomationRules.Same(saved, command.Current)) return ValueTask.FromResult<TenantAutomationAdmissionResult?>(null);
            var agent = FindTenantAgentAsync(tenant, saved.Owner.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("agent_unavailable");
            var policy = InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides);
            var version = workflowVersions.GetValueOrDefault((tenant, value.WorkflowId, value.Version)) ?? throw new AccessDeniedException("workflow_version_unavailable");
            WorkflowAutomationRules.Admission(command, agent, policy, version, human?.PrincipalId);
            WorkflowAutomationRules.AuthorityAsync(state, saved, agent, policy, human?.PrincipalId, command.EventSource, new(this), ct, emailAccounts: WorkflowAutomationRules.EmailAccounts(saved, command.InitialOccurrence)).GetAwaiter().GetResult();
            WorkflowAutomationRules.DestinationAsync(state, saved, version, agent.PrincipalId, new(this), ct).GetAwaiter().GetResult();
            if (human is not null) RequireAutomationHuman(state, human.PrincipalId, agent, saved.AutomationId, "automations.run", false, ct);
            if (workflowDefinitions.GetValueOrDefault((tenant, value.WorkflowId)) is not { Retired: false }) throw new AccessDeniedException("workflow_version_unavailable");
            if (automationBacking.TenantBusy(saved.AutomationId) || WorkflowAutomationBusy(tenant, saved.AutomationId)) return ValueTask.FromResult<TenantAutomationAdmissionResult?>(null);
            if (command.EventSource is { } source) RequireTenantEventSource(saved, command.InitialOccurrence, source, false, ct);
            if (workflowExecutions.Values.Count(e => e.TenantId == tenant && !WorkflowExecutionRules.Terminal(e.Status)) >= 32) throw new SecurityConflictException("workflow_capacity_exceeded");
            RequireWorkflowParticipants(state, value, version, ct);
            var accepted = command.InitialOccurrence.WithWorkflowAcceptance(value.ExecutionId, value.CreatedAt);
            workflowExecutions.Add((tenant, value.ExecutionId), value); automationBacking.CommitTenantOccurrence(command.Next, accepted);
            SettleTenantEventSource(command.EventSource, accepted.OccurrenceId, false); BumpTaskSecurity(state);
            _audit.Add(new(auditId, tenant, agent.PrincipalId, "workflows.start", value.ExecutionId.ToString("D"), "allowed", "automation_occurrence", TaskNow));
            return ValueTask.FromResult<TenantAutomationAdmissionResult?>(new(true, accepted, null, null, WorkflowExecutionId: value.ExecutionId));
        }
    }
}
