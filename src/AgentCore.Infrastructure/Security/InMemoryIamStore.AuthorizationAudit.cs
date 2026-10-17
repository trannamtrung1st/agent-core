using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantAuthorizationAuditStore
{
    public ValueTask<bool> TryRecordOperationAuthorizationAsync(AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); TenantOperationAuditRules.Validate(audit); var e = audit.Execution!;
        lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            if (!_tenants.TryGetValue(e.Context.TenantId, out var state)
                || sessionBacking.ReadTenantRun(e.Context.TenantId, e.Context.RunId) is not { } run
                || !TenantOperationAuditRules.Matches(audit, run, state.Tenant.Revision)) return ValueTask.FromResult(false);
            if (_audit.SingleOrDefault(a => a.AuditId == audit.AuditId) is { } existing) return ValueTask.FromResult(existing == audit);
            ct.ThrowIfCancellationRequested(); _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
}
