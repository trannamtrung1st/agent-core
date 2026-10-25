using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using SpaceEnvironment = AgentCore.Domain.Spaces.Environment;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private readonly Dictionary<(Guid Tenant, Guid Secret), Secret> _secrets = [];
    private bool SpaceRevision(Guid tenant, Guid space, long revision) => _spaces.GetValueOrDefault((tenant, space)) is { DeletedAt: null } value && value.Revision == revision;
    private readonly Dictionary<(Guid Tenant, Guid Space, Guid Environment), SpaceEnvironment> _environments = [];
    private readonly Dictionary<(Guid Tenant, Guid Environment, string Name), EnvironmentVariable> _variables = [];
    public ValueTask<bool> IsSecretCreationActorAsync(Guid tenant, Guid secret, Guid actor, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_audit.Any(a => a.TenantId == tenant && a.Action == "secrets.create" && a.Target == secret.ToString("D") && a.ActorPrincipalId == actor && a.Decision == "allowed")); }
    public ValueTask<Secret?> FindSecretAsync(Guid tenant, Guid secret, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_secrets.TryGetValue((tenant, secret), out var value) && value.Status != SecretStatus.Deleted ? value : null); }
    public ValueTask<SecretPage> ListSecretsAsync(Guid tenant, IReadOnlyList<Guid>? allowedSecrets, int page, int pageSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); SpaceStoreRules.Page(tenant, page, pageSize); lock (_gate)
        {
            var values = _secrets.Values.Where(s => s.TenantId == tenant && s.Status != SecretStatus.Deleted && (allowedSecrets is null || allowedSecrets.Contains(s.SecretId))).OrderBy(s => s.SecretId.ToString("D"), StringComparer.Ordinal).ToArray();
            return ValueTask.FromResult(new SecretPage(values.Skip(page * pageSize).Take(pageSize).ToArray(), values.Length, page, pageSize));
        }
    }
    public ValueTask<bool> TrySaveSecretAsync(TenantSecurityState next, long expectedSecurityRevision, Secret secret, long expectedRevision,
        AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default)
    {
        secret.Validate(); EnvironmentStoreRules.Secret(next, secret, expectedRevision, audit);
        return ValueTask.FromResult(WriteTenantResource(next, expectedSecurityRevision, audit, () =>
        {
            var old = _secrets.GetValueOrDefault((secret.TenantId, secret.SecretId));
            if ((old?.Revision ?? 0) != expectedRevision || old?.Status == SecretStatus.Deleted || old is not null && old.CreatedAt != secret.CreatedAt) return false;
            _secrets[(secret.TenantId, secret.SecretId)] = secret; return true;
        }, ct, initiator));
    }
    public ValueTask<bool> TryDeleteSecretAsync(TenantSecurityState next, long expectedSecurityRevision, Guid secret, long expectedRevision,
        AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default) =>
        ValueTask.FromResult(WriteTenantResource(next, expectedSecurityRevision, audit, () =>
        {
            var tenant = next.Tenant.TenantId;
            var old = _secrets.GetValueOrDefault((tenant, secret));
            if (old is null || old.Status == SecretStatus.Deleted || old.Revision != expectedRevision) return false;
            if (_variables.Values.Any(v => v.TenantId == tenant && v.SecretId == secret) || _spaceMemberships.Values.Any(m => m.TenantId == tenant && m.AddedBindings.Any(b => b.Kind == SpaceBindingKind.Secret && b.ResourceKey == secret.ToString("D"))))
                throw new SecurityConflictException("secret_has_references");
            if (next.Assignments.Any(a => a.Scope == AccessScope.ForResource(tenant, "secret", secret.ToString("D")))) throw new ArgumentException("Retire Secret assignments on deletion.");
            _secrets[(tenant, secret)] = old with { Status = SecretStatus.Deleted, ProtectedPayload = "", Revision = checked(old.Revision + 1), UpdatedAt = audit.At }; return true;
        }, ct, initiator));
    public ValueTask<SpaceEnvironment?> FindEnvironmentAsync(Guid tenant, Guid space, Guid environment, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_environments.TryGetValue((tenant, space, environment), out var value) && value.DeletedAt is null ? value : null); }
    public ValueTask<IReadOnlyList<SpaceEnvironment>> ListEnvironmentsAsync(Guid tenant, Guid space, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult<IReadOnlyList<SpaceEnvironment>>(_environments.Values.Where(e => e.TenantId == tenant && e.SpaceId == space && e.DeletedAt is null).OrderBy(e => e.EnvironmentId).ToArray()); }
    public ValueTask<IReadOnlyList<EnvironmentVariable>> ReadVariablesAsync(Guid tenant, Guid space, Guid environment, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult<IReadOnlyList<EnvironmentVariable>>(_environments.GetValueOrDefault((tenant, space, environment)) is { DeletedAt: null } ? _variables.Values.Where(v => v.TenantId == tenant && v.SpaceId == space && v.EnvironmentId == environment).OrderBy(v => v.Name, StringComparer.Ordinal).ToArray() : []); }
    public ValueTask<bool> TrySaveEnvironmentAsync(TenantSecurityState next, long expectedSecurityRevision, SpaceEnvironment environment, long expectedSpaceRevision,
        long expectedRevision, AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default)
    {
        EnvironmentStoreRules.Environment(next, environment, expectedRevision, audit);
        return ValueTask.FromResult(WriteTenantResource(next, expectedSecurityRevision, audit, () =>
        {
            var e = environment; var key = (e.TenantId, e.SpaceId, e.EnvironmentId); var old = _environments.GetValueOrDefault(key);
            if (!SpaceRevision(e.TenantId, e.SpaceId, expectedSpaceRevision) || (old?.Revision ?? 0) != expectedRevision
                || old?.DeletedAt is not null || old is not null && old.CreatedAt != e.CreatedAt) return false;
            if (_environments.Values.Any(v => v.TenantId == e.TenantId && v.SpaceId == e.SpaceId && v.EnvironmentId != e.EnvironmentId && v.DeletedAt is null && v.NormalizedName == e.NormalizedName)) throw new SecurityConflictException("environment_name_exists");
            if (old is null && _environments.Values.Count(v => v.TenantId == e.TenantId && v.SpaceId == e.SpaceId && v.DeletedAt is null) >= EnvironmentStoreRules.MaxProfiles) throw new SecurityConflictException("environment_limit");
            _environments[key] = e; return true;
        }, ct, initiator));
    }
    public ValueTask<bool> TryDeleteEnvironmentAsync(TenantSecurityState next, long expectedSecurityRevision, Guid space, Guid environment, long expectedSpaceRevision,
        long expectedRevision, AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default) =>
        ValueTask.FromResult(WriteTenantResource(next, expectedSecurityRevision, audit, () =>
        {
            var tenant = next.Tenant.TenantId; var old = _environments.GetValueOrDefault((tenant, space, environment));
            if (!SpaceRevision(tenant, space, expectedSpaceRevision) || old is null || old.DeletedAt is not null || old.Revision != expectedRevision) return false;
            _environments[(tenant, space, environment)] = old with { DeletedAt = audit.At, UpdatedAt = audit.At, Revision = checked(old.Revision + 1) };
            foreach (var key in _variables.Where(v => v.Value.TenantId == tenant && v.Value.EnvironmentId == environment).Select(v => v.Key).ToArray()) _variables.Remove(key);
            return true;
        }, ct, initiator));
    public ValueTask<bool> TrySaveVariableAsync(TenantSecurityState next, long expectedSecurityRevision, EnvironmentVariable variable, long expectedSpaceRevision,
        long expectedEnvironmentRevision, long expectedRevision, AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default)
    {
        EnvironmentStoreRules.Variable(next, variable, expectedRevision, audit);
        return ValueTask.FromResult(WriteTenantResource(next, expectedSecurityRevision, audit, () =>
        {
            var v = variable; var environmentKey = (v.TenantId, v.SpaceId, v.EnvironmentId); var environment = _environments.GetValueOrDefault(environmentKey);
            if (!SpaceRevision(v.TenantId, v.SpaceId, expectedSpaceRevision) || environment is null || environment.DeletedAt is not null || environment.Revision != expectedEnvironmentRevision) return false;
            var key = (v.TenantId, v.EnvironmentId, v.Name); var old = _variables.GetValueOrDefault(key);
            if ((old?.Revision ?? 0) != expectedRevision || old is not null && old.CreatedAt != v.CreatedAt) return false;
            if (v.SecretId is { } secret && _secrets.GetValueOrDefault((v.TenantId, secret))?.Status != SecretStatus.Active
                || v.CredentialId is { } credential && CredentialBacking().ReadTenantCredential(v.TenantId, credential)?.Value.Status != AgentCore.Domain.Credentials.CredentialStatus.Active)
                throw new AccessDeniedException("resource_unavailable");
            if (old is null && _variables.Values.Count(x => x.TenantId == v.TenantId && x.EnvironmentId == v.EnvironmentId) >= EnvironmentStoreRules.MaxVariables) throw new SecurityConflictException("variable_limit");
            _variables[key] = v; _environments[environmentKey] = environment with { Revision = checked(environment.Revision + 1), UpdatedAt = audit.At }; return true;
        }, ct, initiator));
    }
    public ValueTask<bool> TryDeleteVariableAsync(TenantSecurityState next, long expectedSecurityRevision, Guid space, Guid environment, string name,
        long expectedSpaceRevision, long expectedEnvironmentRevision, long expectedRevision, AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default) =>
        ValueTask.FromResult(WriteTenantResource(next, expectedSecurityRevision, audit, () =>
        {
            var tenant = next.Tenant.TenantId; var envKey = (tenant, space, environment); var env = _environments.GetValueOrDefault(envKey);
            if (!SpaceRevision(tenant, space, expectedSpaceRevision) || env is null || env.DeletedAt is not null || env.Revision != expectedEnvironmentRevision) return false;
            if (!_variables.TryGetValue((tenant, environment, name), out var old) || old.Revision != expectedRevision || old.SpaceId != space) return false;
            _variables.Remove((tenant, environment, name)); _environments[envKey] = env with { Revision = checked(env.Revision + 1), UpdatedAt = audit.At }; return true;
        }, ct, initiator));
}
