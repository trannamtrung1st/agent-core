using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<AgentCore.Application.Ports.TenantSandboxExecutionFacts?> ReadTenantSandboxExecutionFactsAsync(Guid tenant, Guid session, Guid executionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (sandboxBacking.TenantGate)
        {
            var owner = sessionBacking.ResolveTenantSessionOwner(session);
            var execution = sandboxBacking.TenantExecution(tenant, session, executionId);
            return ValueTask.FromResult<AgentCore.Application.Ports.TenantSandboxExecutionFacts?>(owner?.TenantId == tenant && execution is not null
                && execution.AgentInstanceId == owner.AgentInstanceId && execution.ExecutorPrincipalId == owner.ExecutorPrincipalId
                ? new(owner, sandboxBacking.TenantLease(tenant, session), execution, sessionBacking.ReadRetainedTenantOperationRun(tenant, execution.AgentRunId)?.CancellationRequested == true) : null);
        }
    }
    public ValueTask<TenantOperationReceipt?> TrySettleApprovedSandboxExecutionAsync(Guid tenant, Guid session, Guid executionId,
        long expectedRevision, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (sandboxBacking.TenantGate)
        {
            var execution = sandboxBacking.TenantExecution(tenant, session, executionId);
            if (execution?.Revision != expectedRevision || execution.ApprovalRequestId is not { } requestId || execution.OperationId is not { } operation)
                return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var request = _approvals.GetValueOrDefault(requestId);
            var run = sessionBacking.ReadRetainedTenantOperationRun(tenant, execution.AgentRunId);
            if (request is null || run is null) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            TenantSandboxEffectRules.Require(request, execution, run);
            if (_operationReceipts.GetValueOrDefault(operation) is { } old) return ValueTask.FromResult<TenantOperationReceipt?>(old);
            if (TenantSandboxEffectRules.Outcome(execution) is null) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var at = ApprovalNow;
            var receipt = TenantSandboxEffectRules.Receipt(request, execution, run, at);
            var next = sessionBacking.ReadTenantRun(tenant, run.AgentRunId) is null ? run : TenantSandboxEffectRules.SettleRun(run, execution, receipt.Outcome, at);
            audit = TenantSandboxEffectRules.Audit(audit, _tenants[tenant], run, request, receipt);
            if (next.Revision != run.Revision) sessionBacking.SaveTenantOperationRun(next, run.Revision);
            _operationReceipts.Add(operation, receipt); _audit.Add(audit);
            return ValueTask.FromResult<TenantOperationReceipt?>(receipt);
        }
    }
}
