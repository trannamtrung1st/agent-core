using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantBrowserProfileStore
{
    public ValueTask<TenantBrowserProfileCommit?> TryBeginTenantProfileResetAsync(TenantBrowserAgentOwner owner, HumanSessionReference human,
        long expectedSecurityRevision, long expectedInstanceRevision, AgentBrowserProfileReset reset, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); owner.Validate(); TenantBrowserProfileRules.Validate(reset);
        if (reset.Status != "pending" || reset.PrincipalId != human.PrincipalId || audit.AuditId != reset.OperationId || audit.TenantId != owner.TenantId
            || audit.ActorPrincipalId != human.PrincipalId || audit.Action != "browser.profile.reset" || audit.Target != owner.AgentInstanceId.ToString("D")
            || audit.Decision != "allowed" || audit.ReasonCode != reset.ReasonCode || audit.At != reset.RequestedAt || audit.Execution is not null)
            throw new ArgumentException("Invalid Browser reset start.");
        lock (_gate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(owner.TenantId, out var state) || state.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantBrowserProfileCommit?>(null);
            try { RequireHumanSession(owner.TenantId, human); } catch (AccessDeniedException) { return ValueTask.FromResult<TenantBrowserProfileCommit?>(null); }
            var current = FindTenantAgentAsync(owner.TenantId, owner.AgentInstanceId, ct).Result;
            if (current is null || !TenantBrowserProfileRules.Matches(owner, current) || current.Instance.Revision != expectedInstanceRevision
                || current.Instance.Lifecycle != AgentInstanceLifecycle.Active || current.Instance.BrowserProfileReset?.Status == "pending"
                || _audit.Any(a => a.AuditId == reset.OperationId)
                || !TenantBrowserProfileRules.Allowed(state, owner, human.PrincipalId, new IamAuthorizer(this), true, ct).GetAwaiter().GetResult()) return ValueTask.FromResult<TenantBrowserProfileCommit?>(null);
            var updated = instances.SetTenantProfileReset(owner.AgentInstanceId, reset, reset.RequestedAt);
            var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
            _tenants[owner.TenantId] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantBrowserProfileCommit?>(new(current with { Instance = updated }, next.Tenant.Revision));
        }
    }
    public ValueTask<TenantBrowserProfileCommit?> TrySettleTenantProfileResetAsync(TenantBrowserAgentOwner owner, Guid operationId,
        string status, string reasonCode, DateTimeOffset at, Guid auditId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); owner.Validate();
        if (operationId == Guid.Empty || auditId == Guid.Empty) throw new ArgumentException("Exact reset and audit IDs are required.");
        lock (_gate) lock (instances.CredentialGate)
        {
            if (_audit.Any(a => a.AuditId == auditId)) return ValueTask.FromResult<TenantBrowserProfileCommit?>(null);
            var current = FindTenantAgentAsync(owner.TenantId, owner.AgentInstanceId, ct).Result;
            if (current is null || !TenantBrowserProfileRules.Matches(owner, current) || current.Instance.BrowserProfileReset is not { } saved
                || saved.OperationId != operationId || saved.Status != "pending" || !_tenants.TryGetValue(owner.TenantId, out var state)) return ValueTask.FromResult<TenantBrowserProfileCommit?>(null);
            var reset = saved with { Status = status, ReasonCode = reasonCode, RecordedAt = at }; TenantBrowserProfileRules.Validate(reset);
            var updated = instances.SetTenantProfileReset(owner.AgentInstanceId, reset, at);
            var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
            _tenants[owner.TenantId] = next;
            _audit.Add(new(auditId, owner.TenantId, saved.PrincipalId, "browser.profile.reset.result", $"{owner.AgentInstanceId:D}:{operationId:D}", "allowed", reasonCode, at));
            return ValueTask.FromResult<TenantBrowserProfileCommit?>(new(current with { Instance = updated }, next.Tenant.Revision));
        }
    }
}
