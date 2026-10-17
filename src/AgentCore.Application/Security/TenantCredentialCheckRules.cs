using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantCredentialCheckRules
{
    public const string Action = "operations.credentials.check";
    public const string DispatchAction = "operations.credentials.check.dispatch";
    private static readonly HashSet<string> Denials = new(StringComparer.Ordinal) {
        "approval_expired", "grant_expired", "grantor_authority_lost", "operation_authority_changed", "requester_unavailable",
        "executor_unavailable", "tenant_unavailable", "executor_read_required", "operation_witness_changed", "credential_binding_changed",
        "credential_unavailable", "oauth_reconnect_required", "oauth_host_disabled", "oauth_not_configured", "oauth_client_changed",
        "credential_check_in_progress", "root_operation_conflict", "operation_configuration_changed" };

    public static DateTimeOffset Milliseconds(DateTimeOffset at) => DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds());
    public static string Denial(string reason) => Denials.Contains(reason) ? reason : "operation_configuration_changed";
    public static string AuditDecision(TenantOperationOutcome outcome) => outcome switch {
        TenantOperationOutcome.Completed => "allowed", TenantOperationOutcome.Denied => "denied", TenantOperationOutcome.Indeterminate => "indeterminate",
        _ => throw new ArgumentException("Unknown credential check outcome.") };
    public static void Start(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant, AgentRun run,
        TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        if (request.Operation.Kind != TenantOperationKind.CredentialConnectionCheck
            || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || (commit.Outcome == TenantOperationOutcome.Completed ? commit.ReasonCode != "operation_completed" : !Denials.Contains(commit.ReasonCode)))
            throw new SecurityConflictException("operation_mismatch");
    }

    public static string? TargetReason(TenantApprovalRequest request, AgentRun run, TenantAgent? agent, TenantCredentialBinding? binding)
    {
        if (agent is null || agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active) return "executor_unavailable";
        try
        {
            TenantApprovalRules.Validate(request, run, agent);
            if (binding is null) return "credential_binding_changed";
            TenantCredentialOperationRules.RequireRequest(request, run, agent, binding);
            return null;
        }
        catch (AccessDeniedException e) { return Denial(e.ReasonCode); }
        catch (SecurityConflictException e) { return Denial(e.ReasonCode); }
    }

    public static TenantCredentialCheckDispatch Dispatch(TenantApprovalRequest request, AgentRun started, DateTimeOffset at) =>
        new(request.Operation.OperationId, request.TenantId, request.RequestId, started.AgentRunId, started.SessionId,
            request.RootActivationId, request.ExecutorPrincipalId, request.Operation.InstanceId, request.Operation.CredentialCheck!.CredentialId,
            started.Claim!.Generation, request.Operation.ActionHash, started.Revision, Milliseconds(at));

    public static void RequireDispatch(TenantCredentialCheckDispatch dispatch, TenantApprovalRequest request, AgentRun run,
        TenantOperationGrant? grant, AgentRun? source)
    {
        TenantApprovalContinuationRules.Require(request, run, source);
        if (dispatch.CompletedAt is not null || request.Status != TenantApprovalStatus.Approved || request.Operation.Kind != TenantOperationKind.CredentialConnectionCheck
            || dispatch.OperationId != request.Operation.OperationId || dispatch.TenantId != request.TenantId || dispatch.RequestId != request.RequestId
            || dispatch.RunId != run.AgentRunId || dispatch.SessionId != run.SessionId || dispatch.RootActivationId != request.RootActivationId
            || dispatch.ExecutorPrincipalId != request.ExecutorPrincipalId || dispatch.InstanceId != request.Operation.InstanceId
            || dispatch.CredentialId != request.Operation.CredentialCheck!.CredentialId || dispatch.ActionHash != request.Operation.ActionHash
            || dispatch.Generation == Guid.Empty || dispatch.StartedRunRevision < 1 || run.Revision < dispatch.StartedRunRevision
            || run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId != request.OriginalInitiatorPrincipalId
            || (request.Kind == ApprovalKind.ExceptionalGrant
                ? grant is null || grant.Status != OperationGrantStatus.Claimed || grant.OperationId != dispatch.OperationId || grant.ActionHash != dispatch.ActionHash
                    || grant.TenantId != dispatch.TenantId || grant.ExecutorPrincipalId != dispatch.ExecutorPrincipalId || grant.RequestId != dispatch.RequestId
                : grant is not null))
            throw new SecurityConflictException("operation_mismatch");
    }
    public static bool Recoverable(TenantCredentialCheckDispatch dispatch, AgentRun run, DateTimeOffset at) =>
        run.Status != AgentRunStatus.Running || run.Claim is not { } claim || claim.Generation != dispatch.Generation || claim.LeaseExpiresAtUtc <= at;

    // Completion maintains the result of a previously authorized dispatch. It grants no further provider use.
    // Never overwrite a newer authorization, owner edit, disconnection or replacement secret.
    public static (CredentialOAuthCheck? Apply, TenantOperationOutcome Outcome, string Reason) Completion(
        TenantApprovalRequest request, Credential? current, CredentialOAuthCheck? check, DateTimeOffset at)
    {
        var witness = request.Operation.CredentialCheck!;
        if (current is null || current.CredentialId != witness.CredentialId || current.Revision != witness.CredentialRevision
            || current.SecretRevision != witness.SecretRevision || TenantCredentialOAuthRules.Epoch(current) != witness.AuthEpoch)
            return (null, TenantOperationOutcome.Indeterminate, "credential_check_target_changed");
        try { TenantCredentialOperationRules.Connected(current); }
        catch (AccessDeniedException) { return (null, TenantOperationOutcome.Indeterminate, "credential_check_target_changed"); }
        if (check is not null)
        {
            try
            {
                _ = TenantCredentialOAuthRules.Checked(current, check, at);
                return (check, TenantOperationOutcome.Completed, check.ReconnectRequired ? "credential_reconnect_required" : "credential_connection_checked");
            }
            catch (ArgumentException) { } // An invalid normalized provider result is still an uncertain dispatch.
        }
        return (new(ReconnectRequired: true), TenantOperationOutcome.Indeterminate, "credential_check_uncertain");
    }

    public static AgentRun SettleRun(AgentRun run, TenantCredentialCheckDispatch dispatch, TenantOperationOutcome outcome, DateTimeOffset at)
    {
        if (run.Status != AgentRunStatus.Running || run.CancellationRequested || run.Claim?.Generation != dispatch.Generation
            || run.SideEffect.Disposition != AgentRunSideEffectDisposition.InFlight || run.SideEffect.ToolCallId != "operation:" + dispatch.OperationId.ToString("D")
            || run.SideEffect.ActionHash != dispatch.ActionHash) return run;
        return run.MarkSideEffect(run.Revision, dispatch.Generation,
            outcome == TenantOperationOutcome.Completed ? AgentRunSideEffectDisposition.Succeeded : AgentRunSideEffectDisposition.Indeterminate,
            run.SideEffect.ToolCallId, dispatch.ActionHash, at);
    }
    public static TenantOperationReceipt Receipt(TenantApprovalRequest request, AgentRun run, TenantAgent? agent,
        TenantOperationOutcome outcome, string reason, DateTimeOffset at) => new(request.Operation.OperationId, request.RequestId, request.TenantId,
            run.AgentRunId, run.SessionId, request.RootActivationId, request.ExecutorPrincipalId, request.Operation.InstanceId, request.Operation.ActionHash,
            outcome, reason, agent?.Instance.Revision ?? request.Operation.ExpectedInstanceRevision,
            agent?.Instance.PersonaRevision ?? request.Operation.ExpectedPersonaRevision, Milliseconds(at));

    public static void Transaction(TenantSecurityState next, long revision, TenantApprovalRequest request, AuthorizationAudit audit, bool dispatch)
    {
        if (next.Tenant.TenantId != request.TenantId || next.Tenant.Revision != checked(revision + 1) || audit.TenantId != request.TenantId
            || audit.ActorPrincipalId != request.ExecutorPrincipalId || audit.Target != request.ResourceId || audit.Action != (dispatch ? DispatchAction : Action))
            throw new ArgumentException("Invalid exact credential operation transaction.");
    }
}
