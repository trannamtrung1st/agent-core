using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using SpaceEnvironment = AgentCore.Domain.Spaces.Environment;

namespace AgentCore.Application.Security;

public sealed record EnvironmentVariableView(string Name, string Kind, string Description, Guid? SecretId, Guid? CredentialId,
    long Revision, DateTimeOffset UpdatedAt);

public sealed record TenantVariableDetail(EnvironmentVariableView Variable, string? PlainValue, bool CanReadPlain, long SecurityRevision, long SpaceRevision, long EnvironmentRevision);

public sealed class TenantEnvironmentService(IIamStore security, ITenantSpaceStore spaces, ITenantEnvironmentStore environments,
    ITenantSecretStore secrets, ITenantCredentialStore credentials, IamAuthorizer authorizer, IdentityService identity, IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<IReadOnlyList<SpaceEnvironment>> ListAsync(Guid actor, Guid tenant, Guid space, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        var (state, _) = await Read(actor, tenant, space, "environments.read", human, ct).ConfigureAwait(false);
        var result = await environments.ListEnvironmentsAsync(tenant, space, ct).ConfigureAwait(false);
        await CompleteRead(actor, state, human, ct).ConfigureAwait(false); return result;
    }
    public async ValueTask<SpaceEnvironment> ReadAsync(Guid actor, Guid tenant, Guid space, Guid id, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        var (state, _) = await Read(actor, tenant, space, "environments.read", human, ct).ConfigureAwait(false);
        var result = await Owned(tenant, space, id, ct).ConfigureAwait(false);
        await CompleteRead(actor, state, human, ct).ConfigureAwait(false); return result;
    }
    public async ValueTask<SpaceEnvironment> SaveAsync(Guid actor, Guid tenant, Guid space, Guid id, string name, string description,
        long expectedSecurityRevision, long expectedSpaceRevision, long expectedRevision, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        if (expectedSecurityRevision < 1 || expectedSpaceRevision < 1 || expectedRevision < 0) throw new ArgumentException("Use valid expected source revisions.");
        var (state, owner) = await Read(actor, tenant, space, "environments.manage", human, ct).ConfigureAwait(false);
        var old = await environments.FindEnvironmentAsync(tenant, space, id, ct).ConfigureAwait(false);
        if (expectedRevision == 0 && old is not null)
        {
            await Require(state, actor, "environments.read", TenantSpaceService.Scope(tenant, space), ct).ConfigureAwait(false);
            if (old.Revision != 1 || old.Name != name.Trim() || old.Description != description) throw new SecurityConflictException("create_identity_conflict");
            await CompleteRead(actor, state, human, ct).ConfigureAwait(false); return old;
        }
        Revisions(state, owner, expectedSecurityRevision, expectedSpaceRevision);
        if ((old?.Revision ?? 0) != expectedRevision) throw new SecurityConflictException("stale_revision");
        var at = Now(); var value = new SpaceEnvironment(tenant, space, id, name.Trim(), description, checked(expectedRevision + 1), old?.CreatedAt ?? at, at);
        value.Validate();
        if (!await environments.TrySaveEnvironmentAsync(Advance(state), expectedSecurityRevision, value, expectedSpaceRevision, expectedRevision,
            Audit(tenant, actor, space, "environments.manage", at), human, ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        return value;
    }
    public async ValueTask DeleteAsync(Guid actor, Guid tenant, Guid space, Guid id, long expectedSecurityRevision, long expectedSpaceRevision,
        long expectedRevision, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        var (state, owner) = await Read(actor, tenant, space, "environments.manage", human, ct).ConfigureAwait(false);
        Revisions(state, owner, expectedSecurityRevision, expectedSpaceRevision);
        if ((await Owned(tenant, space, id, ct).ConfigureAwait(false)).Revision != expectedRevision) throw new SecurityConflictException("stale_revision");
        if (!await environments.TryDeleteEnvironmentAsync(Advance(state), expectedSecurityRevision, space, id, expectedSpaceRevision, expectedRevision,
            Audit(tenant, actor, space, "environments.delete", Now()), human, ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
    }
    public async ValueTask<IReadOnlyList<EnvironmentVariableView>> VariablesAsync(Guid actor, Guid tenant, Guid space, Guid id,
        HumanSessionReference? human = null, CancellationToken ct = default)
    {
        var (state, _) = await Read(actor, tenant, space, "variables.read", human, ct).ConfigureAwait(false);
        await Owned(tenant, space, id, ct).ConfigureAwait(false);
        var values = await environments.ReadVariablesAsync(tenant, space, id, ct).ConfigureAwait(false);
        await CompleteRead(actor, state, human, ct).ConfigureAwait(false); return values.Select(View).ToArray();
    }
    public async ValueTask<TenantVariableDetail> VariableAsync(Guid actor, Guid tenant, Guid space, Guid id, string name, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        var (state, owner) = await Read(actor, tenant, space, "variables.read", human, ct).ConfigureAwait(false);
        var environment = await Owned(tenant, space, id, ct).ConfigureAwait(false);
        name = EnvironmentVariable.NormalizeName(name);
        var variable = (await environments.ReadVariablesAsync(tenant, space, id, ct).ConfigureAwait(false)).SingleOrDefault(v => v.Name == name)
            ?? throw new AccessDeniedException("variable_unavailable");
        var canReadPlain = variable.Kind == EnvironmentVariableKind.Plain
            && (await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("variables.resolve"), TenantSpaceService.Scope(tenant, space), ct).ConfigureAwait(false)).Allowed;
        await CompleteRead(actor, state, human, ct).ConfigureAwait(false);
        return new(View(variable), canReadPlain ? variable.PlainValue : null, canReadPlain, state.Tenant.Revision, owner.Revision, environment.Revision);
    }
    public async ValueTask<EnvironmentVariableView> SaveVariableAsync(Guid actor, Guid tenant, Guid space, Guid id, string name, EnvironmentVariableKind kind,
        string description, string? plainValue, Guid? secretId, Guid? credentialId, long expectedSecurityRevision, long expectedSpaceRevision,
        long expectedEnvironmentRevision, long expectedRevision, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        if (expectedSecurityRevision < 1 || expectedSpaceRevision < 1 || expectedEnvironmentRevision < 1 || expectedRevision < 0) throw new ArgumentException("Use valid expected source revisions.");
        var (state, owner) = await Read(actor, tenant, space, "variables.manage", human, ct).ConfigureAwait(false);
        var environment = await Owned(tenant, space, id, ct).ConfigureAwait(false);
        name = EnvironmentVariable.NormalizeName(name);
        var old = (await environments.ReadVariablesAsync(tenant, space, id, ct).ConfigureAwait(false)).SingleOrDefault(v => v.Name == name);
        var at = Now(); var value = new EnvironmentVariable(tenant, space, id, name, kind, description, plainValue, secretId, credentialId, checked(expectedRevision + 1), old?.CreatedAt ?? at, at);
        value.Validate();
        if (secretId is { } secret)
        {
            if ((await secrets.FindSecretAsync(tenant, secret, ct).ConfigureAwait(false))?.Status != SecretStatus.Active) throw new AccessDeniedException("resource_unavailable");
            await Require(state, actor, "secrets.read", TenantSecretService.Scope(tenant, secret), ct).ConfigureAwait(false);
        }
        if (credentialId is { } credential)
        {
            if ((await credentials.FindTenantCredentialAsync(tenant, credential, ct).ConfigureAwait(false))?.Value.Status != AgentCore.Domain.Credentials.CredentialStatus.Active) throw new AccessDeniedException("resource_unavailable");
            await Require(state, actor, "credentials.read", TenantCredentialRules.Scope(tenant, credential), ct).ConfigureAwait(false);
        }
        if (expectedRevision == 0 && old is not null)
        {
            await Require(state, actor, "variables.read", TenantSpaceService.Scope(tenant, space), ct).ConfigureAwait(false);
            if (kind == EnvironmentVariableKind.Plain) await Require(state, actor, "variables.resolve", TenantSpaceService.Scope(tenant, space), ct).ConfigureAwait(false);
            if (old.Revision != 1 || old.Kind != kind || old.Description != description || old.PlainValue != plainValue || old.SecretId != secretId || old.CredentialId != credentialId)
                throw new SecurityConflictException("create_identity_conflict");
            await CompleteRead(actor, state, human, ct).ConfigureAwait(false); return View(old);
        }
        Revisions(state, owner, expectedSecurityRevision, expectedSpaceRevision);
        if (environment.Revision != expectedEnvironmentRevision || (old?.Revision ?? 0) != expectedRevision) throw new SecurityConflictException("stale_revision");
        if (!await environments.TrySaveVariableAsync(Advance(state), expectedSecurityRevision, value, expectedSpaceRevision, expectedEnvironmentRevision, expectedRevision,
            Audit(tenant, actor, space, "variables.manage", at), human, ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        return View(value);
    }
    public async ValueTask DeleteVariableAsync(Guid actor, Guid tenant, Guid space, Guid id, string name, long expectedSecurityRevision, long expectedSpaceRevision,
        long expectedEnvironmentRevision, long expectedRevision, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        var (state, owner) = await Read(actor, tenant, space, "variables.manage", human, ct).ConfigureAwait(false);
        Revisions(state, owner, expectedSecurityRevision, expectedSpaceRevision);
        await Owned(tenant, space, id, ct).ConfigureAwait(false); name = EnvironmentVariable.NormalizeName(name);
        if (!await environments.TryDeleteVariableAsync(Advance(state), expectedSecurityRevision, space, id, name, expectedSpaceRevision, expectedEnvironmentRevision, expectedRevision,
            Audit(tenant, actor, space, "variables.delete", Now()), human, ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
    }
    private async ValueTask<(TenantSecurityState State, Space Space)> Read(Guid actor, Guid tenant, Guid space, string permission, HumanSessionReference? human, CancellationToken ct)
    {
        var state = await State(actor, tenant, human, ct).ConfigureAwait(false);
        var owner = await spaces.FindSpaceAsync(tenant, space, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await Require(state, actor, "spaces.read", TenantSpaceService.Scope(tenant, space), ct).ConfigureAwait(false);
        await Require(state, actor, permission, TenantSpaceService.Scope(tenant, space), ct).ConfigureAwait(false); return (state, owner);
    }
    private async ValueTask<TenantSecurityState> State(Guid actor, Guid tenant, HumanSessionReference? human, CancellationToken ct)
    {
        if (human is null) return await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        if (human.PrincipalId != actor) throw new AccessDeniedException("initiator_unavailable");
        return await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
    }
    private async ValueTask CompleteRead(Guid actor, TenantSecurityState state, HumanSessionReference? human, CancellationToken ct)
    { if ((await State(actor, state.Tenant.TenantId, human, ct).ConfigureAwait(false)).Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision"); }
    private async ValueTask Require(TenantSecurityState state, Guid actor, string permission, AccessScope scope, CancellationToken ct)
    { var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false); if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode); }
    private async ValueTask<SpaceEnvironment> Owned(Guid tenant, Guid space, Guid id, CancellationToken ct) => await environments.FindEnvironmentAsync(tenant, space, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
    private static void Revisions(TenantSecurityState state, Space owner, long securityRevision, long spaceRevision)
    { if (state.Tenant.Revision != securityRevision || owner.Revision != spaceRevision) throw new SecurityConflictException("stale_revision"); }
    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
    private static TenantSecurityState Advance(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
    private AuthorizationAudit Audit(Guid tenant, Guid actor, Guid space, string action, DateTimeOffset at) => new(ids.NewId(), tenant, actor, action, space.ToString("D"), "allowed", "environment_changed", at);
    private static EnvironmentVariableView View(EnvironmentVariable variable) => new(variable.Name, variable.Kind.ToString(), variable.Description, variable.SecretId, variable.CredentialId, variable.Revision, variable.UpdatedAt);
}
