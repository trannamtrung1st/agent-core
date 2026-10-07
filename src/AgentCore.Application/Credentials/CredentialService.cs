using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Credentials;

public sealed record CredentialView(Guid CredentialId, string DisplayName, string Kind, string Status,
    IReadOnlyDictionary<string, string> Metadata, IReadOnlyList<string> AllowedOrigins, long Revision,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, int BindingCount);
public sealed record CredentialBindingView(Guid BindingId, Guid CredentialId, string Reference, long Revision, CredentialView Credential);
public sealed record BoundCredentialMetadata(string Reference, string DisplayName, string Kind, IReadOnlyDictionary<string, string> Metadata);

public sealed class CredentialService(ICredentialStore store, IAgentCredentialBindingStore bindings,
    ICredentialProtector protector, IAgentInstanceStore instances, IIdGenerator ids, TimeProvider time) : ICredentialResolver
{
    public async ValueTask<IReadOnlyList<CredentialView>> ListAsync(CancellationToken ct = default)
    {
        var grants = await bindings.ListBindingsAsync(ct: ct);
        return (await store.ListAsync(ct)).Select(c => View(c, grants.Count(b => b.CredentialId == c.CredentialId))).ToArray();
    }
    public async ValueTask<CredentialView> GetAsync(Guid id, CancellationToken ct = default) =>
        View(await RequireCredential(id, ct), (await bindings.ListBindingsAsync(ct: ct)).Count(b => b.CredentialId == id));
    public async ValueTask<CredentialView> CreateAsync(string name, string kind, IReadOnlyDictionary<string, string>? metadata,
        IReadOnlyList<string>? origins, string value, CancellationToken ct = default)
    {
        var normalized = Validate(() => (CredentialRules.DisplayName(name), Parse<CredentialKind>(kind), CredentialRules.Metadata(metadata), CredentialRules.Origins(origins)));
        Validate(() => { CredentialRules.ProtectedValue(value); return true; });
        var id = ids.NewId(); var now = time.GetUtcNow();
        var credential = new Credential(id, normalized.Item1, normalized.Item2, CredentialStatus.Active, normalized.Item3,
            normalized.Item4, protector.Protect(id, 1, value), 1, 1, now, now);
        await store.SaveAsync(credential, 0, ct);
        return View(credential, 0);
    }
    public async ValueTask<CredentialView> UpdateAsync(Guid id, long revision, string name, string status,
        IReadOnlyDictionary<string, string>? metadata, IReadOnlyList<string>? origins, CancellationToken ct = default)
    {
        var current = await RequireCredential(id, ct);
        var updated = Validate(() => current with { DisplayName = CredentialRules.DisplayName(name), Status = Parse<CredentialStatus>(status),
            Metadata = CredentialRules.Metadata(metadata), AllowedOrigins = CredentialRules.Origins(origins), Revision = revision + 1, UpdatedAtUtc = time.GetUtcNow() });
        await store.SaveAsync(updated, revision, ct); return await GetAsync(id, ct);
    }
    public async ValueTask<CredentialView> ReplaceAsync(Guid id, long revision, string value, CancellationToken ct = default)
    {
        Validate(() => { CredentialRules.ProtectedValue(value); return true; });
        var current = await RequireCredential(id, ct);
        await store.SaveAsync(current with { ProtectedPayload = protector.Protect(id, current.ProtectionVersion, value),
            Revision = revision + 1, UpdatedAtUtc = time.GetUtcNow() }, revision, ct);
        return await GetAsync(id, ct);
    }
    public ValueTask DeleteAsync(Guid id, long revision, CancellationToken ct = default) => store.DeleteAsync(id, revision, ct);
    public async ValueTask<IReadOnlyList<CredentialBindingView>> BindingsAsync(Guid instanceId, CancellationToken ct = default)
    {
        await RequireInstance(instanceId, false, ct);
        var result = new List<CredentialBindingView>();
        foreach (var binding in await bindings.ListBindingsAsync(instanceId, ct))
            result.Add(new(binding.BindingId, binding.CredentialId, binding.Reference, binding.Revision, await GetAsync(binding.CredentialId, ct)));
        return result;
    }
    public async ValueTask<CredentialBindingView> BindAsync(Guid instanceId, Guid credentialId, string reference, long expectedInstanceRevision, CancellationToken ct = default)
    {
        await RequireInstance(instanceId, true, ct); await RequireCredential(credentialId, ct);
        var alias = Validate(() => CredentialRules.Reference(reference)); var now = time.GetUtcNow();
        var binding = new AgentCredentialBinding(ids.NewId(), instanceId, credentialId, alias, 1, now, now);
        await bindings.BindAsync(binding, expectedInstanceRevision, ct);
        return new(binding.BindingId, credentialId, alias, 1, await GetAsync(credentialId, ct));
    }
    public async ValueTask UnbindAsync(Guid instanceId, Guid bindingId, long revision, long instanceRevision, CancellationToken ct = default)
    {
        await RequireInstance(instanceId, true, ct);
        await bindings.UnbindAsync(instanceId, bindingId, revision, instanceRevision, ct);
    }
    public async ValueTask<IReadOnlyList<BoundCredentialMetadata>> SafeMetadataAsync(Guid instanceId, CancellationToken ct = default)
    {
        if (await instances.FindAsync(instanceId, ct) is not { Lifecycle: AgentInstanceLifecycle.Active }) return [];
        var result = new List<BoundCredentialMetadata>();
        foreach (var binding in await bindings.ListBindingsAsync(instanceId, ct))
            if (await store.GetAsync(binding.CredentialId, ct) is { Status: CredentialStatus.Active } c)
                result.Add(new(binding.Reference, c.DisplayName, c.Kind.ToString(), c.Metadata));
        return result;
    }
    public async ValueTask<string> ResolvePasswordAsync(Guid instanceId, string reference, string origin, CancellationToken ct = default)
    {
        await RequireInstance(instanceId, true, ct);
        var alias = Validate(() => CredentialRules.Reference(reference));
        var binding = (await bindings.ListBindingsAsync(instanceId, ct)).SingleOrDefault(b => b.Reference == alias)
            ?? throw AgentCoreErrors.Forbidden("Credential binding is unavailable.");
        var credential = await RequireCredential(binding.CredentialId, ct);
        if (credential.Status != CredentialStatus.Active || credential.Kind != CredentialKind.Password
            || !credential.AllowedOrigins.Contains(Validate(() => CredentialRules.Origin(origin)), StringComparer.Ordinal))
            throw AgentCoreErrors.Forbidden("Credential cannot be used for this password sink and origin.");
        return protector.Unprotect(credential.CredentialId, credential.ProtectionVersion, credential.ProtectedPayload);
    }
    private async ValueTask<AgentInstance> RequireInstance(Guid id, bool active, CancellationToken ct)
    {
        var instance = await instances.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (active && instance.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Forbidden("Archived agent credentials are read-only.");
        return instance;
    }
    private async ValueTask<Credential> RequireCredential(Guid id, CancellationToken ct) =>
        await store.GetAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Credential was not found.");
    private static CredentialView View(Credential c, int count) => new(c.CredentialId, c.DisplayName, c.Kind.ToString(), c.Status.ToString(),
        c.Metadata, c.AllowedOrigins, c.Revision, c.CreatedAtUtc, c.UpdatedAtUtc, count);
    private static T Parse<T>(string value) where T : struct, Enum =>
        Enum.TryParse<T>(value, false, out var kind) && Enum.IsDefined(kind) && Enum.GetNames<T>().Contains(value)
            ? kind : throw new ArgumentException("Unknown credential kind or status.");
    private static T Validate<T>(Func<T> action) { try { return action(); } catch (ArgumentException ex) { throw AgentCoreErrors.Validation(ex.Message); } }
}
