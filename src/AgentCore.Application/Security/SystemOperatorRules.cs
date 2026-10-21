using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class SystemOperatorRules
{
    public static AccessRole Role(SystemSecurityState system) => system.Roles.Single(r => r.TenantId is null && r.IsBuiltin && r.HumanOnly && r.Key == BuiltinAccessRoles.SystemOperator);
    public static Principal Principal(SystemSecurityState system, Account account) => system.Principals.SingleOrDefault(p =>
        p.AccountId == account.AccountId && p.TenantId is null && p.Kind == PrincipalKind.User && p.UserId is null && p.SystemIdentityId is null && p.Status == IdentityStatus.Active)
        ?? throw new AccessDeniedException("account_unavailable");
    public static RoleAssignment[] Assignments(SystemSecurityState system, Account account)
    {
        var principal = Principal(system, account); var role = Role(system);
        return system.Assignments.Where(a => a.TenantId is null && a.PrincipalId == principal.PrincipalId && a.RoleId == role.RoleId && a.Scope == AccessScope.System).OrderBy(a => a.AssignmentId).ToArray();
    }
    public static string Version(SystemSecurityState system, Account account) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"system-operator-v1:{SystemAdministrationRules.Version(account)}:{Principal(system, account).PrincipalId:D}:{Role(system).RoleId:D}:{string.Join(',', Assignments(system, account).Select(a => a.AssignmentId.ToString("D")))}")));
    public static SystemOperatorSummary Summary(SystemSecurityState system, Account account) => new(account.AccountId, account.LoginKey, account.DisplayName,
        account.Status, account.CreatedAt, Assignments(system, account).Length > 0, Version(system, account));
    public static SystemSecurityState Change(SystemSecurityState system, IReadOnlyList<Account> accounts, Account target, bool enabled,
        Guid assignmentId, HumanSessionReference human, DateTimeOffset at)
    {
        var principal = Principal(system, target); var role = Role(system); var existing = Assignments(system, target);
        if (enabled && target.Status != IdentityStatus.Active) throw new AccessDeniedException("account_unavailable");
        if (enabled == (existing.Length > 0)) throw new SecurityConflictException("operator_unchanged");
        if (assignmentId == Guid.Empty || system.Assignments.Any(a => a.AssignmentId == assignmentId)) throw new ArgumentException("Use a fresh assignment identity.");
        var next = system with { Assignments = Array.AsReadOnly(system.Assignments.Where(a => !existing.Contains(a)).Concat(enabled
            ? [new RoleAssignment(assignmentId, null, principal.PrincipalId, role.RoleId, AccessScope.System, human.PrincipalId, at)] : []).ToArray()) };
        // Reuse the Account-status guard against the pre-change graph. Removing this role
        // must leave another active Account with the built-in platform role.
        if (!enabled) SystemAdministrationRules.EnsureSystemOperator(system, accounts, target with { Status = IdentityStatus.Removed });
        return next;
    }
}
