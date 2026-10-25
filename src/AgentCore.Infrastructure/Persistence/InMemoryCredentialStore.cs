using AgentCore.Application.Ports;
using AgentCore.Application.Admin;
using AgentCore.Application.Sessions;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemoryCredentialStore(InMemoryAgentInstanceStore instances) : ICredentialStore, IAgentCredentialBindingStore
{
    private readonly Dictionary<Guid, Credential> _credentials = [];
    private readonly Dictionary<Guid, AgentCredentialBinding> _bindings = [];
    public ValueTask<IReadOnlyList<Credential>> ListAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate) return ValueTask.FromResult<IReadOnlyList<Credential>>(_credentials.Values.Where(c => !_tenantOwners.ContainsKey(c.CredentialId)).OrderBy(c => c.DisplayName).ToArray()); }
    public ValueTask<Credential?> GetAsync(Guid id, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate) return ValueTask.FromResult(_tenantOwners.ContainsKey(id) ? null : _credentials.GetValueOrDefault(id)); }
    public ValueTask SaveAsync(Credential c, long expectedRevision, CancellationToken ct = default, long? expectedSecretRevision = null)
    {
        ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate)
        {
            RequireLegacyCredential(c.CredentialId);
            var current = _credentials.GetValueOrDefault(c.CredentialId);
            if (expectedSecretRevision is not null && current?.SecretRevision != expectedSecretRevision || (current?.Revision ?? 0) != expectedRevision || c.Revision != expectedRevision + 1 || current is not null && (current.Kind != c.Kind || current.Purpose != c.Purpose))
                throw AgentCoreErrors.Conflict("Credential revision is stale.");
            if ((c.Purpose == CredentialPurpose.EmailAccount) != (c.EmailAccount is not null) || c.Purpose == CredentialPurpose.EmailAccount && (c.Kind != (c.EmailAccount?.AuthMethod == EmailAuthMethod.GoogleOAuth2 ? CredentialKind.Token : CredentialKind.Password) || c.AllowedOrigins.Count != 0)) throw AgentCoreErrors.Validation("Invalid credential purpose/profile.");
            c.EmailAccount?.Validate();
        if (!Enum.IsDefined(c.Purpose) || (c.Purpose == CredentialPurpose.GoogleDriveAccount) != (c.DriveAccount is not null)
            || c.Purpose == CredentialPurpose.GoogleDriveAccount && (c.Kind != CredentialKind.Token || c.AllowedOrigins.Count != 0)) throw AgentCoreErrors.Validation("Invalid Drive credential purpose/profile.");
        c.DriveAccount?.Validate();
        if ((c.Purpose == CredentialPurpose.ContainerRegistry) != (c.ContainerRegistry is not null)
            || c.Purpose == CredentialPurpose.ContainerRegistry && (c.Kind is not (CredentialKind.Password or CredentialKind.Token) || c.AllowedOrigins.Count != 0))
            throw AgentCoreErrors.Validation("Invalid Container Registry purpose/profile.");
        if (c.ContainerRegistry is not null) c = c with { ContainerRegistry=c.ContainerRegistry.Validate() };
            if (c.EmailAccount?.GoogleSubject is { } subject && _credentials.Values.Any(x => !_tenantOwners.ContainsKey(x.CredentialId) && x.CredentialId != c.CredentialId && x.EmailAccount?.GoogleSubject == subject))
                throw AgentCoreErrors.Conflict("email_identity_already_connected: This Google identity belongs to another account.");
            if (c.DriveAccount?.GoogleSubject is { } driveSubject && _credentials.Values.Any(x => !_tenantOwners.ContainsKey(x.CredentialId) && x.CredentialId != c.CredentialId && x.DriveAccount?.GoogleSubject == driveSubject))
                throw AgentCoreErrors.Conflict("drive_identity_already_connected: This Google identity belongs to another Drive account.");
            _credentials[c.CredentialId] = c; return ValueTask.CompletedTask;
        }
    }
    public ValueTask<bool> TryRotateOAuthSecretAsync(Guid id, long expectedRevision, long expectedSecretRevision, string payload, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate)
        {
            if (_tenantOwners.ContainsKey(id) || !_credentials.TryGetValue(id, out var c) || c.Revision != expectedRevision || c.SecretRevision != expectedSecretRevision
                || c.Status != CredentialStatus.Active || (c.EmailAccount is not { AuthMethod: EmailAuthMethod.GoogleOAuth2, ConnectionStatus: EmailConnectionStatus.Connected } && c.DriveAccount is not { ConnectionStatus: DriveConnectionStatus.Connected }))
                return ValueTask.FromResult(false);
            _credentials[id] = c with { ProtectedPayload = payload, SecretRevision = c.SecretRevision + 1 };
            return ValueTask.FromResult(true);
        }
    }
    public ValueTask DeleteAsync(Guid id, long expectedRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate)
        {
            RequireLegacyCredential(id);
            var current = _credentials.GetValueOrDefault(id) ?? throw AgentCoreErrors.NotFound("Credential was not found.");
            if (current.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Credential revision is stale.");
            var count = _bindings.Values.Count(b => b.CredentialId == id);
            if (count != 0) throw AgentCoreErrors.Conflict($"Unbind {count} agent bindings before deleting this credential.");
            _credentials.Remove(id); return ValueTask.CompletedTask;
        }
    }
    public ValueTask<IReadOnlyList<AgentCredentialBinding>> ListBindingsAsync(Guid? instanceId = null, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate) return ValueTask.FromResult<IReadOnlyList<AgentCredentialBinding>>(_bindings.Values
        .Where(b => !_tenantBindingOwners.ContainsKey(b.BindingId) && !_tenantOwners.ContainsKey(b.CredentialId)
            && !instances.IsTenantOwned(b.AgentInstanceId) && (instanceId is null || b.AgentInstanceId == instanceId)).OrderBy(b => b.Reference).ToArray()); }
    public ValueTask BindAsync(AgentCredentialBinding b, long expectedInstanceRevision, CancellationToken ct = default, AdminEventAppend? history = null)
    {
        ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate)
        {
            RequireActive(b.AgentInstanceId, expectedInstanceRevision);
            RequireLegacyCredential(b.CredentialId);
            if (!_credentials.TryGetValue(b.CredentialId,out var credential)) throw AgentCoreErrors.NotFound("Credential was not found.");
            if (credential.Purpose is CredentialPurpose.ContainerRegistry or CredentialPurpose.ModelInference) throw AgentCoreErrors.Forbidden("Infrastructure credentials cannot be bound to agents.");
            if (_bindings.ContainsKey(b.BindingId) || _bindings.Values.Any(x => x.AgentInstanceId == b.AgentInstanceId && (x.Reference == b.Reference || x.CredentialId == b.CredentialId)))
                throw AgentCoreErrors.Conflict("Credential or reference is already bound to this agent.");
            instances.UpdateWithExpectedRevisionAsync(new(b.AgentInstanceId, expectedInstanceRevision, History: instances.EventStore is null ? null : history), b.UpdatedAtUtc, ct).GetAwaiter().GetResult();
            _bindings.Add(b.BindingId, b); return ValueTask.CompletedTask;
        }
    }
    public ValueTask UnbindAsync(Guid instanceId, Guid bindingId, long expectedRevision, long instanceRevision, CancellationToken ct = default, AdminEventAppend? history = null)
    {
        ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate)
        {
            RequireActive(instanceId, instanceRevision);
            if (!_bindings.TryGetValue(bindingId, out var b) || b.AgentInstanceId != instanceId) throw AgentCoreErrors.NotFound("Binding was not found.");
            RequireLegacyCredential(b.CredentialId);
            if (b.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Binding revision is stale.");
            instances.UpdateWithExpectedRevisionAsync(new(instanceId, instanceRevision, History: instances.EventStore is null ? null : history), history?.OccurredAt ?? DateTimeOffset.UtcNow, ct).GetAwaiter().GetResult();
            _bindings.Remove(bindingId); return ValueTask.CompletedTask;
        }
    }
    public ValueTask DeleteBindingsAsync(Guid instanceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate)
        {
            RequireLegacyContext(instanceId);
            if (_bindings.Values.Any(b => b.AgentInstanceId == instanceId && (_tenantBindingOwners.ContainsKey(b.BindingId) || _tenantOwners.ContainsKey(b.CredentialId))))
                throw new AccessDeniedException("tenant_context_required");
            foreach (var id in _bindings.Values.Where(b => b.AgentInstanceId == instanceId).Select(b => b.BindingId).ToArray()) _bindings.Remove(id);
        }
        return ValueTask.CompletedTask;
    }
    private void RequireActive(Guid id, long revision)
    {
        RequireLegacyContext(id);
        var instance = instances.FindAsync(id).Result ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (instance.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Forbidden("Archived agent credentials are read-only.");
        if (instance.Revision != revision) throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
    }
    private void RequireLegacyContext(Guid id)
    {
        if (instances.IsTenantOwned(id)) throw new AccessDeniedException("tenant_context_required");
    }
    private void RequireLegacyCredential(Guid id)
    {
        if (_tenantOwners.ContainsKey(id)) throw new AccessDeniedException("tenant_context_required");
    }
}
