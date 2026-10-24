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
            _ = RequireSystemHuman(human, "system.governance.manage", ct);
            var policy = BrowserPrivacyRules.Prepare(change, privacyAuthority);
            if (_audit.Any(a => a.AuditId == audit.AuditId) || !browserPrivacyBacking.TrySave(policy, change.ExpectedRevision)) return ValueTask.FromResult(false);
            _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
}
