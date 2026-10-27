using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public interface IEvaluationSandboxExecutor
{
    string EvaluationHostScope { get; }
    bool EvaluationEnabled { get; }
    ValueTask ExecuteEvaluationAsync(Guid tenant, Guid executionId, Guid dispatchClaim, CancellationToken ct = default);
    ValueTask<bool> CleanupEvaluationAsync(EvaluationSandboxExecution execution, CancellationToken ct = default);
}

public partial interface ITenantEvaluationStore
{
    ValueTask<EvaluationSandboxExecution?> FindSandboxEvaluationAsync(Guid tenant, Guid executionId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<EvaluationSandboxExecution>> ListSandboxEvaluationsAsync(int limit, DateTimeOffset now, CancellationToken ct = default);
    ValueTask<bool> TryReserveSandboxEvaluationAsync(TenantSecurityState next, long securityRevision, EvaluationSandboxExecution value,
        long attemptRevision, AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default);
    ValueTask<bool> TrySaveSandboxEvaluationAsync(EvaluationSandboxExecution value, long expectedRevision, CancellationToken ct = default,
        long? expectedSecurityRevision = null);
    ValueTask<bool> TryRecordSandboxSettlementUnavailableAsync(Guid tenant, Guid executionId, CancellationToken ct = default);
}
