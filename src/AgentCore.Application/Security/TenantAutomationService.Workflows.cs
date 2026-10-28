using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public sealed partial class TenantAutomationService
{
    private async ValueTask<TenantAutomationAdmissionResult?> AdmitWorkflow(Automation current, TenantSecurityState state, TenantAgent agent,
        AgentDefinition policy, HumanSessionReference? human, bool manual, PreparedEventAdmissionSource? source,
        TenantEventDelivery? delivery, TenantEventBucket? bucket, long? sourceRevision, DateTimeOffset at, CancellationToken ct)
    {
        if (workflows is null || workflowRuntime is null || security is not ITenantWorkflowInvocationStore admissions
            || !await workflowRuntime.IsReadyAsync(ct)) throw new AccessDeniedException("workflow_runtime_unavailable");
        var destination = current.ExecutionTarget.Workflow!;
        var version = await workflows.EligibleAgentVersionAsync(agent.TenantId, destination.WorkflowId, destination.Version, agent.PrincipalId, ct);
        await WorkflowAutomationRules.AuthorityAsync(state, current, agent, policy, human?.PrincipalId, source, iam, ct, emailAccounts: delivery is not null ? TenantEventAdmissionRules.EmailAccounts(delivery) : bucket is not null ? TenantEventAdmissionRules.EmailAccounts(bucket) : []);
        await WorkflowAutomationRules.DestinationAsync(state, current, version, agent.PrincipalId, iam, ct);
        TriggerOccurrence occurrence; Automation next;
        if (manual)
        {
            occurrence = AutomationRules.ManualOccurrence(current, null, at).WithWorkflowInitiator(human!.PrincipalId);
            next = current.WithScheduleAdvance(current.Status, current.NextOccurrenceAtUtc, current.OccurrenceCount, current.Revision + 1, at, null);
        }
        else if (source is not null)
        {
            occurrence = delivery is not null ? TenantEventAdmissionRules.Occurrence(current, delivery, null, at, sourceRevision)
                : TenantEventAdmissionRules.Occurrence(current, bucket!, null, at, sourceRevision);
            next = TenantEventAdmissionRules.Next(current, at);
        }
        else
        {
            var decision = TriggerScheduleAdmission.Decide(current, at); if (decision.Kind != ScheduleAdmissionKind.Admit) return null;
            occurrence = TriggerScheduleAdmission.CreateOccurrence(current, decision, at); next = TriggerScheduleAdmission.Advance(current, decision, at);
        }
        var execution = WorkflowAutomationRules.Create(occurrence, version, ids.NewId());
        var accepted = await admissions.AdmitAutomationWorkflowAsync(new(current, next, occurrence, execution, agent.Instance.Revision, source),
            state.Tenant.Revision, human, ids.NewId(), ct);
        if (accepted is not null) AgentCore.Application.Observability.WorkflowTelemetry.Record(accepted.Created
            ? AgentCore.Application.Observability.WorkflowEvent.Accepted : AgentCore.Application.Observability.WorkflowEvent.Replayed, execution);
        return accepted;
    }
}
