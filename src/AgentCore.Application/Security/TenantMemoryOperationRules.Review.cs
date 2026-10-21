using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantMemoryOperationReview
{
    public static async ValueTask ExecutorAsync(TenantApprovalRequest request, TenantMemoryOperationObservation observation, IamAuthorizer iam, CancellationToken ct)
        => await TenantMemoryOperationRules.RequireRawAsync(observation.Reader, observation.Items, iam, ct, manage: !observation.Arguments.ReadOnly,
            exceptional: request.Kind == ApprovalKind.ExceptionalGrant).ConfigureAwait(false);
    public static async ValueTask ReviewerAsync(TenantApprovalRequest request, TenantMemoryOperationObservation observation, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        var reader = observation.Reader;
        if (!reader.Security.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("approver_unqualified");
        await TenantMemoryOperationRules.RequireRawAsync(reader, observation.Items, iam, ct, human, !observation.Arguments.ReadOnly).ConfigureAwait(false);
        foreach (var (key, scope) in TenantMemoryOperationRules.Targets(reader, observation.Items, !observation.Arguments.ReadOnly))
        {
            var primary = key == request.Permission && scope == TenantApprovalRules.Scope(request.TenantId, request.Operation);
            if (!primary && !await TenantSandboxReadOperationRules.NeedsReviewAsync(reader.Security, request.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(reader.Security, PermissionCatalog.Get(key)).IndependentReview && human == request.OriginalInitiatorPrincipalId)
                throw new AccessDeniedException("independent_reviewer_required");
            if (!(await iam.CheckOwnedTenantAsync(reader.Security, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed
                || primary && request.Kind == ApprovalKind.ExceptionalGrant && !(await iam.CheckOwnedTenantAsync(reader.Security, human, PermissionCatalog.Get("approvals.grant_exception"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        }
    }
    public static void ValidateEffect(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant, AgentRun run,
        TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        if (request.Operation.MemoryOperation is not { } memory || AgentCore.Application.Tools.TenantMemoryToolContract.ReadOnly(memory.ToolName) && request.Kind != ApprovalKind.Review
            || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || commit.Outcome == TenantOperationOutcome.Completed && commit.ReasonCode != TenantMemoryOperationRules.Completed
            || commit.ReasonCode.Length is < 1 or > 128 || commit.ReasonCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) throw new SecurityConflictException("operation_mismatch");
    }
}

public static class TenantMemoryReadConsent
{
    public static async ValueTask RequireAsync(TenantApprovalRequest request, TenantMemoryReader reader, IReadOnlyList<AgentCore.Domain.Memory.TenantMemoryReference> sources,
        Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        if (request.Operation.ContinuityRead is { Memory: not null })
        {
            await TenantContinuityReadRules.ConsentAsync(request, reader, sources, human, iam, ct).ConfigureAwait(false); return;
        }
        if (request.Kind != ApprovalKind.Review || request.Status != TenantApprovalStatus.Approved || request.Operation.MemoryOperation is not { } memory
            || !AgentCore.Application.Tools.TenantMemoryToolContract.ReadOnly(memory.ToolName) || memory.SessionId != reader.Run.SessionId || memory.UserPrincipalId != reader.User?.PrincipalId || sources.Any(s => !TenantMemoryRules.Owns(reader, s))
            || request.TenantId != reader.Security.Tenant.TenantId || request.ExecutorPrincipalId != reader.Agent.PrincipalId
            || request.RootActivationId != reader.Run.Admission.Activation.RootActivationId
            || reader.Agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active) throw new AccessDeniedException("memory_read_required");
        await TenantMemoryOperationRules.RequireRawReferencesAsync(reader, sources, iam, ct).ConfigureAwait(false);
        await TenantMemoryOperationRules.RequireRawReferencesAsync(reader, sources, iam, ct, human).ConfigureAwait(false);
        foreach (var (key, scope) in TenantMemoryOperationRules.ReferenceTargets(reader, sources, false))
        {
            if (key != "memory.read" && !await TenantSandboxReadOperationRules.NeedsReviewAsync(reader.Security, request.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(reader.Security, PermissionCatalog.Get(key)).IndependentReview && human == request.OriginalInitiatorPrincipalId
                || !(await iam.CheckOwnedTenantAsync(reader.Security, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("memory_read_required");
        }
    }
}
