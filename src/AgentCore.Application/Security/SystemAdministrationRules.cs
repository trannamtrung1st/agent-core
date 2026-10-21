using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class SystemAdministrationRules
{
    public static ValueTask<AccessDecision> Decision(SystemSecurityState state, Account account, Guid actor, string permission, IamAuthorizer iam, CancellationToken ct) =>
        iam.CheckOwnedSystemAsync(state, account, actor, permission, ct);
    public static SystemCollectionQuery Normalize(SystemCollectionQuery query)
    {
        var search = query.Search?.Trim();
        if (search?.Length > 128 || search?.Any(char.IsControl) == true || query.Status is { } status && !Enum.IsDefined(status))
            throw new ArgumentException("Invalid System collection filters.");
        return query with { Page = Math.Clamp(query.Page, 0, 100_000), PageSize = Math.Clamp(query.PageSize, 1, 100), Search = string.IsNullOrEmpty(search) ? null : search };
    }
    // An opaque concurrency witness only; never return the stamp or accept it as authentication.
    public static string Version(Account account) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"account-status-v1:{account.AccountId:D}:{account.Status}:{account.SecurityStamp}")));
    public static SystemAccountSummary Summary(Account account) => new(account.AccountId, account.LoginKey, account.DisplayName, account.Status, account.CreatedAt, Version(account));
    public static IdentityStatus ParseStatus(string value) => Enum.GetNames<IdentityStatus>().Contains(value, StringComparer.Ordinal)
        ? Enum.Parse<IdentityStatus>(value) : throw new ArgumentException("Use Active, Suspended or Removed status.");
    public static void AccountStatus(string version, IdentityStatus status)
    {
        if (version is null || version.Length != 64 || version.Any(c => !char.IsAsciiHexDigit(c)) || !Enum.IsDefined(status)) throw new ArgumentException("Use a current Account version and valid status.");
    }
    public static void TenantStatus(long revision, IdentityStatus status)
    {
        if (revision < 1 || revision == long.MaxValue || status is not (IdentityStatus.Active or IdentityStatus.Suspended)) throw new ArgumentException("Use a current organization revision and Active or Suspended status.");
    }
    public static void Audit(HumanSessionReference human, AuthorizationAudit audit, string action, Guid target, Guid? tenant = null)
    {
        if (target == Guid.Empty || audit.AuditId == Guid.Empty || audit.ActorPrincipalId != human.PrincipalId || audit.TenantId != tenant
            || audit.Action != action || audit.Target != target.ToString("D") || audit.Decision != "allowed" || audit.ReasonCode != "authorized" || audit.Execution is not null || audit.At.Offset != TimeSpan.Zero)
            throw new ArgumentException("Use exact body-free System administration audit facts.");
    }
    public static void EnsureSystemOperator(SystemSecurityState system, IReadOnlyList<Account> accounts, Account next)
    {
        if (next.Status == IdentityStatus.Active) return;
        var roles = system.Roles.Where(r => r.TenantId is null && r.IsBuiltin && r.Key == BuiltinAccessRoles.SystemOperator).Select(r => r.RoleId).ToHashSet();
        var principals = system.Assignments.Where(a => a.TenantId is null && a.Scope == AccessScope.System && roles.Contains(a.RoleId)).Select(a => a.PrincipalId).ToHashSet();
        var operators = system.Principals.Where(p => p.TenantId is null && p.Kind == PrincipalKind.User && p.Status == IdentityStatus.Active && principals.Contains(p.PrincipalId)).Select(p => p.AccountId).ToHashSet();
        if (operators.Contains(next.AccountId) && !accounts.Any(a => operators.Contains(a.AccountId) && (a.AccountId == next.AccountId ? next.Status : a.Status) == IdentityStatus.Active))
            throw new SecurityConflictException("last_system_operator");
    }
    public static void NewTenant(HumanSessionReference human, TenantSecurityState tenant)
    {
        var user = tenant.Users.Count == 1 ? tenant.Users[0] : null;
        if (tenant.Tenant.Status != IdentityStatus.Active || tenant.Tenant.Revision != 1 || user?.AccountId != human.AccountId || user.Status != IdentityStatus.Active
            || tenant.Principals.Count != 1 || tenant.Principals[0].AccountId != human.AccountId || tenant.Principals[0].PrincipalId != user.PrincipalId
            || tenant.SystemIdentities.Count != 0 || tenant.Invitations.Count != 0 || tenant.Assignments.Count != 1
            || tenant.Assignments[0].PrincipalId != user.PrincipalId || !tenant.Roles.Any(r => r.Key == BuiltinAccessRoles.Owner && r.IsBuiltin && r.RoleId == tenant.Assignments[0].RoleId))
            throw new ArgumentException("New organization ownership must belong only to the original human Account.");
    }
}
