using AgentCore.Domain.Conversation;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Ports;

public sealed record TenantContextSwitch(TenantSessionRoot Owner, TenantSessionInitiator? Human, Guid TransitionId, Guid SpaceId,
    Guid? EnvironmentId, long EnvironmentSelectionEpoch, long ExpectedSecurityRevision, long ExpectedSessionRevision,
    long ExpectedSelectionRevision, long ExpectedInstanceRevision, TenantRuntimeFrame? Agent = null, string? Tool = null)
{
    public Guid ActorPrincipalId => Human?.PrincipalId ?? Agent?.Owner.ExecutorPrincipalId ?? throw new InvalidOperationException("Context actor is required.");
}
public sealed record TenantContextSwitchResult(SessionContextSelection Selection, long SessionRevision, long SecurityRevision, string State);

// Facets of the existing Session transaction and mailbox, never a second selection store.
public interface ITenantSessionContextStore
{
    ValueTask<TenantContextSwitchResult> SwitchTenantQuiescentContextAsync(TenantContextSwitch request, Guid auditId, CancellationToken ct = default);
    ValueTask<TenantContextSwitchResult> SaveTenantRuntimeContextAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision,
        TenantContextSwitch request, Guid auditId, CancellationToken ct = default);
}
public interface ITenantSessionContextControl
{
    ValueTask<TenantContextSwitchResult> SwitchContextAsync(TenantContextSwitch request, CancellationToken ct = default);
}
public interface ITenantRuntimeContextWriter
{
    ValueTask<TenantContextSwitchResult> SaveContextAsync(SessionSnapshot snapshot, long expectedRevision, TenantContextSwitch request, CancellationToken ct);
}
