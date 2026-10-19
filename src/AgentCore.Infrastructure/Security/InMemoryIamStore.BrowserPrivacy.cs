using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ISystemBrowserPrivacyStore
{
    public ValueTask<bool> TrySaveSystemBrowserPrivacyAsync(HumanSessionReference human, SystemBrowserPrivacyChange change, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); BrowserPrivacyRules.ValidateAudit(human, audit);
        lock (_gate) lock (browserPrivacyBacking.Gate)
        {
            var now = (clock ?? TimeProvider.System).GetUtcNow();
            if (!_accounts.TryGetValue(human.AccountId, out var account) || account.Status != IdentityStatus.Active
                || !_sessions.TryGetValue(human.AuthenticationSessionId, out var session) || session.AccountId != human.AccountId || session.RevokedAt is not null
                || session.ExpiresAt <= now || session.SecurityStamp != account.SecurityStamp
                || !_system.Principals.Any(p => p.PrincipalId == human.PrincipalId && p.AccountId == human.AccountId && p.TenantId is null
                    && p.Kind == PrincipalKind.User && p.Status == IdentityStatus.Active && p.UserId is null && p.SystemIdentityId is null))
                throw new AccessDeniedException("initiator_unavailable");
            if (!BrowserPrivacyRules.SystemDecision(_system, account, human.PrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult().Allowed)
                throw new AccessDeniedException("permission_denied");
            var policy = BrowserPrivacyRules.Prepare(change, privacyAuthority);
            if (_audit.Any(a => a.AuditId == audit.AuditId) || !browserPrivacyBacking.TrySave(policy, change.ExpectedRevision)) return ValueTask.FromResult(false);
            _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
}
