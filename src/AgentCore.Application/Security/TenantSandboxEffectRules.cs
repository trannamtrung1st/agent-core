using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantSandboxEffectRules
{
    public static (TenantOperationOutcome Outcome, string Reason)? Outcome(SandboxExecution execution) => execution switch
    {
        { Phase: "notDispatched", FilesystemEffectsMayHaveOccurred: false, TerminationConfirmed: true } => (TenantOperationOutcome.Denied, "sandbox_command_not_dispatched"),
        { State: SandboxExecutionState.Completed, TerminationConfirmed: true, ExitCode: >= 0 and <= 255 } => (TenantOperationOutcome.Completed, "sandbox_command_completed"),
        { State: SandboxExecutionState.Running, Background: true, ProviderCommandId: not null, Phase: "execution" } => (TenantOperationOutcome.Completed, "sandbox_command_started"),
        { State: SandboxExecutionState.Completed or SandboxExecutionState.Canceled or SandboxExecutionState.Indeterminate, FinishedAt: not null } => (TenantOperationOutcome.Indeterminate, "sandbox_command_uncertain"),
        _ => null
    };
    public static void Require(TenantApprovalRequest request, SandboxExecution execution, AgentRun run)
    {
        if (request.Operation.SandboxCommand is null || request.Status != TenantApprovalStatus.Approved || request.Kind != ApprovalKind.Review
            || execution.ApprovalRequestId != request.RequestId || execution.OperationId != request.Operation.OperationId
            || execution.ActionHash != request.Operation.ActionHash || execution.TenantId != request.TenantId || execution.ExecutorPrincipalId != request.ExecutorPrincipalId
            || execution.SessionId != request.SessionId || execution.AgentInstanceId != request.Operation.InstanceId || execution.AgentRunId != run.AgentRunId
            || run.Owner.TenantId != request.TenantId || run.Owner.ExecutorPrincipalId != request.ExecutorPrincipalId || run.SessionId != execution.SessionId
            || run.ApprovalRequestId != request.RequestId || run.Admission.ExecutionContext?.RootActivationId != request.RootActivationId)
            throw new SecurityConflictException("sandbox_approval_mismatch");
    }
    public static TenantOperationReceipt Receipt(TenantApprovalRequest request, SandboxExecution execution, AgentRun run, DateTimeOffset at)
    {
        Require(request, execution, run);
        var (outcome, reason) = Outcome(execution) ?? throw new SecurityConflictException("sandbox_execution_in_progress");
        return new(request.Operation.OperationId, request.RequestId, request.TenantId, run.AgentRunId, run.SessionId, request.RootActivationId,
            request.ExecutorPrincipalId, request.Operation.InstanceId, request.Operation.ActionHash, outcome, reason,
            request.Operation.ExpectedInstanceRevision, request.Operation.ExpectedPersonaRevision, at);
    }
    public static AgentRun SettleRun(AgentRun run, SandboxExecution execution, TenantOperationOutcome outcome, DateTimeOffset at)
    {
        if (run.Status != AgentRunStatus.Running || run.CancellationRequested || run.Claim?.Generation != execution.ClaimGeneration
            || run.SideEffect.Disposition != AgentRunSideEffectDisposition.InFlight || run.SideEffect.ToolCallId != execution.ToolCallId
            || run.SideEffect.ActionHash != execution.ActionHash) return run;
        return run.MarkSideEffect(run.Revision, execution.ClaimGeneration, outcome switch {
            TenantOperationOutcome.Completed => AgentRunSideEffectDisposition.Succeeded,
            TenantOperationOutcome.Denied => AgentRunSideEffectDisposition.DefinitelyFailed,
            _ => AgentRunSideEffectDisposition.Indeterminate }, execution.ToolCallId, execution.ActionHash, at);
    }
    public static AuthorizationAudit Audit(AuthorizationAudit audit, TenantSecurityState state, AgentRun run, TenantApprovalRequest request, TenantOperationReceipt receipt)
    {
        if (audit.TenantId != request.TenantId || audit.ActorPrincipalId != request.ExecutorPrincipalId || audit.Action != "sandbox.command.result"
            || audit.Target != receipt.OperationId.ToString("D")) throw new ArgumentException("Invalid Sandbox effect audit.");
        return TenantOperationAuditRules.WithEffectResult(audit with { At = receipt.RecordedAt, Decision = receipt.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = receipt.ReasonCode }, state, run, request, null);
    }
}
