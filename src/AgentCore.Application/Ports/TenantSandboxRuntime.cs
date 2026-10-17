using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

// Trusted Core references only. Reservation is durable ownership, never permission to dispatch.
public sealed record TenantSandboxRuntimeWrite(TenantRuntimeFrame Runtime, long SecurityRevision, long RunRevision);
public sealed record TenantSandboxRuntimeSnapshot(TenantSessionRoot Owner, long SessionRevision, SandboxLease? Lease, SandboxExecution? Execution);

public interface ITenantSandboxRuntimeStore
{
    ValueTask<TenantSandboxRuntimeSnapshot?> ReadTenantSandboxRuntimeAsync(Guid tenant, Guid session, Guid? executionId = null, CancellationToken ct = default);
    ValueTask<SandboxExecution?> FindTenantSandboxOperationAsync(Guid tenant, Guid session, Guid run, string toolCallId, CancellationToken ct = default);
    ValueTask<bool> TryReserveTenantSandboxLeaseAsync(TenantSandboxRuntimeWrite write, SandboxLease lease, long expectedRevision, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<bool> TryReserveTenantSandboxExecutionAsync(TenantSandboxRuntimeWrite write, SandboxExecution execution, DateTimeOffset at, CancellationToken ct = default);
    // Factual settlement keeps its exact generation/receipt even after the initiating Run retires.
    ValueTask<bool> TrySaveTenantSandboxLeaseFactsAsync(Guid tenant, SandboxLease lease, long expectedRevision, CancellationToken ct = default);
    ValueTask<bool> TrySaveTenantSandboxExecutionFactsAsync(Guid tenant, SandboxExecution execution, long expectedRevision, CancellationToken ct = default);
}
