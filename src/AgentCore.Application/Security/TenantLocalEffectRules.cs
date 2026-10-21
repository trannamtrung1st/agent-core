using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantLocalEffectRules
{
    public static void RequireDirectRun(AgentRun run, Guid generation, DateTimeOffset at)
    {
        if (run.Owner.TenantId is null || run.Admission.ExecutionContext is null || run.Status != AgentRunStatus.Running
            || run.Claim?.Generation != generation || run.Claim.LeaseExpiresAtUtc <= at || run.CancellationRequested
            || run.SideEffect.Disposition is AgentRunSideEffectDisposition.Prepared or AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
            throw new SecurityConflictException("tenant_runtime_unavailable");
    }
    public static TenantOperationReceipt MatchDirectReceipt(TenantOperationReceipt receipt, AgentRun run, string call, string parametersHash)
    {
        if (receipt.RequestId is not null || receipt.TenantId != run.Owner.TenantId || receipt.RunId != run.AgentRunId
            || receipt.SessionId != run.SessionId || receipt.InstanceId != run.AgentInstanceId || receipt.RootActivationId != run.Admission.ExecutionContext!.RootActivationId
            || receipt.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId || receipt.SourceCallId != call || receipt.ParametersHash != parametersHash)
            throw new SecurityConflictException("operation_mismatch");
        return receipt;
    }

    public static void Validate(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant,
        AgentRun run, TenantOperationCommit commit, AgentRun? source = null)
    {
        if (request.Operation.MemoryOperation is not null)
        { TenantMemoryOperationReview.ValidateEffect(request, decision, grant, run, commit, source); return; }
        if (request.Operation.AccountRead is not null)
        { TenantAccountReadRules.ValidateEffect(request, decision, grant, run, commit, source); return; }
        if (request.Operation.BackgroundStart is not null)
        { TenantBackgroundCreationRules.ValidateEffect(request, decision, grant, run, commit, source); return; }
        if (request.Operation.ArtifactPublication is not null)
        { TenantArtifactReviewRules.ValidateEffect(request, decision, grant, run, commit, source); return; }
        if (request.Operation.WorkspaceRead is not null)
        { TenantWorkspaceReadOperationRules.ValidateEffect(request, decision, grant, run, commit, source); return; }
        if (request.Operation.AttachmentRead is not null)
        { TenantAttachmentReadRules.ValidateEffect(request, decision, grant, run, commit, source); return; }
        if (request.Operation.CompletionRead is not null)
        { TenantCompletionReadRules.ValidateEffect(request, decision, grant, run, commit, source); return; }
        if (request.Operation.ArtifactVerification is not null)
        { TenantArtifactVerificationRules.ValidateEffect(request, decision, grant, run, commit, source); return; }
        if (request.Operation.CredentialRead is not null)
        { TenantCredentialReadOperationRules.ValidateEffect(request, decision, grant, run, commit, source); return; }
        if (request.Operation.SandboxRead is not null)
        { TenantSandboxReadEffectRules.Validate(request, decision, grant, run, commit, source); return; }
        if (request.Operation.SandboxControl is not null)
        { TenantSandboxControlEffectRules.Validate(request, decision, grant, run, commit, source); return; }
        ValidateApproval(request, decision, grant, run, commit, source);
        if (!request.Operation.IsLocalConfiguration || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied))
            throw new SecurityConflictException("operation_mismatch");
        if (commit.Outcome == TenantOperationOutcome.Completed ? commit.ReasonCode != "operation_completed" : !DenialReasons.Contains(commit.ReasonCode))
            throw new ArgumentException("Invalid local operation outcome.");
    }

    public static void ValidateApproval(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant,
        AgentRun run, TenantOperationCommit commit, AgentRun? source = null)
    {
        var context = run.Admission.ExecutionContext;
        TenantApprovalContinuationRules.Require(request, run, source);
        if (request.RunId != run.AgentRunId) TenantRunContinuationService.ValidateAuthorizationCheckpoint(run, request, requirePending: true);
        if (context is null || request.TenantId != context.TenantId || request.RootActivationId != context.RootActivationId
            || request.ExecutorPrincipalId != context.AgentPrincipalId || request.OriginalInitiatorPrincipalId != context.OriginalInitiatorPrincipalId
            || request.Status != TenantApprovalStatus.Approved || request.Revision != commit.ExpectedRequestRevision
            || run.ApprovalRequestId != request.RequestId || request.RequestId != commit.RequestId || request.Operation.OperationId != commit.OperationId
            || request.Operation.InstanceId != run.AgentInstanceId || request.Operation.ActionHash != commit.ActionHash
            || decision.DecisionId != commit.ExpectedDecisionId || decision.RequestId != request.RequestId || decision.TenantId != request.TenantId
            || decision.Status != TenantApprovalStatus.Approved || decision.ActionHash != commit.ActionHash || decision.RequestRevision != request.Revision
            || run.AgentRunId != commit.RunId || run.Revision != commit.ExpectedRunRevision || run.Status != AgentRunStatus.Running
            || run.Claim?.Generation != commit.Generation || run.CancellationRequested || !Enum.IsDefined(commit.Outcome))
            throw new SecurityConflictException("operation_mismatch");
        var op = request.Operation;
        if (TenantApprovalRules.Rebuild(op) != op)
            throw new SecurityConflictException("operation_mismatch");
        if (request.Kind == ApprovalKind.ExceptionalGrant)
        {
            if (grant is null || grant.RequestId != request.RequestId || grant.TenantId != request.TenantId || grant.ExecutorPrincipalId != context.AgentPrincipalId
                || grant.OperationId != commit.OperationId || grant.ActionHash != commit.ActionHash || grant.Revision != commit.ExpectedGrantRevision
                || grant.Status != OperationGrantStatus.Available || grant.EffectIdempotencyKey != "operation:" + commit.OperationId.ToString("D"))
                throw new SecurityConflictException("grant_unavailable");
        }
        else if (grant is not null || commit.ExpectedGrantRevision is not null) throw new SecurityConflictException("grant_mismatch");
    }

    public static string? Eligibility(AgentRun run, TenantAgent target, AgentDefinition currentDefinition)
    {
        if (run.Admission.Configuration?.Definition.SelfManagement?.Allows(HarnessManagementScope.Settings) != true
            || currentDefinition.SelfManagement?.Allows(HarnessManagementScope.Settings) != true)
            return "management_unavailable";
        return null;
    }

    public static TenantOperationCommit AtCommit(TenantOperationCommit commit, TenantApprovalRequest request, TenantOperationGrant? grant,
        AgentRun run, TenantAgent target, DateTimeOffset at, AgentRun? source = null)
    {
        if (run.Claim!.LeaseExpiresAtUtc <= at) throw new SecurityConflictException("execution_claim_expired");
        TenantApprovalRules.Validate(request, run, target, requireCurrentWitness: false, source: source);
        if (commit.Outcome == TenantOperationOutcome.Denied) return commit;
        var reason = grant is not null && grant.ExpiresAt <= at ? "grant_expired" : request.ExpiresAt <= at ? "approval_expired"
            : request.Operation.IsLocalConfiguration && ManagementPolicyResolver.Resolve(run.Admission.Configuration!.Definition, target.Instance).Allows(HarnessManagementScope.Settings) != true ? "management_unavailable"
            : target.Instance.Revision != request.Operation.ExpectedInstanceRevision || target.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision ? "operation_witness_changed" : null;
        return reason is null ? commit : commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = reason };
    }

    private static readonly HashSet<string> DenialReasons = new(StringComparer.Ordinal)
    { "approval_expired", "grant_expired", "grantor_authority_lost", "operation_authority_changed", "operation_witness_changed",
        "management_unavailable", "operation_configuration_changed", "requester_unavailable", "root_operation_conflict" };

    public static TenantOperationReceipt MatchingReceipt(TenantOperationReceipt receipt, Guid tenant, TenantOperationCommit commit)
    {
        if (receipt.TenantId != tenant || receipt.RequestId != commit.RequestId || receipt.OperationId != commit.OperationId
            || receipt.RunId != commit.RunId || receipt.ActionHash != commit.ActionHash) throw new SecurityConflictException("operation_mismatch");
        return receipt;
    }
    public static TenantAgentConfigurationChange Change(TenantApprovalRequest request, AgentInstance current)
    {
        var operation = request.Operation;
        if (TenantApprovalRules.Rebuild(operation) != operation) throw new SecurityConflictException("operation_mismatch");
        if (operation.Persona is { } persona)
            return new(current.InstanceId, operation.ExpectedInstanceRevision, persona, operation.ExpectedPersonaRevision);
        var patch = TenantPreferenceToolContract.Patch(operation.Preferences!);
        return new(current.InstanceId, operation.ExpectedInstanceRevision, SetSettingsOverrides: true,
            SettingsOverrides: InstanceSettingsResolver.Patch(current.SettingsOverrides ?? new(), "conversationPolicy", patch.Set, patch.Clear), AutomationPolicyChanges: []);
    }
    public static string AuditAction(PreparedTenantOperation operation) => operation.MemoryOperation is not null ? "operations.memory.apply" : operation.AccountRead is not null ? TenantAccountReadRules.AuditAction : operation.CompletionRead is not null ? TenantCompletionReadRules.AuditAction : operation.BackgroundStart is not null ? TenantBackgroundCreationRules.AuditAction : operation.AttachmentRead is not null ? TenantAttachmentReadRules.AuditAction : operation.WorkspaceRead is not null ? TenantWorkspaceReadOperationRules.AuditAction : operation.ArtifactVerification is not null ? TenantArtifactVerificationRules.AuditAction : operation.ArtifactPublication is not null ? TenantArtifactOperationRules.AuditAction : operation.CredentialRead is not null ? TenantCredentialReadOperationRules.AuditAction : operation.SandboxRead is not null ? TenantSandboxReadOperationRules.AuditAction : operation.SandboxControl is not null ? TenantSandboxControlOperationRules.AuditAction : operation.Kind == TenantOperationKind.Persona ? "operations.persona.apply" : "operations.preferences.apply";

    public static void ValidateTransaction(TenantSecurityState next, long expected, TenantApprovalRequest request, AuthorizationAudit audit)
    {
        if (next.Tenant.TenantId != request.TenantId || next.Tenant.Revision != checked(expected + 1) || audit.TenantId != request.TenantId
            || audit.ActorPrincipalId != request.ExecutorPrincipalId || audit.Action != AuditAction(request.Operation) || audit.Target != request.ResourceId)
            throw new ArgumentException("Invalid exact local effect transaction.");
    }
}
