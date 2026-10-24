using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record BootstrapIdentityResult(Guid AccountId, Guid TenantId, Guid OwnerPrincipalId);
public sealed record SignedInAccount(Account Account, AuthenticationSession Session);

public sealed class IdentityService(IIamStore store, IAccountPasswordHasher passwords, IIdentityTokens tokens,
    IIdGenerator ids, TimeProvider time)
{
    public async ValueTask<BootstrapIdentityResult> BootstrapAsync(string login, string displayName, string password,
        string tenantName, string tenantSlug, CancellationToken ct = default)
    {
        if (await store.HasBootstrapAsync(ct).ConfigureAwait(false)) throw new SecurityConflictException("bootstrap_complete");
        var account = CreateAccount(login, displayName, password);
        var systemPrincipal = new Principal(ids.NewId(), PrincipalKind.User, null, account.AccountId, null, null, IdentityStatus.Active);
        var systemRole = BuiltinAccessRoles.ForSystem(ids.NewId);
        var system = new SystemSecurityState([systemPrincipal], [systemRole],
            [new(ids.NewId(), null, systemPrincipal.PrincipalId, systemRole.RoleId, AccessScope.System, systemPrincipal.PrincipalId, time.GetUtcNow())]);
        var tenant = CreateTenant(account, tenantName, tenantSlug);
        var audit = new AuthorizationAudit(ids.NewId(), null, systemPrincipal.PrincipalId, "identity.bootstrap", account.AccountId.ToString("D"), "allowed", "explicit_cli_bootstrap", time.GetUtcNow());
        if (!await store.TryBootstrapAsync(account, system, tenant, audit, ct).ConfigureAwait(false)) throw new SecurityConflictException("bootstrap_complete");
        return new(account.AccountId, tenant.Tenant.TenantId, tenant.Users.Single().PrincipalId);
    }

    // Account registration grants no tenant or system authority. Invitations are accepted separately.
    public async ValueTask<Account> RegisterAsync(string login, string displayName, string password, CancellationToken ct = default)
    {
        var account = CreateAccount(login, displayName, password);
        var principal = new Principal(ids.NewId(), PrincipalKind.User, null, account.AccountId, null, null, IdentityStatus.Active);
        if (!await store.TryCreateAccountAsync(account, principal, ct).ConfigureAwait(false)) throw new SecurityConflictException("account_exists");
        return account;
    }

    public async ValueTask<SignedInAccount?> SignInAsync(string login, string password, CancellationToken ct = default)
    {
        // Always perform the vetted password verifier, including an unknown account, to reduce login enumeration.
        // The dummy hash is cached by the Infrastructure hasher, not a per-request unbounded hash computation.
        ArgumentNullException.ThrowIfNull(password);
        var account = await store.FindAccountAsync(NormalizeLogin(login), ct).ConfigureAwait(false);
        var valid = passwords.Verify(account ?? DummyAccount, password);
        if (!valid || account?.Status != IdentityStatus.Active) return null;
        var now = time.GetUtcNow();
        var session = new AuthenticationSession(ids.NewId(), account.AccountId, account.SecurityStamp, now, now.AddHours(12));
        await store.SaveSessionAsync(session, ct).ConfigureAwait(false);
        return new(account, session);
    }

    public async ValueTask ChangePasswordAsync(Guid accountId, string currentPassword, string newPassword, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        if (initiator is not null)
        {
            if (initiator.AccountId != accountId) throw new AccessDeniedException("initiator_unavailable");
            _ = await RequireAccountHumanAsync(initiator, ct).ConfigureAwait(false);
        }
        ArgumentNullException.ThrowIfNull(currentPassword);
        ArgumentNullException.ThrowIfNull(newPassword);
        if (newPassword.Length is < 12 or > 256) throw new ArgumentException("Use a password between 12 and 256 characters.", nameof(newPassword));
        var account = await store.ReadAccountAsync(accountId, ct).ConfigureAwait(false);
        if (account?.Status != IdentityStatus.Active || !passwords.Verify(account, currentPassword)) throw new AccessDeniedException("invalid_credentials");
        var principal = (await store.ReadSystemAsync(ct).ConfigureAwait(false)).Principals.SingleOrDefault(p => p.AccountId == accountId && p.Kind == PrincipalKind.User)
            ?? throw new AccessDeniedException("account_unavailable");
        var updated = account with { PasswordHash = passwords.Hash(account, newPassword), SecurityStamp = tokens.Create() };
        var audit = new AuthorizationAudit(ids.NewId(), null, principal.PrincipalId, "account.password.change", accountId.ToString("D"), "allowed", "reauthenticated", time.GetUtcNow());
        if (!await store.TryUpdateAccountAsync(updated, account.SecurityStamp, audit, ct, initiator).ConfigureAwait(false)) throw new SecurityConflictException("stale_account");
    }

    public async ValueTask<Account?> ValidateSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await store.ReadSessionAsync(sessionId, ct).ConfigureAwait(false);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= time.GetUtcNow()) return null;
        var account = await store.ReadAccountAsync(session.AccountId, ct).ConfigureAwait(false);
        return account?.Status == IdentityStatus.Active && account.SecurityStamp == session.SecurityStamp ? account : null;
    }

    public async ValueTask<Account> RequireAccountHumanAsync(HumanSessionReference human, CancellationToken ct = default)
    {
        var account = await ValidateSessionAsync(human.AuthenticationSessionId, ct).ConfigureAwait(false);
        var system = await store.ReadSystemAsync(ct).ConfigureAwait(false);
        if (account?.AccountId != human.AccountId || !system.Principals.Any(p => p.PrincipalId == human.PrincipalId && p.AccountId == human.AccountId
            && p.TenantId is null && p.UserId is null && p.SystemIdentityId is null && p.Kind == PrincipalKind.User && p.Status == IdentityStatus.Active))
            throw new AccessDeniedException("initiator_unavailable");
        return account;
    }

    public ValueTask SignOutAsync(Guid sessionId, CancellationToken ct = default) => store.RevokeSessionAsync(sessionId, time.GetUtcNow(), ct);

    public async ValueTask<TenantUser?> ResolveUserAsync(Guid accountId, Guid tenantId, CancellationToken ct = default)
    {
        var account = await store.ReadAccountAsync(accountId, ct).ConfigureAwait(false);
        if (account?.Status != IdentityStatus.Active) return null;
        var state = await store.ReadTenantAsync(tenantId, ct).ConfigureAwait(false);
        if (state?.Tenant.Status != IdentityStatus.Active) return null;
        var user = state.Users.SingleOrDefault(u => u.AccountId == accountId && u.Status == IdentityStatus.Active);
        return user is not null && state.Principals.Any(p => p.PrincipalId == user.PrincipalId && p.UserId == user.UserId
            && p.AccountId == accountId && p.TenantId == tenantId && p.Status == IdentityStatus.Active && p.Kind == PrincipalKind.User) ? user : null;
    }

    internal TenantSecurityState CreateTenant(Account account, string name, string slug)
    {
        name = CleanName(name); slug = NormalizeSlug(slug);
        var tenantId = ids.NewId(); var userId = ids.NewId(); var principalId = ids.NewId(); var now = time.GetUtcNow();
        var roles = BuiltinAccessRoles.ForTenant(tenantId, ids.NewId);
        return new(new(tenantId, name, slug, IdentityStatus.Active, 1, now),
            [new(userId, tenantId, account.AccountId, principalId, IdentityStatus.Active, now)],
            [new(principalId, PrincipalKind.User, tenantId, account.AccountId, userId, null, IdentityStatus.Active)], [], roles,
            [new(ids.NewId(), tenantId, principalId, roles.Single(r => r.Key == BuiltinAccessRoles.Owner).RoleId,
                AccessScope.ForTenant(tenantId), principalId, now)], []);
    }

    private Account CreateAccount(string login, string displayName, string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (password.Length is < 12 or > 256) throw new ArgumentException("Use a password between 12 and 256 characters.", nameof(password));
        var account = new Account(ids.NewId(), NormalizeLogin(login), CleanName(displayName), "", tokens.Create(), IdentityStatus.Active, time.GetUtcNow());
        return account with { PasswordHash = passwords.Hash(account, password) };
    }

    public static string NormalizeLogin(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var login = value.Trim().ToUpperInvariant();
        if (login.Length is < 3 or > 254 || !login.Contains('@') || login.Any(char.IsControl) || login.Any(char.IsWhiteSpace))
            throw new ArgumentException("Enter a valid account email.", nameof(value));
        return login;
    }
    internal static string CleanName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var name = value.Trim();
        if (name.Length is < 1 or > 160 || name.Any(char.IsControl)) throw new ArgumentException("Use a name between 1 and 160 characters.", nameof(value));
        return name;
    }
    internal static string NormalizeSlug(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var slug = value.Trim().ToLowerInvariant();
        if (slug.Length is < 2 or > 64 || slug.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
            || slug[0] == '-' || slug[^1] == '-') throw new ArgumentException("Use 2–64 lowercase letters, digits or internal hyphens.", nameof(value));
        return slug;
    }
    private static readonly Account DummyAccount = new(Guid.Empty, "UNKNOWN@INVALID.EXAMPLE", "", "", "", IdentityStatus.Suspended, DateTimeOffset.UnixEpoch);
}
