using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Ports;

// Trusted Core references only. Reservation is durable ownership, never permission to dispatch.
public sealed record TenantSandboxRuntimeWrite(TenantRuntimeFrame Runtime, long SecurityRevision, long RunRevision);
public sealed record TenantSandboxApprovalReservation(TenantSandboxRuntimeWrite Write, Guid RequestId, long RequestRevision,
    Guid DecisionId, string ActionHash, AgentCore.Domain.Security.AuthorizationAudit Audit, SandboxLease? NewGeneration = null);
public sealed record TenantSandboxRuntimeSnapshot(TenantSessionRoot Owner, long SessionRevision, SandboxLease? Lease, SandboxExecution? Execution);
public sealed record TenantSandboxExecutionFacts(TenantSessionRoot Owner, SandboxLease? Lease, SandboxExecution Execution, bool RunCancellationRequested);

public sealed record TenantSandboxRecoveryPage(IReadOnlyList<SandboxLease> Items, string? NextCursor);

public interface ITenantSandboxRuntimeStore
{
    // Bounded technical inventory over retained owned roots, including terminal result settlement. Never a human inventory or dispatch authority.
    ValueTask<TenantSandboxRecoveryPage> ReadTenantSandboxRecoveryPageAsync(string? afterSession, int limit, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<TenantSandboxRuntimeSnapshot?> ReadTenantSandboxRuntimeAsync(Guid tenant, Guid session, Guid? executionId = null, CancellationToken ct = default);
    ValueTask<SandboxExecution?> FindTenantSandboxOperationAsync(Guid tenant, Guid session, Guid run, string toolCallId, CancellationToken ct = default);
    // Exact technical evidence remains readable after logical Session retirement; this grants no new effect or human read.
    ValueTask<TenantSandboxExecutionFacts?> ReadTenantSandboxExecutionFactsAsync(Guid tenant, Guid session, Guid execution, CancellationToken ct = default) => throw new NotSupportedException();
    ValueTask<bool> TryReserveTenantSandboxLeaseAsync(TenantSandboxRuntimeWrite write, SandboxLease lease, long expectedRevision, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<bool> TryReserveTenantSandboxExecutionAsync(TenantSandboxRuntimeWrite write, SandboxExecution execution, DateTimeOffset at, CancellationToken ct = default);
    ValueTask<bool> TryReserveApprovedSandboxExecutionAsync(TenantSandboxApprovalReservation review, SandboxExecution execution,
        DateTimeOffset at, CancellationToken ct = default) => throw new NotSupportedException("Approved Sandbox reservations require owned persistence.");
    ValueTask<bool> TryStartApprovedSandboxCommandAsync(TenantSandboxRuntimeWrite write, Guid execution, long expectedExecutionRevision,
        long expectedLeaseRevision, DateTimeOffset at, CancellationToken ct = default) => throw new NotSupportedException();
    // Factual settlement keeps its exact generation/receipt even after the initiating Run retires.
    ValueTask<bool> TrySaveTenantSandboxLeaseFactsAsync(Guid tenant, SandboxLease lease, long expectedRevision, CancellationToken ct = default);
    ValueTask<bool> TrySaveTenantSandboxExecutionFactsAsync(Guid tenant, SandboxExecution execution, long expectedRevision, CancellationToken ct = default);
    ValueTask<AgentCore.Domain.Security.TenantOperationReceipt?> TrySettleApprovedSandboxExecutionAsync(Guid tenant, Guid session, Guid execution,
        long expectedRevision, AgentCore.Domain.Security.AuthorizationAudit audit, CancellationToken ct = default) => throw new NotSupportedException();
}

// Trusted worker entry; arguments and ownership are loaded from the saved exact review.
public interface ITenantSandboxCommandExecutor
{
    ValueTask<SandboxCommandResult> ExecuteApprovedAsync(TenantRuntimeFrame frame, Guid requestId, CancellationToken ct = default);
}

// Prepares only the approved operation's actual owned directories and admitted immutable template bytes.
public interface ITenantSandboxWorkspacePreparer
{
    ValueTask PrepareAsync(TenantRuntimeFrame frame, Guid executionId, CancellationToken ct = default);
}
