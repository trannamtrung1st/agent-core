using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareArtifactAsync(TenantRunReference reference, string arguments,
        long expectedRunRevision, Guid generation, CancellationToken ct = default)
    {
        try { return await PrepareArtifactCoreAsync(reference, arguments, expectedRunRevision, generation, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, "artifacts.create", e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode,
                ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false); throw;
        }
    }
    private async ValueTask<TenantApprovalPreparationResult> PrepareArtifactCoreAsync(TenantRunReference reference, string arguments,
        long expectedRunRevision, Guid generation, CancellationToken ct)
    {
        var args = TenantArtifactToolContract.Parse(arguments);
        var (run, state) = await operations.ResolveForDecisionAsync(reference, "artifacts.create", ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
        TenantArtifactReviewRules.HumanRoot(run);
        TenantLocalEffectRules.RequireDirectRun(run, generation, time.GetUtcNow());
        if (run.Revision != expectedRunRevision) throw new SecurityConflictException("stale_revision");
        var current = await configuration.ResolveAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        var op = TenantApprovalRules.PrepareArtifact(ids.NewId(), run.AgentInstanceId, new(run.SessionId, args.CanonicalArguments), current.Agent.Instance.Revision, current.Agent.Instance.PersonaRevision);
        var decision = await operations.Check(state, run, op.Permission, TenantSessionRules.Scope(reference.TenantId, run.SessionId), ct, purpose: ExecutionAuthorizationPurpose.ApprovalPreparation).ConfigureAwait(false);
        var request = new TenantApprovalRequest(ids.NewId(), reference.TenantId, run.AgentRunId, run.SessionId, run.ActivationId, current.Agent.PrincipalId,
            run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, decision.ApprovalKind ?? (decision.Decision == OperationDecisionKind.Allowed ? ApprovalKind.Review : ApprovalKind.ExceptionalGrant), op, TenantApprovalStatus.Pending, 1,
            time.GetUtcNow(), time.GetUtcNow().AddHours(1), new(current.Agent.Instance.Persona.Name));
        await RequireArtifactAsync(request, run, state, ct).ConfigureAwait(false);
        if (decision.Decision != OperationDecisionKind.RequiresApproval) return await PreparationOutcome(state, run, op, decision.Decision, decision.ReasonCode, null, ct).ConfigureAwait(false);
        var qualified = false;
        foreach (var human in state.Users.Where(u => u.Status == IdentityStatus.Active))
        {
            try { await TenantArtifactReviewRules.ReviewerAsync(state, request, human.PrincipalId, authorizer, ct).ConfigureAwait(false); qualified = true; break; }
            catch (AccessDeniedException) { }
        }
        if (!qualified) return await PreparationOutcome(state, run, op, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
        return await PersistAsync(reference, run, state, current.Agent, op, request.Snapshot, false, expectedRunRevision, generation, ct).ConfigureAwait(false);
    }

    private async ValueTask RequireArtifactAsync(TenantApprovalRequest request, AgentRun run, TenantSecurityState state, CancellationToken ct)
    {
        var sessions = security as ITenantSessionStore ?? throw new AccessDeniedException("tenant_effect_unavailable");
        var session = await sessions.FindTenantSessionAsync(request.TenantId, request.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var current = await configuration.ResolveAsync(request.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        _ = TenantArtifactReviewRules.Require(request, run, current.Agent, current.Configuration.Configuration.Definition, session);
        await TenantArtifactReviewRules.ExecutorAsync(state, request, authorizer, ct).ConfigureAwait(false);
    }
}
