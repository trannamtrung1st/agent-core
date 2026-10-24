using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ISystemAdministrationStore
{
    private Account RequireAccountHuman(HumanSessionReference human)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (!_accounts.TryGetValue(human.AccountId, out var account) || account.Status != IdentityStatus.Active
            || !_sessions.TryGetValue(human.AuthenticationSessionId, out var session) || session.AccountId != human.AccountId || session.RevokedAt is not null
            || session.ExpiresAt <= now || session.SecurityStamp != account.SecurityStamp
            || !_system.Principals.Any(p => p.PrincipalId == human.PrincipalId && p.AccountId == human.AccountId && p.TenantId is null
                && p.Kind == PrincipalKind.User && p.Status == IdentityStatus.Active && p.UserId is null && p.SystemIdentityId is null))
            throw new AccessDeniedException("initiator_unavailable");
        return account;
    }
    private Account RequireSystemHuman(HumanSessionReference human, string permission, CancellationToken ct)
    {
        var account = RequireAccountHuman(human);
        if (!SystemAdministrationRules.Decision(_system, account, human.PrincipalId, permission, new IamAuthorizer(this), ct).GetAwaiter().GetResult().Allowed)
            throw new AccessDeniedException("permission_denied");
        return account;
    }
    public ValueTask<SystemAccountPage> QuerySystemAccountsAsync(SystemCollectionQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); query = SystemAdministrationRules.Normalize(query); lock (_gate)
        {
            var rows = _accounts.Values.Where(a => query.Status is null || a.Status == query.Status)
                .Where(a => query.Search is null || a.DisplayName.Contains(query.Search, StringComparison.Ordinal) || a.LoginKey.Contains(query.Search, StringComparison.Ordinal)).ToArray();
            return ValueTask.FromResult(new SystemAccountPage(rows.OrderBy(a => a.CreatedAt).ThenBy(a => a.AccountId).Skip(query.Page * query.PageSize).Take(query.PageSize)
                .Select(SystemAdministrationRules.Summary).ToArray(), rows.Length, query.Page, query.PageSize));
        }
    }
    public ValueTask<SystemTenantPage> QuerySystemTenantsAsync(SystemCollectionQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); query = SystemAdministrationRules.Normalize(query); lock (_gate)
        {
            var rows = _tenants.Values.Select(t => t.Tenant).Where(t => query.Status is null || t.Status == query.Status)
                .Where(t => query.Search is null || t.Name.Contains(query.Search, StringComparison.Ordinal) || t.Slug.Contains(query.Search, StringComparison.Ordinal)).ToArray();
            return ValueTask.FromResult(new SystemTenantPage(rows.OrderBy(t => t.CreatedAt).ThenBy(t => t.TenantId).Skip(query.Page * query.PageSize).Take(query.PageSize).ToArray(), rows.Length, query.Page, query.PageSize));
        }
    }
    public ValueTask<bool> TryCreateSystemTenantAsync(HumanSessionReference human, TenantSecurityState tenant, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); tenant = IamStateRules.Freeze(tenant);
        SystemAdministrationRules.NewTenant(human, tenant); SystemAdministrationRules.Audit(human, audit, "system.tenants.create", tenant.Tenant.TenantId, tenant.Tenant.TenantId);
        lock (_gate)
        {
            RequireSystemHuman(human, "system.tenants.create", ct);
            if (_audit.Any(a => a.AuditId == audit.AuditId)) return ValueTask.FromResult(false);
            return TryCreateTenantAsync(tenant, audit, ct);
        }
    }
    public ValueTask<bool> TrySetSystemAccountStatusAsync(HumanSessionReference human, Guid accountId, string expectedVersion, IdentityStatus status,
        string nextSecurityStamp, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); SystemAdministrationRules.AccountStatus(expectedVersion, status);
        SystemAdministrationRules.Audit(human, audit, "system.accounts.status", accountId);
        lock (_gate)
        {
            RequireSystemHuman(human, "system.accounts.manage", ct);
            var account = _accounts.GetValueOrDefault(accountId) ?? throw new AccessDeniedException("account_unavailable");
            if (SystemAdministrationRules.Version(account) != expectedVersion) return ValueTask.FromResult(false);
            if (string.IsNullOrWhiteSpace(nextSecurityStamp) || nextSecurityStamp == account.SecurityStamp) throw new ArgumentException("Account status must rotate its security stamp.");
            if (_audit.Any(a => a.AuditId == audit.AuditId)) return ValueTask.FromResult(false);
            var next = account with { Status = status, SecurityStamp = nextSecurityStamp };
            SystemAdministrationRules.EnsureSystemOperator(_system, _accounts.Values.ToArray(), next);
            return TryUpdateAccountAsync(next, account.SecurityStamp, audit, ct);
        }
    }
    public ValueTask<bool> TrySetSystemTenantStatusAsync(HumanSessionReference human, Guid tenantId, long expectedRevision, IdentityStatus status,
        AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); SystemAdministrationRules.TenantStatus(expectedRevision, status);
        SystemAdministrationRules.Audit(human, audit, "system.tenants.status", tenantId);
        lock (_gate)
        {
            RequireSystemHuman(human, "system.tenants.manage", ct);
            var state = _tenants.GetValueOrDefault(tenantId) ?? throw new AccessDeniedException("tenant_unavailable");
            if (state.Tenant.Revision != expectedRevision || _audit.Any(a => a.AuditId == audit.AuditId)) return ValueTask.FromResult(false);
            _tenants[tenantId] = state with { Tenant = state.Tenant with { Status = status, Revision = checked(expectedRevision + 1) } };
            _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
}
