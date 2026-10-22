using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    private async ValueTask RequireAutomationInvocation(AgentCoreDbContext db, TenantAutomationMutationObservation source, PreparedAutomationMutation value,
        TenantAutomationAdmission? admission, CancellationToken ct)
    {
        if (value.Invocation is null)
        { if (admission is not null) throw new SecurityConflictException("operation_mismatch"); return; }
        TenantAutomationInvocationRules.RequireAdmission(source, value, admission);
        if (await TenantAutomationBusy(db, source.Reader.Security.Tenant.TenantId, value.Proposed.AutomationId, ct).ConfigureAwait(false)) throw new SecurityConflictException("automation_in_use");
        if (await db.TriggerOccurrences.IgnoreQueryFilters().AnyAsync(o => o.OccurrenceId == admission!.Occurrence.OccurrenceId.ToString("D"), ct).ConfigureAwait(false))
            throw new SecurityConflictException("identity_conflict");
        await RequireAutomationConversationAdmission(db, source.Reader.Security, admission!, TenantAutomationInvocationRules.Requester(source), null, false, ct).ConfigureAwait(false);
    }
}
