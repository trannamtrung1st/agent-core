using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ISystemOperatorStore
{
    public ValueTask<SystemOperatorPage> QuerySystemOperatorsAsync(SystemCollectionQuery query, bool? isOperator, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); query = SystemAdministrationRules.Normalize(query);
        lock (_gate)
        {
            var rows = _accounts.Values.Where(a => query.Status is null || a.Status == query.Status)
                .Where(a => query.Search is null || a.DisplayName.Contains(query.Search, StringComparison.Ordinal) || a.LoginKey.Contains(query.Search, StringComparison.Ordinal))
                .Select(a => SystemOperatorRules.Summary(_system, a)).Where(a => isOperator is null || a.IsOperator == isOperator).ToArray();
            return ValueTask.FromResult(new SystemOperatorPage(rows.OrderBy(a => a.CreatedAt).ThenBy(a => a.AccountId).Skip(query.Page * query.PageSize).Take(query.PageSize).ToArray(), rows.Length, query.Page, query.PageSize));
        }
    }
    public ValueTask<bool> TrySetSystemOperatorAsync(HumanSessionReference human, Guid accountId, string expectedVersion, bool enabled,
        Guid assignmentId, string nextSecurityStamp, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); SystemAdministrationRules.AccountStatus(expectedVersion, IdentityStatus.Active);
        SystemAdministrationRules.Audit(human, audit, enabled ? "system.operators.assign" : "system.operators.revoke", accountId);
        lock (_gate)
        {
            RequireSystemHuman(human, "system.governance.manage", ct);
            var target = _accounts.GetValueOrDefault(accountId) ?? throw new AccessDeniedException("account_unavailable");
            if (SystemOperatorRules.Version(_system, target) != expectedVersion || _audit.Any(a => a.AuditId == audit.AuditId)) return ValueTask.FromResult(false);
            if (string.IsNullOrWhiteSpace(nextSecurityStamp) || nextSecurityStamp == target.SecurityStamp) throw new ArgumentException("Operator changes must rotate the Account security stamp.");
            var next = SystemOperatorRules.Change(_system, _accounts.Values.ToArray(), target, enabled, assignmentId, human, audit.At);
            _system = FreezeSystem(next); _accounts[accountId] = target with { SecurityStamp = nextSecurityStamp }; _audit.Add(audit);
            return ValueTask.FromResult(true);
        }
    }
}
