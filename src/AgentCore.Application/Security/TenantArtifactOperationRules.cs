using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantArtifactOperationRules
{
    public const string AuditAction = "operations.artifact.publish";
    public static TenantArtifactPublicationArguments Validate(AgentRun run, TenantAgent target, TenantArtifactCommit commit, DateTimeOffset at)
    {
        TenantLocalEffectRules.RequireDirectRun(run, commit.Generation, at);
        TenantArtifactReviewRules.RequireTask(run);
        if (commit.OperationId == Guid.Empty || run.AgentRunId != commit.RunId || run.Revision != commit.ExpectedRunRevision
            || target.TenantId != run.Owner.TenantId || target.Instance.InstanceId != run.AgentInstanceId || target.PrincipalId != run.Owner.ExecutorPrincipalId
            || target.Instance.Revision != commit.ExpectedInstanceRevision || string.IsNullOrWhiteSpace(commit.SourceCallId) || commit.SourceCallId.Length > 200)
            throw new SecurityConflictException("operation_mismatch");
        var publication = new PreparedArtifactPublication(run.SessionId, commit.ArgumentsJson, commit.Source);
        var args = TenantArtifactToolContract.ParsePublication(publication);
        if (args.ParametersHash != commit.ParametersHash || args.CanonicalArguments != commit.ArgumentsJson
            || commit.ActionHash != TenantArtifactToolContract.ActionHash(run.SessionId, args.ParametersHash)) throw new SecurityConflictException("operation_mismatch");
        if ((commit.Source is null) != (commit.SourceContent is null) || commit.SourceContent is { } source &&
            (source.Observation.Owner != TenantArtifactSourceRules.Owner(run, args.SourcePath!) || TenantArtifactSourceRules.Witness(source.Observation) != commit.Source))
            throw new SecurityConflictException("operation_mismatch");
        if (!TenantArtifactToolContract.AllowsPublication(run.Admission.Configuration!.Definition, publication)) throw new AccessDeniedException("capability_unavailable");
        return args;
    }
    public static async ValueTask RequireAuthorityAsync(TenantSecurityState state, AgentRun run, IamAuthorizer iam, CancellationToken ct)
    {
        var target = TenantSessionRules.Scope(state.Tenant.TenantId, run.SessionId); var executor = run.Owner.ExecutorPrincipalId!.Value;
        if (!(await iam.CheckOwnedTenantAsync(state, executor, PermissionCatalog.Get("sessions.read"), target, ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("executor_read_required");
        if (!(await iam.CheckOwnedTenantAsync(state, executor, PermissionCatalog.Get(TenantArtifactToolContract.Permission), target, ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("permission_denied");
        var policy = TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(TenantArtifactToolContract.Permission));
        if (policy.Mode == OperationApprovalMode.Always || policy.Mode == OperationApprovalMode.WhenRequesterLacksPermission
            && run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId is { } human
            && !(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(TenantArtifactToolContract.Permission), target, ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("review_required");
    }
    public static TenantOperationReceipt Match(TenantOperationReceipt receipt, AgentRun run, string call, string parametersHash)
    {
        _ = TenantLocalEffectRules.MatchDirectReceipt(receipt, run, call, parametersHash);
        if (receipt.ActionHash != TenantArtifactToolContract.ActionHash(run.SessionId, parametersHash) || receipt.Outcome != TenantOperationOutcome.Completed
            || receipt.ReasonCode != "operation_completed") throw new SecurityConflictException("operation_mismatch");
        return receipt;
    }
    public static void Transaction(TenantSecurityState next, long expected, AgentRun run, AuthorizationAudit audit)
    {
        if (next.Tenant.TenantId != run.Owner.TenantId || next.Tenant.Revision != checked(expected + 1) || audit.TenantId != next.Tenant.TenantId
            || audit.ActorPrincipalId != run.Owner.ExecutorPrincipalId || audit.Action != AuditAction || audit.Target != run.SessionId.ToString("D")
            || audit.Decision != "allowed" || audit.ReasonCode != "operation_completed") throw new ArgumentException("Invalid direct Artifact transaction.");
    }
}
