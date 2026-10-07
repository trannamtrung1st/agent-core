using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryCredentialStore(InMemoryAgentInstanceStore instances) : ICredentialStore, IAgentCredentialBindingStore
{
    private readonly Dictionary<Guid, Credential> _credentials = [];
    private readonly Dictionary<Guid, AgentCredentialBinding> _bindings = [];
    public ValueTask<IReadOnlyList<Credential>> ListAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate) return ValueTask.FromResult<IReadOnlyList<Credential>>(_credentials.Values.OrderBy(c => c.DisplayName).ToArray()); }
    public ValueTask<Credential?> GetAsync(Guid id, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate) return ValueTask.FromResult(_credentials.GetValueOrDefault(id)); }
    public ValueTask SaveAsync(Credential c, long expectedRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate)
        {
            var current = _credentials.GetValueOrDefault(c.CredentialId);
            if ((current?.Revision ?? 0) != expectedRevision || c.Revision != expectedRevision + 1 || current is not null && current.Kind != c.Kind)
                throw AgentCoreErrors.Conflict("Credential revision is stale.");
            _credentials[c.CredentialId] = c; return ValueTask.CompletedTask;
        }
    }
    public ValueTask DeleteAsync(Guid id, long expectedRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate)
        {
            var current = _credentials.GetValueOrDefault(id) ?? throw AgentCoreErrors.NotFound("Credential was not found.");
            if (current.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Credential revision is stale.");
            var count = _bindings.Values.Count(b => b.CredentialId == id);
            if (count != 0) throw AgentCoreErrors.Conflict($"Unbind {count} agent bindings before deleting this credential.");
            _credentials.Remove(id); return ValueTask.CompletedTask;
        }
    }
    public ValueTask<IReadOnlyList<AgentCredentialBinding>> ListBindingsAsync(Guid? instanceId = null, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate) return ValueTask.FromResult<IReadOnlyList<AgentCredentialBinding>>(_bindings.Values.Where(b => instanceId is null || b.AgentInstanceId == instanceId).OrderBy(b => b.Reference).ToArray()); }
    public ValueTask BindAsync(AgentCredentialBinding b, long expectedInstanceRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate)
        {
            RequireActive(b.AgentInstanceId, expectedInstanceRevision);
            if (!_credentials.ContainsKey(b.CredentialId)) throw AgentCoreErrors.NotFound("Credential was not found.");
            if (_bindings.ContainsKey(b.BindingId) || _bindings.Values.Any(x => x.AgentInstanceId == b.AgentInstanceId && (x.Reference == b.Reference || x.CredentialId == b.CredentialId)))
                throw AgentCoreErrors.Conflict("Credential or reference is already bound to this agent.");
            _bindings.Add(b.BindingId, b); return ValueTask.CompletedTask;
        }
    }
    public ValueTask UnbindAsync(Guid instanceId, Guid bindingId, long expectedRevision, long instanceRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate)
        {
            RequireActive(instanceId, instanceRevision);
            if (!_bindings.TryGetValue(bindingId, out var b) || b.AgentInstanceId != instanceId) throw AgentCoreErrors.NotFound("Binding was not found.");
            if (b.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Binding revision is stale.");
            _bindings.Remove(bindingId); return ValueTask.CompletedTask;
        }
    }
    public ValueTask DeleteBindingsAsync(Guid instanceId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (instances.CredentialGate) { foreach (var id in _bindings.Values.Where(b => b.AgentInstanceId == instanceId).Select(b => b.BindingId).ToArray()) _bindings.Remove(id); } return ValueTask.CompletedTask; }
    private void RequireActive(Guid id, long revision)
    {
        var instance = instances.FindAsync(id).Result ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (instance.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Forbidden("Archived agent credentials are read-only.");
        if (instance.Revision != revision) throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
    }
}
