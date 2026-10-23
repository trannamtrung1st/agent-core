using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Sandbox;

public sealed partial class OpenSandboxService
{
    private static string ClassifyFailure(Exception error, string? phase) => error switch
    {
        AgentCoreException core when core.Code == "SandboxProcessesActive" => "backgroundProcessesActive",
        AgentCoreException core when core.Code == "WorkspaceQuotaExceeded" => "storageLimit",
        OperationCanceledException => "cancellation",
        TimeoutException => "commandTimeout",
        AgentCoreException core when core.Code == "MountIncompatible" => "mountFailure",
        AgentCore.Application.Security.AccessDeniedException denied when denied.ReasonCode == "sandbox_generation_unavailable" => "provisioningFailure",
        AgentCore.Application.Security.AccessDeniedException => "sourceAuthorizationChanged",
        AgentCore.Application.Security.SecurityConflictException conflict when conflict.ReasonCode is "sandbox_operation_witness_changed" or "operation_witness_changed" or "stale_revision" or "resource_changed" => "sourceAuthorizationChanged",
        AgentCoreException core when core.Code == "Forbidden" => "sourceAuthorizationChanged",
        _ when phase == "provisioning" => "provisioningFailure",
        _ when phase == "processInspection" => "processInspectionFailure",
        _ => "providerTransportFailure"
    };

    private async ValueTask SettleTerminatedExecutionAsync(SandboxLease lease, SandboxExecution execution, CancellationToken ct)
    {
        var settled = execution with { TerminationConfirmed = true, RemoteTerminationConfirmed = true, Phase = "completed",
            State = execution.State is SandboxExecutionState.Dispatching or SandboxExecutionState.Running ? SandboxExecutionState.Indeterminate : execution.State,
            FinishedAt = execution.FinishedAt ?? time.GetUtcNow() };
        settled = await SaveReceiptAsync(settled, false, ct);
        await SaveAsync(lease with { State = SandboxLeaseState.Terminated, ActiveExecutionId = null }, ct);
    }
}
