using AgentCore.Domain.Credentials;

namespace AgentCore.Application.Ports;

public interface ICredentialStore
{
    ValueTask<IReadOnlyList<Credential>> ListAsync(CancellationToken ct = default);
    ValueTask<Credential?> GetAsync(Guid id, CancellationToken ct = default);
    ValueTask SaveAsync(Credential credential, long expectedRevision, CancellationToken ct = default);
    ValueTask DeleteAsync(Guid id, long expectedRevision, CancellationToken ct = default);
}
public interface IAgentCredentialBindingStore
{
    ValueTask<IReadOnlyList<AgentCredentialBinding>> ListBindingsAsync(Guid? instanceId = null, CancellationToken ct = default);
    ValueTask BindAsync(AgentCredentialBinding binding, long expectedInstanceRevision, CancellationToken ct = default);
    ValueTask UnbindAsync(Guid instanceId, Guid bindingId, long expectedRevision, long expectedInstanceRevision, CancellationToken ct = default);
    ValueTask DeleteBindingsAsync(Guid instanceId, CancellationToken ct = default);
}
public interface ICredentialProtector
{
    string Protect(Guid credentialId, int version, string value);
    string Unprotect(Guid credentialId, int version, string payload);
}
public interface ICredentialResolver
{
    ValueTask<string> ResolvePasswordAsync(Guid instanceId, string reference, string origin, CancellationToken ct = default);
}
