using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using OpenSandbox;
using OpenSandbox.Core;
using SdkSandbox = OpenSandbox.Sandbox;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private static bool HasPendingFinalization(SandboxExecution execution) => execution.ExitCode is not null
        && execution.State is SandboxExecutionState.Completed or SandboxExecutionState.Canceled
        && (execution.FailureCategory is null || execution.State == SandboxExecutionState.Canceled && execution.CancellationReason == "processStop" && execution.FailureCategory == "cancellation")
        && execution.Phase is "processInspection" or "retirementRequested" or "retired" or "fileCollection" or "workspaceCommit" or "stagingCleanup";

    private async ValueTask ReconcileFinalizationAsync(SandboxLease lease, SandboxExecution execution, CancellationToken ct)
    {
        if (lease.CleanupReason is "reset" or "sessionDeleted" or "sessionArchived" or "sessionEnded" or "sessionDeactivated" or "sessionLifecycle")
        {
            // The lease discard intent may have persisted just before a crash while
            // writing the receipt. It still fences any later automatic import.
            execution = execution with { FailureCategory = execution.FailureCategory ?? "cancellation",
                CancellationReason = lease.CleanupReason == "reset" ? "ownerReset" : "sessionLifecycle" };
            await SaveReceiptAsync(execution, false, ct);
            if (await TryTerminateRemoteAsync(lease, ct)) await SettleTerminatedExecutionAsync(lease, execution, ct);
            return;
        }
        if (execution.WorkspaceSyncStatus is "committing" or "committed" or "unknown")
        {
            // A journaled import is resolved, never reissued, regardless of Run status.
            await SettleRecycledExecutionAsync(lease, execution with { GenerationRecycled = execution.GenerationRecycled || execution.State == SandboxExecutionState.Completed }, ct);
            return;
        }
        SdkSandbox? sdk = null;
        try
        {
            var session = await sessions.LoadAsync(lease.SessionId, ct) ?? throw AgentCoreErrors.NotFound("Finalization owner is unavailable.");
            var run = session.ProfileId is { } profile ? await runs.GetAsync(new(lease.AgentInstanceId, profile), execution.AgentRunId, ct) : null;
            var definition = run?.Admission.Configuration?.Definition ?? session.Definition;
            var access = new SandboxAccess(lease.SessionId, lease.AgentInstanceId, execution.AgentRunId, execution.ClaimGeneration,
                execution.ToolCallId, lease.Profile, definition);
            ValueTask Fence(CancellationToken token) => execution.Background ? ValidateProcessCommitAsync(lease, execution, token) : ValidateAccessAsync(access, token);
            if (execution.State == SandboxExecutionState.Canceled && execution.CancellationReason != "processStop") throw new OperationCanceledException();
            await Fence(ct);
            if (!execution.RemoteTerminationConfirmed && execution.Phase == "processInspection")
            {
                try { sdk = await ConnectAsync(lease, ct); }
                catch (SandboxApiException error) when (error.StatusCode == 404) { /* Confirm absence through exact-owned retirement before reading staging. */ }
            }
            await FinalizeCompletedExecutionAsync(sdk, lease, execution, definition, Fence, ct);
        }
        catch (Exception error) { await AbortExecutionFinalizationAsync(lease, execution, error); }
        finally { if (sdk is not null) await sdk.DisposeAsync(); }
    }

    private async ValueTask<(SandboxLease Lease, SandboxExecution Execution)> FinalizeCompletedExecutionAsync(
        SdkSandbox? sdk, SandboxLease lease, SandboxExecution execution, AgentDefinition definition, Func<CancellationToken, ValueTask> fence, CancellationToken ct)
    {
        await fence(ct);
        if (!execution.RemoteTerminationConfirmed && (execution.Phase == "retirementRequested" || sdk is null || !await IsQuiescentAsync(sdk, lease, ct)))
        {
            execution = execution with { Phase = "retirementRequested" };
            await SaveReceiptAsync(execution, false, ct);
            lease = await SaveAsync(lease with { State = SandboxLeaseState.Terminating, CleanupReason = "executionRecycle" }, ct);
            FinalizationCheckpoint?.Invoke("retirementRequested");
            if (!await TryTerminateRemoteAsync(lease, ct))
            {
                lease = await SaveAsync(lease with { State = SandboxLeaseState.NeedsReconciliation,
                    SafeMessage = "Command completion is recorded. Container retirement is pending; files have not been imported. Inspect cleanup before further execution." }, CancellationToken.None);
                return (lease, execution);
            }
            FinalizationCheckpoint?.Invoke("remoteTerminated");
            execution = execution with { RemoteTerminationConfirmed = true, GenerationRecycled = true, Phase = "retired" };
            await SaveReceiptAsync(execution, false, CancellationToken.None);
            FinalizationCheckpoint?.Invoke("retired");
        }
        await fence(ct);
        execution = execution with { Phase = "fileCollection" };
        await SaveReceiptAsync(execution, false, ct);
        var transfer = await CollectAsync(lease, ct);
        FinalizationCheckpoint?.Invoke("collected");
        await fence(ct);
        execution = execution with { WorkspaceSyncStatus = "committing", Phase = "workspaceCommit" };
        await SaveReceiptAsync(execution, false, ct);
        await workspace.CommitScratchAsync(lease.SessionId, transfer, lease.ScratchFingerprint!, ct, fence, execution.Id);
        FinalizationCheckpoint?.Invoke("canonicalCommitted");
        execution = execution with { WorkspaceSyncStatus = "committed", Phase = execution.GenerationRecycled ? "stagingCleanup" : "completed" };
        await SaveReceiptAsync(execution, false, CancellationToken.None);
        if (execution.GenerationRecycled)
        {
            FinalizationCheckpoint?.Invoke("stagingCleanup");
            return await SettleRecycledExecutionAsync(lease, execution, CancellationToken.None);
        }
        if (sdk is null) throw AgentCoreErrors.Conflict("Live generation ownership could not be confirmed.");
        lease = await SaveAsync(lease with { State = SandboxLeaseState.Ready, ActiveExecutionId = null,
            LastActivityAt = time.GetUtcNow(), ExpiresAt = await RenewIdleAsync(sdk, lease, ct),
            ScratchFingerprint = (await workspace.CaptureScratchAsync(lease.SessionId, definition, ct)).Fingerprint }, ct);
        return (lease, execution);
    }

    private async ValueTask<(SandboxLease Lease, SandboxExecution Execution)> SettleRecycledExecutionAsync(SandboxLease lease, SandboxExecution execution, CancellationToken ct)
    {
        var sync = await CommitStatusAsync(execution, ct);
        execution = execution with { WorkspaceSyncStatus = sync };
        if (!execution.RemoteTerminationConfirmed && !await TryTerminateRemoteAsync(lease, ct))
        {
            lease = await SaveAsync(lease with { State = SandboxLeaseState.NeedsReconciliation }, ct);
            return (lease, execution);
        }
        execution = execution with { RemoteTerminationConfirmed = true };
        var resolved = sync is "committed" or "notCommitted";
        var deleted = resolved && TryDeleteStaging(lease);
        if (deleted) FinalizationCheckpoint?.Invoke("stagingDeleted");
        execution = execution with { TerminationConfirmed = deleted, Phase = deleted ? "completed" : resolved ? "stagingCleanup" : execution.Phase };
        await SaveReceiptAsync(execution, false, ct);
        lease = await SaveAsync(lease with { State = deleted ? SandboxLeaseState.Terminated : resolved ? SandboxLeaseState.NeedsReconciliation : SandboxLeaseState.NeedsResync,
            ActiveExecutionId = deleted ? null : execution.Id }, ct);
        return (lease, execution);
    }

    private static SandboxCommandResult FinalizedResult(SandboxLease lease, SandboxExecution execution)
        => lease.State is not (SandboxLeaseState.NeedsReconciliation or SandboxLeaseState.Terminating or SandboxLeaseState.NeedsResync) ? Result(execution)
            : Result(execution) with { EnvironmentState = lease.State.ToString(), EnvironmentGeneration = lease.Generation,
            Error = lease.State is SandboxLeaseState.NeedsReconciliation or SandboxLeaseState.Terminating or SandboxLeaseState.NeedsResync ? "recovery_required" : null,
            Recovery = lease.State is SandboxLeaseState.NeedsReconciliation or SandboxLeaseState.Terminating or SandboxLeaseState.NeedsResync
                ? "Command completion is recorded; file commit or cleanup still requires recovery. Inspect durable evidence and do not replay the command or switch profiles." : null };

    private async ValueTask<SandboxCommandResult> UnsettledFailureResultAsync(SandboxLease lease, SandboxExecution fallback)
    {
        var execution = fallback;
        try
        {
            execution = await store.GetExecutionAsync(lease.SessionId, fallback.Id, CancellationToken.None) ?? fallback;
            execution = execution with { WorkspaceSyncStatus = await CommitStatusAsync(execution, CancellationToken.None) };
        }
        catch { execution = execution with { WorkspaceSyncStatus = "unknown" }; }
        return Result(execution) with { Error = "recovery_required", EnvironmentState = "NeedsReconciliation", EnvironmentGeneration = lease.Generation,
            Recovery = "Finalization persistence or cleanup was interrupted. Inspect durable evidence and canonical files; no command may be replayed or another profile selected." };
    }

    private async ValueTask<(SandboxLease Lease, SandboxExecution Execution)> AbortExecutionFinalizationAsync(
        SandboxLease lease, SandboxExecution execution, Exception error)
    {
        execution = await store.GetExecutionAsync(lease.SessionId, execution.Id, CancellationToken.None) ?? execution;
        lease = await store.GetLeaseAsync(lease.SessionId, CancellationToken.None) ?? lease;
        var sync = await CommitStatusAsync(execution, CancellationToken.None);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var remote = execution.RemoteTerminationConfirmed || await TryTerminateRemoteAsync(lease, cleanup.Token);
        var deleted = remote && sync is "committed" or "notCommitted" && TryDeleteStaging(lease);
        var runCancelled = await RunCancellationRequestedAsync(lease.SessionId, lease.AgentInstanceId, execution.AgentRunId, CancellationToken.None);
        var cancelled = error is OperationCanceledException || runCancelled;
        execution = execution with {
            State = cancelled && remote ? SandboxExecutionState.Canceled : execution.ExitCode is not null && execution.State is SandboxExecutionState.Completed or SandboxExecutionState.Canceled ? execution.State : SandboxExecutionState.Indeterminate,
            FinishedAt = execution.FinishedAt ?? time.GetUtcNow(), WorkspaceSyncStatus = sync,
            FailureCategory = cancelled ? "cancellation" : ClassifyFailure(error, execution.Phase),
            CancellationReason = cancelled ? runCancelled ? "runCancellation" : "requestCancellation" : execution.CancellationReason,
            DiagnosticId = execution.DiagnosticId ?? Guid.CreateVersion7().ToString("D"), RemoteTerminationConfirmed = remote, TerminationConfirmed = deleted };
        await SaveReceiptAsync(execution, false, CancellationToken.None);
        lease = await SaveAsync(lease with { State = deleted ? SandboxLeaseState.Terminated : remote && sync == "unknown" ? SandboxLeaseState.NeedsResync : SandboxLeaseState.NeedsReconciliation,
            ActiveExecutionId = deleted ? null : execution.Id, DiagnosticId = execution.DiagnosticId,
            CleanupReason = cancelled ? "cancelled" : "executionFailed", SafeMessage = deleted
                ? "Termination and staging cleanup confirmed. The original command outcome and file status are retained; commands are never replayed."
                : "Termination, file synchronization or staging cleanup remains unresolved. Inspect durable evidence before further execution." }, CancellationToken.None);
        return (lease, execution);
    }
}
