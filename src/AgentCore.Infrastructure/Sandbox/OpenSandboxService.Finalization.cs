using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using OpenSandbox.Core;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private static bool HasPendingFinalization(SandboxExecution execution) => execution.ExitCode is not null
        && execution.Phase is "processInspection" or "retirementRequested" or "retired";

    private async ValueTask ReconcileFinalizationAsync(SandboxLease lease, SandboxExecution execution, CancellationToken ct)
    {
        // Recovery settles process ownership only. Canonical filesystem effects
        // already happened and are never imported, rolled back or replayed.
        if (await TryTerminateRemoteAsync(lease, ct))
        {
            execution = execution with { GenerationRecycled = true };
            await SettleTerminatedExecutionAsync(lease, execution, ct);
        }
        else await SaveAsync(lease with { State = SandboxLeaseState.NeedsReconciliation }, ct);
    }

    private async ValueTask<(SandboxLease Lease, SandboxExecution Execution)> FinalizeCompletedExecutionAsync(
        SdkSandbox? sdk, SandboxLease lease, SandboxExecution execution, AgentDefinition definition, Func<CancellationToken, ValueTask> fence, CancellationToken ct)
    {
        await fence(ct);
        if (!execution.RemoteTerminationConfirmed && !await CompletedGenerationIsQuiescentAsync(sdk, lease, ct))
        {
            execution = execution with { Phase = "retirementRequested", GenerationRecycled = true };
            execution = await SaveReceiptAsync(execution, false, ct);
            lease = await SaveAsync(lease with { State = SandboxLeaseState.Terminating, CleanupReason = "executionRecycle" }, ct);
            FinalizationCheckpoint?.Invoke("retirementRequested");
            if (!await TryTerminateRemoteAsync(lease, ct))
            {
                lease = await SaveAsync(lease with { State = SandboxLeaseState.NeedsReconciliation,
                    SafeMessage = "Command completion is recorded. Container retirement is pending. Canonical files already contain any writes; inspect cleanup before execution." }, CancellationToken.None);
                return (lease, execution);
            }
            FinalizationCheckpoint?.Invoke("remoteTerminated");
            execution = execution with { RemoteTerminationConfirmed = true, TerminationConfirmed = true, Phase = "completed" };
            execution = await SaveReceiptAsync(execution, false, CancellationToken.None);
            lease = await SaveAsync(lease with { State = SandboxLeaseState.Terminated, ActiveExecutionId = null }, CancellationToken.None);
            return (lease, execution);
        }
        await fence(ct);
        execution = execution with { Phase = "completed", TerminationConfirmed = true };
        execution = await SaveReceiptAsync(execution, false, ct);
        if (sdk is null) throw AgentCoreErrors.Conflict("Live generation ownership could not be confirmed.");
        lease = await SaveAsync(lease with { State = SandboxLeaseState.Ready, ActiveExecutionId = null,
            LastActivityAt = time.GetUtcNow(), ExpiresAt = await RenewIdleAsync(sdk, lease, ct) }, ct);
        return (lease, execution);
    }

    private static SandboxCommandResult FinalizedResult(SandboxLease lease, SandboxExecution execution)
        => lease.State is not (SandboxLeaseState.NeedsReconciliation or SandboxLeaseState.Terminating) ? Result(execution)
            : Result(execution) with { EnvironmentState = lease.State.ToString(), EnvironmentGeneration = lease.Generation,
                Error = "recovery_required", Recovery = "Process cleanup requires recovery. Canonical files retain any writes. Inspect durable evidence; do not replay the command or switch profiles." };

    private async ValueTask<SandboxCommandResult> UnsettledFailureResultAsync(SandboxLease lease, SandboxExecution fallback)
    {
        SandboxExecution execution;
        try { execution = await ReadReceiptAsync(lease, fallback.Id, CancellationToken.None) ?? fallback; }
        catch { execution = fallback; }
        return Result(execution) with { Error = "recovery_required", EnvironmentState = "NeedsReconciliation", EnvironmentGeneration = lease.Generation,
            Recovery = "Receipt persistence or process cleanup was interrupted. Inspect durable evidence and current files; never replay an uncertain command." };
    }

    private async ValueTask<(SandboxLease Lease, SandboxExecution Execution)> AbortExecutionFinalizationAsync(
        SandboxLease lease, SandboxExecution execution, Exception error)
    {
        execution = await ReadReceiptAsync(lease, execution.Id, CancellationToken.None) ?? execution;
        lease = await ReadLeaseAsync(lease, CancellationToken.None) ?? lease;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var remote = execution.RemoteTerminationConfirmed || await TryTerminateRemoteAsync(lease, cleanup.Token);
        var runCancelled = lease.TenantId is { } tenant
            ? (await TenantRuntime.ReadTenantSandboxExecutionFactsAsync(tenant, lease.SessionId, execution.Id, CancellationToken.None))?.RunCancellationRequested == true
            : await RunCancellationRequestedAsync(lease.SessionId, lease.AgentInstanceId, execution.AgentRunId, CancellationToken.None);
        var cancelled = error is OperationCanceledException || runCancelled;
        execution = execution with {
            State = execution.ExitCode is not null && execution.State is SandboxExecutionState.Completed or SandboxExecutionState.Canceled ? execution.State
                : cancelled && remote ? SandboxExecutionState.Canceled : SandboxExecutionState.Indeterminate,
            FinishedAt = execution.FinishedAt ?? time.GetUtcNow(),
            FailureCategory = cancelled ? "cancellation" : ClassifyFailure(error, execution.Phase),
            CancellationReason = cancelled ? runCancelled ? "runCancellation" : "requestCancellation" : execution.CancellationReason,
            DiagnosticId = execution.DiagnosticId ?? Guid.CreateVersion7().ToString("D"), RemoteTerminationConfirmed = remote, TerminationConfirmed = remote };
        execution = await SaveReceiptAsync(execution, false, CancellationToken.None);
        lease = await SaveAsync(lease with { State = remote ? SandboxLeaseState.Terminated : SandboxLeaseState.NeedsReconciliation,
            ActiveExecutionId = remote ? null : execution.Id, DiagnosticId = execution.DiagnosticId,
            CleanupReason = cancelled ? "cancelled" : "executionFailed", SafeMessage = remote
                ? "Container termination confirmed. Canonical files retain any complete or partial writes. The command is never replayed."
                : "Container termination remains unconfirmed. Canonical writes may still occur. Inspect durable evidence before further execution." }, CancellationToken.None);
        return (lease, execution);
    }
}
