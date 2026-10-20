using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public sealed partial class TenantAutomationService
{
    public async ValueTask<TenantAutomationAdmissionResult?> AdmitEventAsync(TenantEventDelivery source, CancellationToken ct = default)
    {
        var current = await EventRegistration(source.TenantId, source.Subscription, ct);
        var state = await security.ReadTenantAsync(source.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        return await Admit(current, state, null, null, false, ct, eventDelivery: source);
    }
    public async ValueTask<TenantAutomationAdmissionResult?> AdmitEventAsync(TenantEventBucket source, CancellationToken ct = default)
    {
        var current = await EventRegistration(source.TenantId, source.Bucket.Subscription, ct);
        var state = await security.ReadTenantAsync(source.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        return await Admit(current, state, null, null, false, ct, eventBucket: source);
    }
    private async ValueTask<Automation> EventRegistration(Guid tenant, AgentCore.Domain.Events.EventSubscriptionSnapshot source, CancellationToken ct)
    {
        var value = await store.FindTenantAutomationAsync(tenant, source.AutomationId, ct) ?? throw new AccessDeniedException("event_subscription_changed");
        _ = TenantEventAdmissionRules.Child(value, source);
        if (value.ExpiresAtUtc <= TriggerScheduleCalculator.Truncate(time.GetUtcNow())) throw new AccessDeniedException("automation_expired");
        return value;
    }
}
