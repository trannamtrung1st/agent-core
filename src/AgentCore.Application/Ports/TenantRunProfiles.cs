using AgentCore.Domain.Security;
using AgentCore.Application.Security;

namespace AgentCore.Application.Ports;

// Trusted backend metadata, never a model argument, login or ownership grant.
public sealed record TenantProfileSource(Guid TenantId, Guid PrincipalId, Guid AccountId, long Revision, DateTimeOffset UpdatedAt);
public sealed record TenantRunProfileObservation(TenantUserProfile Profile, TenantProfileSource Source, long SecurityRevision, long InstanceRevision);
public interface ITenantRunProfileStore
{
    ValueTask<TenantRunProfileObservation?> ReadTenantRunProfileAsync(TenantRuntimeFrame frame, CancellationToken ct = default);
    ValueTask RequireTenantProfileProjectionAsync(TenantRuntimeFrame frame, TenantRunProfileObservation observation, CancellationToken ct = default);
    ValueTask RequireTenantProfileSourceAsync(TenantRunReference reference, TenantProfileSource source, Guid? generation, CancellationToken ct = default);
}
