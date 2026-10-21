using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private TenantSession? RequireAutomationConversation(TenantSecurityState state, AgentCore.Domain.Triggers.Automation value,
        Guid? human, PreparedAutomationConversation? witness, CancellationToken ct)
    {
        if (!TenantAutomationConversationRules.Existing(value)) return null;
        var target = TenantAutomationConversationRules.Require(value,
            sessionBacking.ReadTenantSession(state.Tenant.TenantId, value.ExecutionTarget.SessionId!.Value), witness);
        if (human is { } principal) TenantAutomationConversationRules.AuthoringAsync(state, principal, value, target, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
        return target;
    }
    private void RequireAutomationConversationAdmission(TenantSecurityState state, TenantAutomationAdmission admission, Guid? human,
        PreparedAutomationAdmission? consent, bool preparing, CancellationToken ct)
    {
        var target = RequireAutomationConversation(state, admission.Current, human, consent?.Conversation, ct);
        if (target is null) return;
        TenantRequestIsolationRules.AutomationRoot(target.Snapshot, admission.Run, sessionBacking.AgentRuns.Runs.Values
            .Where(r => r.Owner == admission.Run.Owner && r.SessionId == target.Owner.SessionId).ToArray());
        if (target.Owner != admission.SessionOwner || target.Snapshot.Revision != admission.ExpectedSessionRevision
            || JsonSerializer.Serialize(target.Snapshot) != JsonSerializer.Serialize(admission.Session)) throw new SecurityConflictException("operation_witness_changed");
        if (!preparing && (target.Snapshot.PendingAgentInputIds.Count > 0
            || sessionBacking.AgentRuns.Runs.Values.Any(r => r.SessionId == target.Owner.SessionId && !r.IsTerminal))) throw new SecurityConflictException("parent_busy");
    }
    private void StageTenantAutomationGraph(TenantAutomationAdmission admission)
    {
        if (!TenantAutomationConversationRules.Existing(admission.Current))
        { StageTenantBackgroundGraph(admission.SessionOwner, admission.Session, admission.Run); return; }
        var all = sessionBacking.AgentRuns; var run = admission.Run;
        if (all.Runs.ContainsKey(run.AgentRunId) || all.Activations.ContainsKey(run.ActivationId)
            || all.ByAdmission.ContainsKey((run.SessionId, run.Admission.Activation.DedupeKey))) throw new SecurityConflictException("identity_conflict");
        var activation = AgentRunStoreMapping.ToActivationRecord(admission.Session, run);
        all.Runs.Add(run.AgentRunId, run); all.Activations.Add(run.ActivationId, activation);
        all.ByAdmission.Add((run.SessionId, run.Admission.Activation.DedupeKey), run.AgentRunId);
    }
}
