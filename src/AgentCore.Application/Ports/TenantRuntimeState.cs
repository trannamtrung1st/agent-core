using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

// Server-derived owner and permission-filtered identifiers. Null means fully readable; empty means none.
public sealed record TenantRuntimeStateQuery(AgentRunOwner Owner, IReadOnlyList<Guid>? Sessions,
    IReadOnlyList<Guid>? Runs, IReadOnlyList<string>? SandboxProfiles)
{
    public void Validate()
    {
        if (Owner.TenantId is null || Owner.TenantId == Guid.Empty || Owner.ExecutorPrincipalId is null || Owner.ExecutorPrincipalId == Guid.Empty
            || Owner.AgentInstanceId == Guid.Empty || Owner.ProfileIdOrNull is not null) throw new ArgumentException("A complete tenant execution owner is required.");
    }
}
public interface ITenantRuntimeStateStore
{
    ValueTask<DurableSessionState> ReadTenantSessionStateAsync(TenantRuntimeStateQuery query, CancellationToken ct = default);
    ValueTask<DurableRunState> ReadTenantRunStateAsync(TenantRuntimeStateQuery query, CancellationToken ct = default);
    ValueTask<ResourceStateObservation> ReadTenantSandboxStateAsync(TenantRuntimeStateQuery query, DateTimeOffset observedAt, CancellationToken ct = default);
}
public interface ITenantHostedRuntimeStateReader
{
    // Counts existing tenant Run/Voice lifetimes and recipient subscriptions only. No mailbox mutation or launch.
    HostedSessionState ObserveTenant(TenantRuntimeStateQuery query);
}
