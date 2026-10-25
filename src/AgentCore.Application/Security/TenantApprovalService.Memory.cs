using AgentCore.Application.Tools;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    internal async ValueTask<TenantMemoryApprovalVisibility> MemoryVisibilityAsync(Guid tenant, TenantSessionInitiator human, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var readable = await VisibleIds(state, human.PrincipalId, "memory.read", "agent", ct).ConfigureAwait(false);
        var agentsRead = await VisibleIds(state, human.PrincipalId, "agents.read", "agent", ct).ConfigureAwait(false);
        readable = readable is null ? agentsRead : agentsRead is null ? readable : readable.Intersect(agentsRead).ToArray();
        return new(human.PrincipalId, readable, await VisibleIds(state, human.PrincipalId, "sessions.read", "session", ct).ConfigureAwait(false));
    }

    public async ValueTask<TenantApprovalPreparationResult> PrepareMemoryAsync(TenantRunReference reference, ModelToolCall call,
        long expectedRunRevision, Guid generation, CancellationToken ct = default)
    {
        try
        {
            var key = TenantMemoryToolContract.ReadOnly(call.Name) ? "memory.read" : "memory.manage";
            await operations.ResolveForDecisionAsync(reference, key, ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            var service = memoryOperations ?? throw new AccessDeniedException("tenant_effect_unavailable");
            var observed = await service.PrepareAsync(reference, generation, call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
            var run = observed.Reader.Run;
            if (run.Revision != expectedRunRevision) throw new SecurityConflictException("stale_revision");
            var op = TenantApprovalRules.PrepareMemory(ids.NewId(), run.AgentInstanceId, observed.Value, observed.Reader.Agent.Instance.Revision, observed.Reader.Agent.Instance.PersonaRevision);
            op = TenantApprovalRules.ForRun(op, run);
            var prior = await approvals.FindRootApprovalAsync(reference.TenantId, run.Admission.Activation.RootActivationId, observed.Reader.Agent.PrincipalId, op, ct).ConfigureAwait(false);
            if (prior is not null) return await DuplicateOutcome(observed.Reader.Security, run, prior, ct).ConfigureAwait(false);
            if (observed.ExistingResult is not null)
                return await PreparationOutcome(observed.Reader.Security, run, op, OperationDecisionKind.Denied, "memory_source_changed", null, ct).ConfigureAwait(false);
            var decision = await service.DecisionAsync(observed, ct).ConfigureAwait(false);
            if (decision.Decision != OperationDecisionKind.RequiresApproval)
                return await PreparationOutcome(observed.Reader.Security, run, op, decision.Decision, decision.ReasonCode, null, ct).ConfigureAwait(false);
            var at = time.GetUtcNow(); var request = new TenantApprovalRequest(ids.NewId(), reference.TenantId, run.AgentRunId, run.SessionId,
                run.Admission.ExecutionContext!.RootActivationId, observed.Reader.Agent.PrincipalId, run.Admission.ExecutionContext.OriginalInitiatorPrincipalId,
                decision.ApprovalKind!.Value, op, TenantApprovalStatus.Pending, 1, at, at.AddHours(1), new(observed.Reader.Agent.Instance.Persona.Name));
            await TenantMemoryOperationReview.ExecutorAsync(request, observed, authorizer, ct).ConfigureAwait(false);
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
            await operations.RecordFailureAsync(reference, TenantMemoryToolContract.ReadOnly(call.Name) ? "memory.read" : "memory.manage",
                e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode, ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false); throw;
        }
    }
}
