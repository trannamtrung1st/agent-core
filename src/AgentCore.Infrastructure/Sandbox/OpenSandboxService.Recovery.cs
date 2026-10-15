using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private async ValueTask<SandboxCommandResult> RecoveryRequiredAsync(SandboxLease lease, CancellationToken ct)
    {
        var executionId = lease.ActiveExecutionId ?? lease.LastExecutionId;
        var evidence = executionId is { } id ? await store.GetExecutionAsync(lease.SessionId, id, ct) : null;
        var result = evidence is null ? new SandboxCommandResult(Guid.Empty, lease.State.ToString(), null, "", "", false, 0, false) : Result(evidence);
        return result with { Stdout = "", Stderr = "", Error = "recovery_required", EnvironmentState = lease.State.ToString(), EnvironmentGeneration = lease.Generation,
            Phase = lease.FailurePhase ?? lease.TransitionPhase ?? result.Phase, FailureCategory = lease.FailureCategory ?? result.FailureCategory,
            Recovery = lease.State is SandboxLeaseState.Terminating or SandboxLeaseState.NeedsReconciliation
                ? "Remote termination is pending; canonical writes may still occur. Inspect durable status and wait for confirmed cleanup. Do not retry execution or switch profiles. Owner reset may be required after readiness is restored."
                : lease.ActiveExecutionId is not null && lease.State == SandboxLeaseState.Executing
                    ? "An execution is active. Inspect its Core execution ID; stop background work only when authorized. Do not switch or dispatch another command."
                    : "The generation is blocked. Inspect canonical /home and /working and durable evidence, then ask the owner to explicitly reset. Do not retry execution or switch profiles. Historical package claims do not establish current image availability." };
    }

    private static string ClassifyFailure(Exception error, string? phase) => error switch
    {
        AgentCoreException core when core.Code == "SandboxProcessesActive" => "backgroundProcessesActive",
        AgentCoreException core when core.Code == "WorkspaceQuotaExceeded" => "storageLimit",
        OperationCanceledException => "cancellation",
        TimeoutException => "commandTimeout",
        AgentCoreException core when core.Code == "Forbidden" => "sourceAuthorizationChanged",
        _ when phase == "processInspection" => "processInspectionFailure",
        _ => "providerTransportFailure"
    };

    private async ValueTask<bool> RunCancellationRequestedAsync(Guid sessionId, Guid instanceId, Guid runId, CancellationToken ct)
    {
        try
        {
            var metadata = await sessions.LoadMetadataAsync(sessionId, ct);
            return metadata?.ProfileId is { } profileId && await runs.GetAsync(new(instanceId, profileId), runId, ct) is { CancellationRequested: true };
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Missing cancellation evidence must not replace the original failure or
            // prevent persisting its receipt. Never infer cancellation from an outage.
            return false;
        }
    }

    private async ValueTask SettleTerminatedExecutionAsync(SandboxLease lease, SandboxExecution execution, CancellationToken ct)
    {
        var settled = execution with { TerminationConfirmed = true, RemoteTerminationConfirmed = true, Phase = "completed",
            State = execution.State is SandboxExecutionState.Dispatching or SandboxExecutionState.Running ? SandboxExecutionState.Indeterminate : execution.State,
            FinishedAt = execution.FinishedAt ?? time.GetUtcNow() };
        await SaveReceiptAsync(settled, false, ct);
        await SaveAsync(lease with { State = SandboxLeaseState.Terminated, ActiveExecutionId = null }, ct);
    }
}
