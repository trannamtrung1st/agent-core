using AgentCore.Application.Ports;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

public sealed record SecretView(Guid TenantId, Guid SecretId, string Name, string Description, string Status,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record SecretViewPage(IReadOnlyList<SecretView> Items, int Total, int Page, int PageSize);

/// <summary>Tenant resource management. No public value-read operation exists.</summary>
public sealed record TenantSecretAccess(IReadOnlyList<string> Permissions, long SecurityRevision);

public sealed class TenantSecretService(IIamStore security, ITenantSecretStore secrets, IamAuthorizer authorizer,
    ISecretProtector protector, IIdGenerator ids, TimeProvider time, IdentityService identity)
{
    public async ValueTask<TenantSecretAccess> AccessAsync(Guid actor, Guid tenant, Guid id, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct).ConfigureAwait(false);
        await Owned(tenant, id, ct).ConfigureAwait(false);
        await Require(state, actor, "secrets.read", Scope(tenant, id), ct).ConfigureAwait(false);
        var keys = new List<string>();
        foreach (var key in new[] { "secrets.read", "secrets.manage", "secrets.use", "iam.roles.read", "iam.roles.assign" })
            if ((await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(key), Scope(tenant, id), ct).ConfigureAwait(false)).Allowed) keys.Add(key);
        await CompleteRead(actor, state, human, ct).ConfigureAwait(false);
        return new(keys, state.Tenant.Revision);
    }
    public async ValueTask<SecretViewPage> ListAsync(Guid actor, Guid tenant, int page = 0, int size = 30, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct).ConfigureAwait(false);
        var all = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("secrets.read"), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
        List<Guid>? allowed = null;
        if (!all.Allowed)
        {
            if (all.ReasonCode != "permission_denied") throw new AccessDeniedException(all.ReasonCode);
            allowed = [];
            foreach (var scope in state.Assignments.Where(a => a.PrincipalId == actor && TryId(a.Scope, out _)).Select(a => a.Scope).Distinct())
                if ((await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("secrets.read"), scope, ct).ConfigureAwait(false)).Allowed) allowed.Add(Guid.Parse(scope.ResourceId!));
        }
        var result = await secrets.ListSecretsAsync(tenant, allowed, Math.Clamp(page, 0, 100_000), Math.Clamp(size, 1, 100), ct).ConfigureAwait(false);
        await CompleteRead(actor, state, human, ct).ConfigureAwait(false);
        return new(result.Items.Select(View).ToArray(), result.Total, result.Page, result.PageSize);
    }
    public async ValueTask<SecretView> ReadAsync(Guid actor, Guid tenant, Guid id, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct).ConfigureAwait(false);
        await Require(state, actor, "secrets.read", Scope(tenant, id), ct).ConfigureAwait(false);
        var secret = await Owned(tenant, id, ct).ConfigureAwait(false); await CompleteRead(actor, state, human, ct).ConfigureAwait(false); return View(secret);
    }
    public async ValueTask<SecretView> CreateAsync(Guid actor, Guid tenant, string name, string description, string value, long expected,
        HumanSessionReference? human = null, CancellationToken ct = default, Guid? secretId = null)
    {
        if (expected < 1) throw new ArgumentException("Use a valid expected security revision.");
        var state = await State(actor, tenant, human, ct).ConfigureAwait(false);
        await Require(state, actor, "secrets.manage", AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
        CredentialRules.ProtectedValue(value); var id = secretId ?? ids.NewId();
        if (id == Guid.Empty) throw new ArgumentException("A stable Secret creation identity is required.");
        var existing = await secrets.FindSecretAsync(tenant, id, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            // Replay never becomes a value oracle for a manager lacking current metadata access.
            await Require(state, actor, "secrets.read", Scope(tenant, id), ct).ConfigureAwait(false);
            if (!await secrets.IsSecretCreationActorAsync(tenant, id, actor, ct).ConfigureAwait(false) || existing.Revision != 1 || existing.Status != SecretStatus.Active || existing.Name != CredentialRules.DisplayName(name) || existing.Description != description
                || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)),
                    System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(protector.Unprotect(tenant, id, existing.ProtectionVersion, existing.ProtectedPayload)))))
                throw new SecurityConflictException("create_identity_conflict");
            await CompleteRead(actor, state, human, ct).ConfigureAwait(false); return View(existing);
        }
        if (state.Tenant.Revision != expected) throw new SecurityConflictException("stale_revision");
        var at = Now();
        var secret = new Secret(tenant, id, CredentialRules.DisplayName(name), description, SecretStatus.Active, protector.Protect(tenant, id, 1, value), 1, 1, at, at);
        secret.Validate();
        if (!await secrets.TrySaveSecretAsync(Advance(state), expected, secret, 0, Audit(tenant, actor, id, "create", at), human, ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        return View(secret);
    }
    public async ValueTask<SecretView> UpdateAsync(Guid actor, Guid tenant, Guid id, string name, string description, SecretStatus status,
        long expected, long expectedSecret, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        if (status is not (SecretStatus.Active or SecretStatus.Disabled)) throw new ArgumentException("Unknown Secret status.");
        var state = await Change(actor, tenant, Scope(tenant, id), expected, human, ct).ConfigureAwait(false);
        var old = await Owned(tenant, id, ct).ConfigureAwait(false); if (old.Revision != expectedSecret) throw new SecurityConflictException("stale_revision");
        var secret = old with { Name = CredentialRules.DisplayName(name), Description = description, Status = status, Revision = checked(old.Revision + 1), UpdatedAt = Now() };
        secret.Validate();
        if (!await secrets.TrySaveSecretAsync(Advance(state), expected, secret, expectedSecret, Audit(tenant, actor, id, "update", secret.UpdatedAt), human, ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        return View(secret);
    }
    public async ValueTask<SecretView> ReplaceAsync(Guid actor, Guid tenant, Guid id, string value, long expected, long expectedSecret,
        HumanSessionReference? human = null, CancellationToken ct = default)
    {
        var state = await Change(actor, tenant, Scope(tenant, id), expected, human, ct).ConfigureAwait(false);
        var old = await Owned(tenant, id, ct).ConfigureAwait(false); if (old.Revision != expectedSecret) throw new SecurityConflictException("stale_revision");
        CredentialRules.ProtectedValue(value);
        var secret = old with { ProtectedPayload = protector.Protect(tenant, id, old.ProtectionVersion, value), Revision = checked(old.Revision + 1), UpdatedAt = Now() };
        if (!await secrets.TrySaveSecretAsync(Advance(state), expected, secret, expectedSecret, Audit(tenant, actor, id, "replace", secret.UpdatedAt), human, ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        return View(secret);
    }
    public async ValueTask DeleteAsync(Guid actor, Guid tenant, Guid id, long expected, long expectedSecret, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        var state = await Change(actor, tenant, Scope(tenant, id), expected, human, ct).ConfigureAwait(false);
        var old = await Owned(tenant, id, ct).ConfigureAwait(false); if (old.Revision != expectedSecret) throw new SecurityConflictException("stale_revision");
        state = state with { Assignments = state.Assignments.Where(a => a.Scope != Scope(tenant, id)).ToArray() };
        if (!await secrets.TryDeleteSecretAsync(Advance(state), expected, id, expectedSecret, Audit(tenant, actor, id, "delete", Now()), human, ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
    }
    private async ValueTask<TenantSecurityState> State(Guid actor, Guid tenant, HumanSessionReference? human, CancellationToken ct)
    {
        if (human is null) return await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        if (human.PrincipalId != actor) throw new AccessDeniedException("initiator_unavailable");
        return await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
    }
    private async ValueTask CompleteRead(Guid actor, TenantSecurityState state, HumanSessionReference? human, CancellationToken ct)
    { if ((await State(actor, state.Tenant.TenantId, human, ct).ConfigureAwait(false)).Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision"); }
    private async ValueTask<TenantSecurityState> Change(Guid actor, Guid tenant, AccessScope scope, long expected, HumanSessionReference? human, CancellationToken ct)
    {
        var state = await State(actor, tenant, human, ct).ConfigureAwait(false);
        await Require(state, actor, "secrets.manage", scope, ct).ConfigureAwait(false);
        if (scope.Kind == AccessScopeKind.Resource) await Require(state, actor, "secrets.read", scope, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != expected) throw new SecurityConflictException("stale_revision"); return state;
    }
    private async ValueTask Require(TenantSecurityState state, Guid actor, string permission, AccessScope scope, CancellationToken ct)
    { var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false); if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode); }
    private async ValueTask<Secret> Owned(Guid tenant, Guid id, CancellationToken ct) => await secrets.FindSecretAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
    private static TenantSecurityState Advance(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
    private AuthorizationAudit Audit(Guid tenant, Guid actor, Guid id, string action, DateTimeOffset at) => new(ids.NewId(), tenant, actor, "secrets." + action, id.ToString("D"), "allowed", "secret_changed", at);
    private static SecretView View(Secret secret) => new(secret.TenantId, secret.SecretId, secret.Name, secret.Description, secret.Status.ToString(), secret.Revision, secret.CreatedAt, secret.UpdatedAt);
    public static AccessScope Scope(Guid tenant, Guid id) => AccessScope.ForResource(tenant, "secret", id.ToString("D"));
    public static bool TryId(AccessScope scope, out Guid id)
    { id = default; return scope is { Kind: AccessScopeKind.Resource, ResourceType: "secret", TenantId: not null } && Guid.TryParseExact(scope.ResourceId, "D", out id) && id != Guid.Empty && scope.ResourceId == id.ToString("D"); }
}
