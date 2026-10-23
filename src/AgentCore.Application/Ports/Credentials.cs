using AgentCore.Domain.Credentials;
using AgentCore.Application.Admin;

namespace AgentCore.Application.Ports;

public interface ICredentialStore
{
    ValueTask<IReadOnlyList<Credential>> ListAsync(CancellationToken ct = default);
    ValueTask<Credential?> GetAsync(Guid id, CancellationToken ct = default);
    ValueTask SaveAsync(Credential credential, long expectedRevision, CancellationToken ct = default, long? expectedSecretRevision = null);
    // Rotation is independent of the logical account revision; never restores a stale grant.
    ValueTask<bool> TryRotateOAuthSecretAsync(Guid id, long expectedRevision, long expectedSecretRevision,
        string protectedPayload, CancellationToken ct = default);
    ValueTask DeleteAsync(Guid id, long expectedRevision, CancellationToken ct = default);
}
public interface IAgentCredentialBindingStore
{
    ValueTask<IReadOnlyList<AgentCredentialBinding>> ListBindingsAsync(Guid? instanceId = null, CancellationToken ct = default);
    ValueTask BindAsync(AgentCredentialBinding binding, long expectedInstanceRevision, CancellationToken ct = default, AdminEventAppend? history = null);
    ValueTask UnbindAsync(Guid instanceId, Guid bindingId, long expectedRevision, long expectedInstanceRevision, CancellationToken ct = default, AdminEventAppend? history = null);
    ValueTask DeleteBindingsAsync(Guid instanceId, CancellationToken ct = default);
}
public interface ICredentialProtector
{
    string Protect(Guid credentialId, int version, string value);
    string Unprotect(Guid credentialId, int version, string payload);
}
