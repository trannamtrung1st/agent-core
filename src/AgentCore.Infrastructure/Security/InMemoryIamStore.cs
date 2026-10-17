using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Definitions;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore(InMemoryAgentInstanceStore instances, InMemoryAgentDefinitionAdminStore definitions, InMemoryMemoryStore definitionReferences,
    IBuiltInAgentDefinitionStore? builtIns = null, AgentCore.Application.Agents.InheritedDefinitionResourceCatalog? inheritedResources = null,
    InMemoryCredentialStore? credentials = null, TimeProvider? clock = null) : IIamStore, ITenantAgentStore, ITenantDefinitionStore, ITenantCredentialStore, ITenantCredentialBindingStore, ITenantCredentialOAuthStore, ITenantSessionStore, ITenantRunStore, ITenantOperationPolicyStore
{
    private readonly InMemoryMemoryStore sessionBacking = definitionReferences.BindTenantInstances(instances);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Account> _accounts = [];
    private readonly Dictionary<Guid, TenantSecurityState> _tenants = [];
    private readonly Dictionary<Guid, AuthenticationSession> _sessions = [];
    private readonly List<AuthorizationAudit> _audit = [];
    private SystemSecurityState _system = new([], [], []);
    private bool _bootstrapped;

    public ValueTask<Account?> FindAccountAsync(string loginKey, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_accounts.Values.SingleOrDefault(a => a.LoginKey == loginKey)); }
    public ValueTask<Account?> ReadAccountAsync(Guid accountId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_accounts.GetValueOrDefault(accountId)); }
    public ValueTask<bool> HasBootstrapAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_bootstrapped); }
    public ValueTask<bool> TryBootstrapAsync(Account account, SystemSecurityState system, TenantSecurityState tenant, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); tenant = IamStateRules.Freeze(tenant);
        if (tenant.SystemIdentities.Count > 0 || tenant.Assignments.Any(a => a.Scope.Kind == AccessScopeKind.Resource))
            throw new ArgumentException("Bootstrap cannot seed resource identities or grants.");
        lock (_gate)
        {
            if (_bootstrapped || _accounts.Count > 0 || _tenants.Count > 0) return ValueTask.FromResult(false);
            _accounts.Add(account.AccountId, account); _system = FreezeSystem(system);
            _tenants.Add(tenant.Tenant.TenantId, tenant); _audit.Add(audit); _bootstrapped = true;
            return ValueTask.FromResult(true);
        }
    }
    public ValueTask<bool> TryCreateAccountAsync(Account account, Principal accountPrincipal, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            if (!_bootstrapped) throw new SecurityConflictException("bootstrap_required");
            if (_accounts.ContainsKey(account.AccountId) || _accounts.Values.Any(a => a.LoginKey == account.LoginKey)) return ValueTask.FromResult(false);
            ValidateAccountPrincipal(account, accountPrincipal);
            if (_system.Principals.Any(p => p.PrincipalId == accountPrincipal.PrincipalId)) return ValueTask.FromResult(false);
            _accounts.Add(account.AccountId, account);
            _system = FreezeSystem(_system with { Principals = _system.Principals.Append(accountPrincipal).ToArray() });
            return ValueTask.FromResult(true);
        }
    }
    public ValueTask<bool> TryUpdateAccountAsync(Account account, string expectedSecurityStamp, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            if (!_accounts.TryGetValue(account.AccountId, out var old) || old.SecurityStamp != expectedSecurityStamp) return ValueTask.FromResult(false);
            if (account.LoginKey != old.LoginKey || audit.TenantId is not null || string.IsNullOrEmpty(account.SecurityStamp)) throw new ArgumentException("Invalid Account update.");
            if (account.Status != IdentityStatus.Active)
                foreach (var state in _tenants.Values.Where(t => t.Users.Any(u => u.AccountId == account.AccountId)))
                    EnsureActiveAccountOwner(state, account);
            _accounts[account.AccountId] = account; _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
    public ValueTask<SystemSecurityState> ReadSystemAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_system); }
    public ValueTask<TenantSecurityState?> ReadTenantAsync(Guid tenantId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_tenants.GetValueOrDefault(tenantId)); }
    public ValueTask<IReadOnlyList<TenantMembership>> ListMembershipsAsync(Guid accountId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            IReadOnlyList<TenantMembership> result = _tenants.Values.Where(t => t.Tenant.Status == IdentityStatus.Active)
                .SelectMany(t => t.Users.Where(u => u.AccountId == accountId && u.Status == IdentityStatus.Active).Select(u => new TenantMembership(t.Tenant, u)))
                .OrderBy(m => m.Tenant.Name, StringComparer.Ordinal).ThenBy(m => m.Tenant.TenantId).ToArray();
            return ValueTask.FromResult(result);
        }
    }
    public ValueTask<bool> TryCreateTenantAsync(TenantSecurityState tenant, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); tenant = IamStateRules.Freeze(tenant); lock (_gate) lock (instances.CredentialGate)
        {
            if (!_bootstrapped || tenant.Tenant.Revision != 1 || _tenants.ContainsKey(tenant.Tenant.TenantId)
                || _tenants.Values.Any(t => t.Tenant.Slug == tenant.Tenant.Slug)) return ValueTask.FromResult(false);
            EnsureReferences(tenant, audit, ct: ct); _tenants.Add(tenant.Tenant.TenantId, tenant); MarkOwnedAgents(tenant); _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
    public ValueTask<bool> TrySaveTenantAsync(TenantSecurityState tenant, long expectedRevision, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); tenant = IamStateRules.Freeze(tenant); lock (_gate) lock (instances.CredentialGate)
        {
            if (tenant.Tenant.Revision != checked(expectedRevision + 1) || !_tenants.TryGetValue(tenant.Tenant.TenantId, out var old)
                || old.Tenant.Revision != expectedRevision || _tenants.Values.Any(t => t.Tenant.TenantId != tenant.Tenant.TenantId && t.Tenant.Slug == tenant.Tenant.Slug)) return ValueTask.FromResult(false);
            IamStateRules.ValidateAgentContinuity(old.SystemIdentities, tenant);
            foreach (var actor in IamStateRules.SecurityWriteActors(old, tenant, audit)) ValidateAgentActor(actor.State, actor.Principal);
            EnsureReferences(tenant, audit, ct: ct); _tenants[tenant.Tenant.TenantId] = tenant; MarkOwnedAgents(tenant); _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
    public ValueTask SaveSessionAsync(AuthenticationSession session, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            if (!_accounts.ContainsKey(session.AccountId)) throw new ArgumentException("Unknown Account.", nameof(session));
            if (!_sessions.TryAdd(session.SessionId, session)) throw new SecurityConflictException("session_exists");
            return ValueTask.CompletedTask;
        }
    }
    public ValueTask<AuthenticationSession?> ReadSessionAsync(Guid sessionId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_sessions.GetValueOrDefault(sessionId)); }
    public ValueTask RevokeSessionAsync(Guid sessionId, DateTimeOffset now, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        { if (_sessions.TryGetValue(sessionId, out var session) && session.RevokedAt is null) _sessions[sessionId] = session with { RevokedAt = now }; return ValueTask.CompletedTask; }
    }
    public ValueTask<IReadOnlyList<AuthorizationAudit>> ReadAuditAsync(Guid tenantId, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult<IReadOnlyList<AuthorizationAudit>>(_audit.Where(a => a.TenantId == tenantId)
            .OrderByDescending(a => a.At).ThenByDescending(a => a.AuditId).Take(Math.Clamp(limit, 1, 200)).ToArray());
    }
    private void EnsureReferences(TenantSecurityState state, AuthorizationAudit audit, Guid? creatingInstance = null, CancellationToken ct = default)
    {
        if (state.Assignments.Any(a => TenantApprovalRules.TryApprovalId(a.Scope, out var id) && _approvals.GetValueOrDefault(id)?.TenantId != state.Tenant.TenantId))
            throw new ArgumentException("An Approval assignment requires its actual owned request.");
        if (state.Assignments.Any(a => TenantRunRules.TryId(a.Scope, out var run) && sessionBacking.ReadTenantRun(state.Tenant.TenantId, run) is null))
            throw new ArgumentException("A Run assignment requires a current owned Run.");
        if (state.Assignments.Any(a => TenantSessionRules.TryId(a.Scope, out var session) && !sessionBacking.OwnsTenantSession(state.Tenant.TenantId, session)))
            throw new ArgumentException("A Session assignment requires a current owned Session.");
        if (state.Assignments.Any(a => TenantCredentialRules.TryId(a.Scope, out var id) && credentials?.OwnsTenantCredential(state.Tenant.TenantId, id) != true))
            throw new ArgumentException("A credential assignment requires a current owned credential.");
        if (audit.TenantId != state.Tenant.TenantId || state.Users.Any(u => !_accounts.ContainsKey(u.AccountId)))
            throw new ArgumentException("Invalid Account or audit ownership.");
        EnsureActiveAccountOwner(state);
        if (state.Assignments.Any(a => TenantDefinitionResourceScopes.TryParse(a.Scope, out var reference) && !OwnsDefinitionResource(state.Tenant.TenantId, reference)))
            throw new ArgumentException("A Definition resource assignment requires a current owned binding.");
        if (state.Assignments.Any(a => a.Scope.Kind == AccessScopeKind.Resource && a.Scope.ResourceType == "definition"
            && (!Guid.TryParseExact(a.Scope.ResourceId, "D", out var id) || !definitions.OwnsActiveDefinition(state.Tenant.TenantId, id))))
            throw new ArgumentException("A definition assignment requires an owned definition.");
        foreach (var group in state.Assignments.Where(a => TenantAgentChildScopes.TryParse(a.Scope, out _, out _))
            .GroupBy(a => { TenantAgentChildScopes.TryParse(a.Scope, out var id, out _); return id; }))
        {
            var catalog = ChildCatalog(state.Tenant.TenantId, group.Key, null, ct);
            if (group.Any(a => !catalog.Contains(a.Scope))) throw new ArgumentException("A child assignment requires a current child of the owned Instance.");
        }
        if (state.SystemIdentities.Any(i => i.AgentInstanceId != creatingInstance && instances.FindTenantBackingAsync(i.AgentInstanceId!.Value).Result is null))
            throw new ArgumentException("An agent identity requires a backing Instance.");
        if (state.SystemIdentities.Any(i => _tenants.Values.Any(t => t.Tenant.TenantId != state.Tenant.TenantId
            && t.SystemIdentities.Any(other => other.AgentInstanceId == i.AgentInstanceId))))
            throw new SecurityConflictException("agent_identity_exists");
        if (state.Principals.Any(p => _tenants.Values.Any(t => t.Tenant.TenantId != state.Tenant.TenantId && t.Principals.Any(other => other.PrincipalId == p.PrincipalId))))
            throw new ArgumentException("A Principal cannot belong to two tenants.");
        ct.ThrowIfCancellationRequested();
    }
    private void MarkOwnedAgents(TenantSecurityState state)
    {
        foreach (var identity in state.SystemIdentities) instances.MarkTenantOwned(identity.AgentInstanceId!.Value);
    }
    private void EnsureActiveAccountOwner(TenantSecurityState state, Account? changed = null)
    {
        var role = state.Roles.Single(r => r.Key == BuiltinAccessRoles.Owner && r.IsBuiltin);
        var owners = state.Assignments.Where(a => a.RoleId == role.RoleId && a.Scope == AccessScope.ForTenant(state.Tenant.TenantId)).Select(a => a.PrincipalId).ToHashSet();
        if (!state.Users.Any(u => owners.Contains(u.PrincipalId) && u.Status == IdentityStatus.Active
            && (u.AccountId == changed?.AccountId ? changed.Status : _accounts.GetValueOrDefault(u.AccountId)?.Status) == IdentityStatus.Active))
            throw new SecurityConflictException("last_owner");
    }
    private static void ValidateAccountPrincipal(Account account, Principal principal)
    {
        if (principal.AccountId != account.AccountId || principal.Kind != PrincipalKind.User || principal.TenantId is not null
            || principal.UserId is not null || principal.SystemIdentityId is not null || principal.Status != account.Status)
            throw new ArgumentException("Invalid Account Principal.");
    }
    private static SystemSecurityState FreezeSystem(SystemSecurityState system) => new(
        Array.AsReadOnly(system.Principals.ToArray()), Array.AsReadOnly(system.Roles.Select(r => r with { Permissions = Array.AsReadOnly(r.Permissions.ToArray()) }).ToArray()),
        Array.AsReadOnly(system.Assignments.ToArray()));
}
