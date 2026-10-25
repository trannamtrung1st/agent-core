using AgentCore.Application.Tools;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareExperienceAsync(TenantRunReference reference, ModelToolCall call,
        long expectedRunRevision, Guid generation, CancellationToken ct = default)
    {
        try
        {
            var args = TenantExperienceToolContract.Parse(call.Name, call.ArgumentsJson);
            var key = args.ReadOnly ? args.SourceKind == AgentCore.Domain.Experience.ExperienceSourceKind.AgentRun ? "agent-runs.read" : "sessions.read" : "memory.manage";
            await operations.ResolveForDecisionAsync(reference, key, ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            var service = experienceOperations ?? throw new AccessDeniedException("tenant_effect_unavailable");
            var observed = await service.PrepareAsync(reference, generation, call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
            var run = observed.Reader.Run;
            if (run.Revision != expectedRunRevision) throw new SecurityConflictException("stale_revision");
            var op = TenantApprovalRules.PrepareExperience(ids.NewId(), run.AgentInstanceId, observed.Value, observed.Reader.Agent.Instance.Revision, observed.Reader.Agent.Instance.PersonaRevision);
            op = TenantApprovalRules.ForRun(op, run);
            var prior = await approvals.FindRootApprovalAsync(reference.TenantId, run.Admission.Activation.RootActivationId, observed.Reader.Agent.PrincipalId, op, ct).ConfigureAwait(false);
            if (prior is not null) return await DuplicateOutcome(observed.Reader.Security, run, prior, ct).ConfigureAwait(false);
            if (observed.ExistingResult is not null)
                return await PreparationOutcome(observed.Reader.Security, run, op, OperationDecisionKind.Denied, "experience_source_changed", null, ct).ConfigureAwait(false);
            var decision = await service.DecisionAsync(observed, ct).ConfigureAwait(false);
            if (decision.Decision != OperationDecisionKind.RequiresApproval)
                return await PreparationOutcome(observed.Reader.Security, run, op, decision.Decision, decision.ReasonCode, null, ct).ConfigureAwait(false);
            var at = time.GetUtcNow(); var request = new TenantApprovalRequest(ids.NewId(), reference.TenantId, run.AgentRunId, run.SessionId,
                run.Admission.ExecutionContext!.RootActivationId, observed.Reader.Agent.PrincipalId, run.Admission.ExecutionContext.OriginalInitiatorPrincipalId,
                decision.ApprovalKind!.Value, op, TenantApprovalStatus.Pending, 1, at, at.AddHours(1), new(observed.Reader.Agent.Instance.Persona.Name));
            await TenantExperienceConsentRules.ExecutorAsync(request, observed, authorizer, ct).ConfigureAwait(false);
            var qualified = false;
            foreach (var human in observed.Reader.Security.Users.Where(u => u.Status == IdentityStatus.Active))
            {
                try { await service.ReviewerAsync(request, observed, human.PrincipalId, ct).ConfigureAwait(false); qualified = true; break; }
                catch (AccessDeniedException) { }
            }
            if (!qualified) return await PreparationOutcome(observed.Reader.Security, run, op, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
            return await PersistAsync(reference, run, observed.Reader.Security, observed.Reader.Agent, op, request.Snapshot, true, expectedRunRevision, generation, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, call.Name == TenantExperienceToolContract.Source ? "sessions.read" : "memory.manage",
                e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode, ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false); throw;
        }
    }
}
