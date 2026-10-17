using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantSandboxCommandStartRules
{
    public static (SandboxLease Lease, SandboxExecution Execution) Start(TenantSandboxRuntimeWrite write, SandboxLease lease,
        SandboxExecution execution, TenantApprovalRequest request, TenantApprovalDecision decision, AgentRun run, AgentRun? source, DateTimeOffset at)
    {
        TenantRunContinuationRules.Require(run, request, source);
        TenantSandboxEffectRules.Require(request, execution, run);
        if (!execution.InitializesGeneration || execution.CommandStartedAt is not null || execution.DeadlineAt is not null
            || execution.State != SandboxExecutionState.Dispatching || execution.Phase != "provisioning" || execution.ProviderCommandId is not null
            || lease.State != SandboxLeaseState.Provisioning || lease.Generation != execution.Generation || lease.ActiveExecutionId != execution.Id
            || lease.Initialization is not { State: "Succeeded", ExitCode: 0, FinishedAt: not null }
            || lease.MountAuthority is null || lease.InitializedAt is null || lease.InitializedAt < lease.CreatedAt || lease.InitializedAt > at
            || string.IsNullOrWhiteSpace(lease.ProviderId) || request.ExpiresAt <= at
            || run.Claim?.Generation != write.Runtime.Generation || execution.ClaimGeneration != write.Runtime.Generation
            || run.SideEffect.Disposition != AgentRunSideEffectDisposition.InFlight || run.SideEffect.ToolCallId != execution.ToolCallId || run.SideEffect.ActionHash != execution.ActionHash
            || execution.InitializesGeneration != (request.Snapshot?.Sandbox?.WillInitialize == true)
            || execution.ToolCallId != "operation:" + request.Operation.OperationId.ToString("D")
            || decision.RequestId != request.RequestId || decision.TenantId != request.TenantId || decision.Status != TenantApprovalStatus.Approved || decision.RequestRevision != request.Revision || decision.ActionHash != execution.ActionHash)
            throw new SecurityConflictException("sandbox_command_start_mismatch");
        var command = TenantApprovalRules.SandboxCommand(request.Operation.SandboxCommand!);
        var deadline = at.AddSeconds(command.TimeoutSeconds!.Value);
        if (deadline > lease.ExpiresAt || deadline.AddSeconds(20) > lease.CreatedAt.AddSeconds(lease.Profile.MaxLifetimeSeconds))
            throw new AccessDeniedException("sandbox_lifetime_insufficient");
        return (lease with { Revision = checked(lease.Revision + 1), State = SandboxLeaseState.Executing, TransitionPhase = null, LastActivityAt = at },
            execution with { Revision = checked(execution.Revision + 1), CommandStartedAt = at, DeadlineAt = deadline, Phase = "dispatch", FilesystemEffectsMayHaveOccurred = true });
    }
}
