using AgentCore.Domain.Triggers;
using AgentCore.Application.Triggers;

namespace AgentCore.Application.Security;

public sealed partial class TenantAutomationService
{
    // Reuse the existing schedule/lifecycle transaction. Its store repeats current context
    // unavailability under CAS; a repaired or edited source cannot be suspended by stale work.
    private async ValueTask SuspendContextAsync(Automation current, string failure, CancellationToken ct)
    {
        var reason = TenantAutomationContextRules.Failure(failure);
        if (reason is null || current.Status != AutomationStatus.Active) return;
        try
        {
            var state = await security.ReadTenantAsync(current.Owner.TenantId!.Value, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("tenant_unavailable");
            var at = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
            var next = current.WithScheduleAdvance(AutomationStatus.SuspendedPolicy, current.NextOccurrenceAtUtc,
                current.OccurrenceCount, checked(current.Revision + 1), at, "context:" + reason);
            await store.TryAdvanceTenantAutomationAsync(Next(state), state.Tenant.Revision, current, next,
                new(ids.NewId(), state.Tenant.TenantId, current.Owner.ExecutorPrincipalId!.Value, "automations.schedule.advance",
                    current.AutomationId.ToString("D"), "allowed", "automation_context_suspended", at), ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is AccessDeniedException or SecurityConflictException) { /* Next pass observes the current source; no fallback admission. */ }
    }
}
