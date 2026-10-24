using AgentCore.Application.Credentials;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantCredentialCreate(string DisplayName, CredentialKind Kind, CredentialPurpose Purpose,
    IReadOnlyDictionary<string, string>? Metadata, IReadOnlyList<string>? AllowedOrigins, string? ProtectedValue,
    EmailAccountProfile? EmailAccount = null, ContainerRegistryProfile? ContainerRegistry = null);
public sealed record TenantCredentialUpdate(string DisplayName, CredentialStatus Status,
    IReadOnlyDictionary<string, string>? Metadata, IReadOnlyList<string>? AllowedOrigins,
    EmailAccountProfile? EmailAccount = null, ContainerRegistryProfile? ContainerRegistry = null);
public sealed record TenantCredentialView(Guid TenantId, CredentialView Credential);
public sealed record TenantCredentialViewPage(IReadOnlyList<TenantCredentialView> Items, int Total, int Page, int PageSize);
public sealed record TenantCredentialAccess(IReadOnlyList<string> Permissions, long Revision);

public sealed class TenantCredentialService(IIamStore security, ITenantCredentialStore credentials,
    IamAuthorizer authorizer, ICredentialProtector protector, IIdGenerator ids, TimeProvider time, IdentityService identity)
{
    public async ValueTask<TenantCredentialAccess> AccessAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var state = await State(actor, tenant, ct, initiator).ConfigureAwait(false);
        await Owned(tenant, id, ct).ConfigureAwait(false); var scope = TenantCredentialRules.Scope(tenant, id);
        var read = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("credentials.read"), scope, ct).ConfigureAwait(false);
        if (!read.Allowed) throw new AccessDeniedException(read.ReasonCode);
        var permissions = new List<string> { "credentials.read" };
        foreach (var key in new[] { "credentials.manage", "credentials.bind", "credentials.use", "iam.roles.read", "iam.roles.manage" })
            if ((await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed) permissions.Add(key);
        await CompleteRead(actor, tenant, state.Tenant.Revision, initiator, ct).ConfigureAwait(false);
        return new(permissions.AsReadOnly(), state.Tenant.Revision);
    }
    public async ValueTask<TenantCredentialViewPage> ListAsync(Guid actor, Guid tenant, int page = 0, int pageSize = 30, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var state = await State(actor, tenant, ct, initiator).ConfigureAwait(false);
        var permission = PermissionCatalog.Get("credentials.read");
        var all = await authorizer.CheckTenantAsync(state, actor, permission, AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
        List<Guid>? allowed = null;
        if (!all.Allowed)
        {
            if (all.ReasonCode != "permission_denied") throw new AccessDeniedException(all.ReasonCode);
            allowed = [];
            foreach (var scope in state.Assignments.Where(a => a.PrincipalId == actor && TenantCredentialRules.TryId(a.Scope, out _)).Select(a => a.Scope).Distinct())
                if ((await authorizer.CheckTenantAsync(state, actor, permission, scope, ct).ConfigureAwait(false)).Allowed)
                    allowed.Add(Guid.Parse(scope.ResourceId!));
        }
        var result = await credentials.ListTenantCredentialsAsync(tenant, allowed, Math.Clamp(page, 0, 100_000), Math.Clamp(pageSize, 1, 100), ct).ConfigureAwait(false);
        await CompleteRead(actor, tenant, state.Tenant.Revision, initiator, ct).ConfigureAwait(false);
        return new(result.Items.Select(View).ToArray(), result.Total, result.Page, result.PageSize);
    }
    public async ValueTask<TenantCredentialView> ReadAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var state = await State(actor, tenant, ct, initiator).ConfigureAwait(false);
        var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("credentials.read"), TenantCredentialRules.Scope(tenant, id), ct).ConfigureAwait(false);
        if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        var current = await Owned(tenant, id, ct).ConfigureAwait(false);
        await CompleteRead(actor, tenant, state.Tenant.Revision, initiator, ct).ConfigureAwait(false);
        return View(current);
    }
    public async ValueTask<TenantCredentialView> CreateAsync(Guid actor, Guid tenant, TenantCredentialCreate input, long expectedSecurityRevision, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var state = await ForWrite(actor, tenant, AccessScope.ForTenant(tenant), expectedSecurityRevision, true, ct, initiator).ConfigureAwait(false);
        var id = ids.NewId(); var now = time.GetUtcNow();
        var oauth = input.Purpose == CredentialPurpose.GoogleDriveAccount || input.EmailAccount?.AuthMethod == EmailAuthMethod.GoogleOAuth2;
        if (oauth && !string.IsNullOrEmpty(input.ProtectedValue)) throw AgentCoreErrors.Validation("OAuth authorization cannot be supplied as a protected value.");
        // Validate metadata/profiles before preparing a protected value. Freeze enforces that
        // initial OAuth identities are disconnected and contain no caller-asserted authorization.
        var value = Validate(() => TenantCredentialRules.Freeze(new(id, input.DisplayName, input.Kind, CredentialStatus.Active,
            CredentialRules.Metadata(input.Metadata), CredentialRules.Origins(input.AllowedOrigins), oauth ? "" : "pending-protection", 1, 1, now, now,
            input.Purpose, input.EmailAccount, 0, input.Purpose == CredentialPurpose.GoogleDriveAccount ? new() : null, input.ContainerRegistry)));
        Validate(() => { TenantCredentialRules.RequireInitialOAuth(value); return true; });
        if (!oauth)
        {
            Validate(() => { CredentialRules.ProtectedValue(input.ProtectedValue!); return true; });
            value = value with { ProtectedPayload = protector.Protect(id, 1, input.ProtectedValue!) };
        }
        var root = new TenantCredentialRoot(tenant, id, now);
        if (!await credentials.TrySaveTenantCredentialAsync(Advance(state), expectedSecurityRevision, root, value, 0, 0, Audit(tenant, actor, id, "create", now), ct, initiator).ConfigureAwait(false))
            throw new SecurityConflictException("stale_revision");
        return View(new(root, value, 0));
    }
    public async ValueTask<TenantCredentialView> UpdateAsync(Guid actor, Guid tenant, Guid id, TenantCredentialUpdate input,
        long expectedSecurityRevision, long expectedRevision, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var owned = await Owned(tenant, id, ct).ConfigureAwait(false);
        var state = await ForWrite(actor, tenant, TenantCredentialRules.Scope(tenant, id), expectedSecurityRevision, true, ct, initiator).ConfigureAwait(false);
        RequireRevision(owned, expectedRevision); var now = time.GetUtcNow();
        var email = input.EmailAccount ?? owned.Value.EmailAccount;
        if (owned.Value.EmailAccount?.AuthMethod == EmailAuthMethod.GoogleOAuth2 && input.EmailAccount is { } incoming)
        {
            var plain = incoming.GoogleSubject is null && incoming.GrantedScopes is null && incoming.ConnectionStatus == EmailConnectionStatus.NotConnected
                && incoming.AuthEpoch == 0 && incoming.ConnectedAtUtc is null && !incoming.Synthetic;
            if (incoming.Preset != owned.Value.EmailAccount.Preset || incoming.AuthMethod != owned.Value.EmailAccount.AuthMethod
                || !string.Equals(incoming.EmailAddress, owned.Value.EmailAccount.EmailAddress, StringComparison.OrdinalIgnoreCase)
                || !plain && System.Text.Json.JsonSerializer.Serialize(incoming) != System.Text.Json.JsonSerializer.Serialize(owned.Value.EmailAccount))
                throw AgentCoreErrors.Validation("OAuth identity is managed through authorization.");
            email = owned.Value.EmailAccount;
        }
        var baseline = TenantCredentialOAuthRules.AdvanceStatusEpoch(owned.Value, input.Status);
        if (baseline.EmailAccount?.AuthMethod == EmailAuthMethod.GoogleOAuth2) email = baseline.EmailAccount;
        var value = Validate(() => TenantCredentialRules.Freeze(baseline with { DisplayName = input.DisplayName, Status = input.Status,
            Metadata = CredentialRules.Metadata(input.Metadata), AllowedOrigins = CredentialRules.Origins(input.AllowedOrigins),
            EmailAccount = email, ContainerRegistry = input.ContainerRegistry ?? owned.Value.ContainerRegistry,
            Revision = checked(expectedRevision + 1), UpdatedAtUtc = now }));
        Validate(() => { TenantCredentialRules.ValidateChange(owned.Owner, value, owned.Value, expectedRevision, owned.Value.SecretRevision); return true; });
        if (!await credentials.TrySaveTenantCredentialAsync(Advance(state), expectedSecurityRevision, owned.Owner, value, expectedRevision, owned.Value.SecretRevision,
            Audit(tenant, actor, id, "update", now), ct, initiator).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        return View(owned with { Value = value });
    }
    public async ValueTask<TenantCredentialView> ReplaceAsync(Guid actor, Guid tenant, Guid id, string value,
        long expectedSecurityRevision, long expectedRevision, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var owned = await Owned(tenant, id, ct).ConfigureAwait(false);
        var state = await ForWrite(actor, tenant, TenantCredentialRules.Scope(tenant, id), expectedSecurityRevision, true, ct, initiator).ConfigureAwait(false);
        RequireRevision(owned, expectedRevision);
        if (owned.Value.Purpose == CredentialPurpose.GoogleDriveAccount || owned.Value.EmailAccount?.AuthMethod == EmailAuthMethod.GoogleOAuth2)
            throw AgentCoreErrors.Validation("Use correlated OAuth authorization to replace this connection.");
        Validate(() => { CredentialRules.ProtectedValue(value); return true; }); var now = time.GetUtcNow();
        var updated = owned.Value with { ProtectedPayload = protector.Protect(id, owned.Value.ProtectionVersion, value),
            SecretRevision = checked(owned.Value.SecretRevision + 1), Revision = checked(expectedRevision + 1), UpdatedAtUtc = now };
        if (!await credentials.TrySaveTenantCredentialAsync(Advance(state), expectedSecurityRevision, owned.Owner, updated, expectedRevision, owned.Value.SecretRevision,
            Audit(tenant, actor, id, "replace", now), ct, initiator).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        return View(owned with { Value = updated });
    }
    public async ValueTask DeleteAsync(Guid actor, Guid tenant, Guid id, long expectedSecurityRevision, long expectedRevision, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var owned = await Owned(tenant, id, ct).ConfigureAwait(false);
        var state = await ForWrite(actor, tenant, TenantCredentialRules.Scope(tenant, id), expectedSecurityRevision, false, ct, initiator).ConfigureAwait(false);
        RequireRevision(owned, expectedRevision); var scope = TenantCredentialRules.Scope(tenant, id); var now = time.GetUtcNow();
        var next = Advance(state with { Assignments = state.Assignments.Where(a => a.Scope != scope).ToArray() });
        if (!await credentials.TryDeleteTenantCredentialAsync(next, expectedSecurityRevision, id, expectedRevision, owned.Value.SecretRevision, now,
            Audit(tenant, actor, id, "delete", now), ct, initiator).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
    }
    private async ValueTask<TenantSecurityState> State(Guid actor, Guid tenant, CancellationToken ct, HumanSessionReference? initiator)
    {
        if (initiator is null) return await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        if (initiator.PrincipalId != actor) throw new AccessDeniedException("initiator_unavailable");
        return await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, initiator, ct).ConfigureAwait(false);
    }
    private async ValueTask CompleteRead(Guid actor, Guid tenant, long expectedRevision, HumanSessionReference? initiator, CancellationToken ct)
    {
        if (initiator is null) return;
        var current = await State(actor, tenant, ct, initiator).ConfigureAwait(false);
        if (current.Tenant.Revision != expectedRevision) throw new SecurityConflictException("stale_revision");
    }
    private async ValueTask<TenantCredential> Owned(Guid tenant, Guid id, CancellationToken ct) => await credentials.FindTenantCredentialAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
    private async ValueTask<TenantSecurityState> ForWrite(Guid actor, Guid tenant, AccessScope scope, long expected, bool read, CancellationToken ct, HumanSessionReference? initiator)
    {
        var state = await State(actor, tenant, ct, initiator).ConfigureAwait(false);
        foreach (var key in read ? new[] { "credentials.manage", "credentials.read" } : new[] { "credentials.manage" })
        {
            var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        if (state.Tenant.Revision != expected) throw new SecurityConflictException("stale_revision");
        return state;
    }
    private static void RequireRevision(TenantCredential current, long revision) { if (revision < 1 || current.Value.Revision != revision) throw new SecurityConflictException("stale_revision"); }
    private static TenantSecurityState Advance(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
    private AuthorizationAudit Audit(Guid tenant, Guid actor, Guid id, string operation, DateTimeOffset at) => new(ids.NewId(), tenant, actor, "credentials." + operation, id.ToString("D"), "allowed", "credential_changed", at);
    private static TenantCredentialView View(TenantCredential item) => new(item.Owner.TenantId, CredentialProjection.View(item.Value, item.BindingCount));
    private static T Validate<T>(Func<T> action) { try { return action(); } catch (ArgumentException ex) { throw AgentCoreErrors.Validation(ex.Message); } }
}
