using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    private void RequireAutomationInvocation(TenantAutomationMutationObservation source, PreparedAutomationMutation value,
        TenantAutomationAdmission? admission, CancellationToken ct)
    {
        if (value.Invocation is null)
        { if (admission is not null) throw new SecurityConflictException("operation_mismatch"); return; }
        TenantAutomationInvocationRules.RequireAdmission(source, value, admission);
        if (automationBacking.TenantBusy(value.Proposed.AutomationId)) throw new SecurityConflictException("automation_in_use");
        if (automationBacking.ReadTenantOccurrence(source.Reader.Security.Tenant.TenantId, admission!.Occurrence.OccurrenceId) is not null)
            throw new SecurityConflictException("identity_conflict");
        RequireAutomationConversationAdmission(source.Reader.Security, admission, TenantAutomationInvocationRules.Requester(source), null, false, ct);
    }
}
