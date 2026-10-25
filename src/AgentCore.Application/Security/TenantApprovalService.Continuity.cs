using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;
public sealed partial class TenantApprovalService
{
    internal async ValueTask<TenantContinuityApprovalVisibility> ContinuityVisibilityAsync(Guid tenant, TenantSessionInitiator human, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var memory = await MemoryVisibilityAsync(tenant, human, ct).ConfigureAwait(false);
        return new(human.PrincipalId, await VisibleIds(state, human.PrincipalId, "agents.read", "agent", ct).ConfigureAwait(false),
            await VisibleIds(state, human.PrincipalId, "agents.chat", "agent", ct).ConfigureAwait(false), memory.Sessions, memory.Agents, await VisibleIds(state, human.PrincipalId, "agent-runs.read", "agent-run", ct).ConfigureAwait(false));
    }

    public async ValueTask<TenantApprovalPreparationResult> PrepareContinuityAsync(TenantRunReference reference, ModelToolCall call,
        long expectedRunRevision, Guid generation, CancellationToken ct = default)
    {
        try
        {
            await operations.ResolveForDecisionAsync(reference, "sessions.read", ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            var service = continuityReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
            var observed = await service.PrepareAsync(reference, generation, call.Name, call.ArgumentsJson, ct).ConfigureAwait(false); var run = observed.Reader.Run;
            if (run.Revision != expectedRunRevision) throw new SecurityConflictException("stale_revision");
            var op = TenantApprovalRules.PrepareContinuity(ids.NewId(), run.AgentInstanceId, observed.Value, observed.Reader.Agent.Instance.Revision, observed.Reader.Agent.Instance.PersonaRevision);
            op = TenantApprovalRules.ForRun(op, run);
            var prior = await approvals.FindRootApprovalAsync(reference.TenantId, run.Admission.Activation.RootActivationId, observed.Reader.Agent.PrincipalId, op, ct).ConfigureAwait(false);
            if (prior is not null) return await DuplicateOutcome(observed.Reader.Security, run, prior, ct).ConfigureAwait(false);
            var decision = await service.DecisionAsync(observed, ct).ConfigureAwait(false);
            if (decision.Decision != OperationDecisionKind.RequiresApproval) return await PreparationOutcome(observed.Reader.Security, run, op, decision.Decision, decision.ReasonCode, null, ct).ConfigureAwait(false);
            if (decision.ApprovalKind != ApprovalKind.Review) return await PreparationOutcome(observed.Reader.Security, run, op, OperationDecisionKind.Denied, "continuity_read_required", null, ct).ConfigureAwait(false);
            var at = time.GetUtcNow(); var request = new TenantApprovalRequest(ids.NewId(), reference.TenantId, run.AgentRunId, run.SessionId, run.Admission.Activation.RootActivationId,
                observed.Reader.Agent.PrincipalId, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, ApprovalKind.Review, op, TenantApprovalStatus.Pending, 1, at, at.AddHours(1), new(observed.Reader.Agent.Instance.Persona.Name));
            var qualified = false;
            foreach (var human in observed.Reader.Security.Users.Where(u => u.Status == IdentityStatus.Active))
            {
                try { await service.ReviewerAsync(request, observed, human.PrincipalId, ct).ConfigureAwait(false); qualified = true; break; }
                catch (AccessDeniedException) { }
            }
            if (!qualified) return await PreparationOutcome(observed.Reader.Security, run, op, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
            return await PersistAsync(reference, run, observed.Reader.Security, observed.Reader.Agent, op, request.Snapshot, true, expectedRunRevision, generation, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, "sessions.read", error is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)error).ReasonCode,
                ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false); throw;
        }
    }
}
