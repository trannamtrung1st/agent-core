using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantSandboxApprovalRules
{
    // Actual owning Session/Instance roots have already been resolved by the transactional caller.
    public static async ValueTask RequireHumanAsync(TenantSecurityState state, TenantApprovalRequest request, TenantApprovalDecision decision,
        IamAuthorizer authorizer, CancellationToken ct)
    {
        var policy = TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(request.Permission));
        if (policy.IndependentReview && request.OriginalInitiatorPrincipalId == decision.HumanPrincipalId)
            throw new AccessDeniedException("independent_reviewer_required");
        var target = AccessScope.ForResource(request.TenantId, "agent", request.Operation.InstanceId.ToString("D"));
        foreach (var permission in new[] { "sandbox.execute", "approvals.approve" })
            if (!(await authorizer.CheckOwnedTenantAsync(state, decision.HumanPrincipalId, PermissionCatalog.Get(permission), target, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("grantor_authority_lost");
        if (request.OriginalInitiatorPrincipalId is { } requester)
            foreach (var pair in new[] { ("agents.chat", target), ("sessions.read", TenantSessionRules.Scope(request.TenantId, request.SessionId)) })
                if (!(await authorizer.CheckOwnedTenantAsync(state, requester, PermissionCatalog.Get(pair.Item1), pair.Item2, ct).ConfigureAwait(false)).Allowed)
                    throw new AccessDeniedException("requester_unavailable");
    }
    public static void RequireApproved(TenantSandboxApprovalReservation review, TenantApprovalRequest request, TenantApprovalDecision decision,
        AgentRun run, AgentRun? source, SandboxExecution execution, DateTimeOffset at)
    {
        TenantApprovalContinuationRules.Require(request, run, source);
        if (request.Operation.SandboxCommand is not { } command || request.Kind != ApprovalKind.Review || request.Status != TenantApprovalStatus.Approved
            || request.RequestId != review.RequestId || request.Revision != review.RequestRevision || request.Operation.ActionHash != review.ActionHash
            || request.RequestId != run.ApprovalRequestId || request.TenantId != run.Owner.TenantId || request.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId
            || request.SessionId != run.SessionId || request.RootActivationId != run.Admission.ExecutionContext?.RootActivationId
            || request.OriginalInitiatorPrincipalId != run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId || request.ExpiresAt <= at
            || decision.DecisionId != review.DecisionId || decision.RequestId != request.RequestId || decision.TenantId != request.TenantId
            || decision.RequestRevision != request.Revision || decision.Status != TenantApprovalStatus.Approved || decision.ActionHash != review.ActionHash
            || execution.ApprovalRequestId != request.RequestId || execution.OperationId != request.Operation.OperationId || execution.ActionHash != review.ActionHash
            || execution.ToolCallId != "operation:" + request.Operation.OperationId.ToString("D") || execution.ProfileId != command.ProfileId
            || execution.ProfileRevision != command.ProfileRevision || execution.Background != TenantApprovalRules.SandboxCommand(command).Background)
            throw new SecurityConflictException("sandbox_approval_mismatch");
        var exact = TenantApprovalRules.SandboxCommand(command);
        if (execution.CommandText != (exact.Command ?? JsonSerializer.Serialize(exact.Argv)) || execution.DeadlineAt != execution.StartedAt.AddSeconds(exact.TimeoutSeconds!.Value))
            throw new SecurityConflictException("sandbox_approval_mismatch");
        var audit = review.Audit;
        if (audit.TenantId != request.TenantId || audit.ActorPrincipalId != request.ExecutorPrincipalId || audit.Action != "sandbox.command.reserve"
            || audit.Target != execution.Id.ToString("D") || audit.ReasonCode != "sandbox_command_reserved" || audit.Decision != "allowed" || audit.At != at)
            throw new ArgumentException("Invalid exact Sandbox reservation audit.");
    }
}
