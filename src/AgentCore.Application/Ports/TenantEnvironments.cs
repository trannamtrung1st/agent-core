using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using SpaceEnvironment = AgentCore.Domain.Spaces.Environment;

namespace AgentCore.Application.Ports;

public sealed record SecretPage(IReadOnlyList<Secret> Items, int Total, int Page, int PageSize);
public interface ISecretProtector
{
    string Protect(Guid tenant, Guid secret, int version, string value);
    string Unprotect(Guid tenant, Guid secret, int version, string payload);
}
public interface ITenantSecretStore
{
    ValueTask<bool> IsSecretCreationActorAsync(Guid tenant, Guid secret, Guid actor, CancellationToken ct = default) => ValueTask.FromResult(false);
    ValueTask<Secret?> FindSecretAsync(Guid tenant, Guid secret, CancellationToken ct = default);
    ValueTask<SecretPage> ListSecretsAsync(Guid tenant, IReadOnlyList<Guid>? allowedSecrets, int page, int pageSize, CancellationToken ct = default);
    ValueTask<bool> TrySaveSecretAsync(TenantSecurityState next, long expectedSecurityRevision, Secret secret, long expectedRevision,
        AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default);
    ValueTask<bool> TryDeleteSecretAsync(TenantSecurityState next, long expectedSecurityRevision, Guid secret, long expectedRevision,
        AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default);
}
public interface ITenantEnvironmentStore
{
    ValueTask<SpaceEnvironment?> FindEnvironmentAsync(Guid tenant, Guid space, Guid environment, CancellationToken ct = default);
    ValueTask<IReadOnlyList<SpaceEnvironment>> ListEnvironmentsAsync(Guid tenant, Guid space, CancellationToken ct = default);
    ValueTask<IReadOnlyList<EnvironmentVariable>> ReadVariablesAsync(Guid tenant, Guid space, Guid environment, CancellationToken ct = default);
    ValueTask<bool> TrySaveEnvironmentAsync(TenantSecurityState next, long expectedSecurityRevision, SpaceEnvironment environment,
        long expectedSpaceRevision, long expectedRevision, AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default);
    ValueTask<bool> TryDeleteEnvironmentAsync(TenantSecurityState next, long expectedSecurityRevision, Guid space, Guid environment,
        long expectedSpaceRevision, long expectedRevision, AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default);
    ValueTask<bool> TrySaveVariableAsync(TenantSecurityState next, long expectedSecurityRevision, EnvironmentVariable variable,
        long expectedSpaceRevision, long expectedEnvironmentRevision, long expectedRevision, AuthorizationAudit audit,
        HumanSessionReference? initiator = null, CancellationToken ct = default);
    ValueTask<bool> TryDeleteVariableAsync(TenantSecurityState next, long expectedSecurityRevision, Guid space, Guid environment, string name,
        long expectedSpaceRevision, long expectedEnvironmentRevision, long expectedRevision, AuthorizationAudit audit,
        HumanSessionReference? initiator = null, CancellationToken ct = default);
}
