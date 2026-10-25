using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareWorkspaceMutationAsync(TenantRunReference reference, ModelToolCall call,
        long expectedRunRevision, Guid generation, CancellationToken ct = default)
    {
        try
        {
            var (run, _) = await operations.ResolveForDecisionAsync(reference, "workspaces.write", ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            if (run.Revision != expectedRunRevision) throw new SecurityConflictException("stale_revision");
            var target = await (workspaceMutations ?? throw new AccessDeniedException("tenant_effect_unavailable")).PrepareAsync(new(run.Owner, run.SessionId, run.AgentRunId, generation), call, ct).ConfigureAwait(false);
            if (target.Run.Revision != expectedRunRevision) throw new SecurityConflictException("stale_revision");
            var value = TenantApprovalRules.MutationParameters(run.SessionId, target.Arguments, target.ObservationHash);
            var op = TenantApprovalRules.PrepareWorkspaceMutation(ids.NewId(), run.AgentInstanceId, value, target.Agent.Instance.Revision, target.Agent.Instance.PersonaRevision);
            op = TenantApprovalRules.ForRun(op, run);
            var prior = await approvals.FindRootApprovalAsync(reference.TenantId, run.Admission.ExecutionContext!.RootActivationId, target.Agent.PrincipalId, op, ct).ConfigureAwait(false);
            if (prior is not null) return await DuplicateOutcome(target.State, run, prior, ct).ConfigureAwait(false);
            var compoundReview = false;
            foreach (var (key, scope) in TenantWorkspaceMutationOperationRules.Targets(reference.TenantId, run.AgentInstanceId, value, TenantWorkspaceRules.Context(run)))
                if (key != op.Permission || scope != target.Scope)
                    compoundReview |= await TenantSandboxReadOperationRules.NeedsReviewAsync(target.State, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, key, scope, authorizer, ct).ConfigureAwait(false);
            var decision = await operations.Evaluate(target.State, run.Admission.ExecutionContext!, op.Permission, target.Scope, ct, compoundReview).ConfigureAwait(false);
            if (decision.Decision != OperationDecisionKind.RequiresApproval) return await PreparationOutcome(target.State, run, op, decision.Decision, decision.ReasonCode, null, ct).ConfigureAwait(false);
            var request = new TenantApprovalRequest(ids.NewId(), reference.TenantId, run.AgentRunId, run.SessionId, run.Admission.ExecutionContext!.RootActivationId,
                target.Agent.PrincipalId, run.Admission.ExecutionContext.OriginalInitiatorPrincipalId, decision.ApprovalKind!.Value, op, TenantApprovalStatus.Pending, 1,
                time.GetUtcNow(), time.GetUtcNow().AddHours(1), new(target.Agent.Instance.Persona.Name));
            await TenantWorkspaceMutationOperationRules.ExecutorAsync(target.State, request, authorizer, ct).ConfigureAwait(false);
            var qualified = false;
            foreach (var human in target.State.Users.Where(u => u.Status == IdentityStatus.Active))
            {
                try { await TenantWorkspaceMutationOperationRules.ReviewerAsync(target.State, request, human.PrincipalId, authorizer, ct).ConfigureAwait(false); qualified = true; break; }
                catch (AccessDeniedException) { }
            }
            if (!qualified) return await PreparationOutcome(target.State, run, op, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
            return await PersistAsync(reference, run, target.State, target.Agent, op, request.Snapshot, compoundReview, expectedRunRevision, generation, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, "workspaces.write", e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode,
                ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false); throw;
        }
    }
}
