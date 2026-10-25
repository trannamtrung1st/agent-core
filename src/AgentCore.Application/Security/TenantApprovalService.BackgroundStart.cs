using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareBackgroundStartAsync(TenantRunReference reference, string arguments, long revision, Guid generation, CancellationToken ct = default)
    {
        try { return await PrepareBackgroundStartCoreAsync(reference, arguments, revision, generation, ct).ConfigureAwait(false); }
        catch (Exception error) when (error is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, "", error is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)error).ReasonCode,
                ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            throw;
        }
    }
    private async ValueTask<TenantApprovalPreparationResult> PrepareBackgroundStartCoreAsync(TenantRunReference reference, string arguments, long revision, Guid generation, CancellationToken ct)
    {
        var task = TenantBackgroundStartArguments.Parse(arguments);
        var (run, state) = await operations.ResolveForDecisionAsync(reference, "agent-runs.create", ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
        TenantLocalEffectRules.RequireDirectRun(run, generation, time.GetUtcNow());
        if (run.Revision != revision) throw new SecurityConflictException("stale_revision");
        var (agent, resolved, _) = await (backgroundCreations ?? throw new AccessDeniedException("tenant_effect_unavailable")).SnapshotAsync(run, ct).ConfigureAwait(false);
        var primary = "agent-runs.create";
        if (!(await authorizer.CheckTenantAsync(state, run.Owner.ExecutorPrincipalId!.Value, PermissionCatalog.Get("sessions.create"), AccessScope.ForTenant(reference.TenantId), ct).ConfigureAwait(false)).Allowed)
            primary = "sessions.create";
        var value = new PreparedBackgroundStart(reference.TenantId, run.SessionId, ids.NewSessionId(), ids.NewId(), ids.NewId(), ids.NewId(), ids.NewId(),
            task.Objective, task.Title, task.ReportCompletion, primary, TenantBackgroundCreationRules.Hash(resolved), new(run.PinnedModel.CatalogKey, run.PinnedModel.ProviderAlias, run.PinnedModel.ModelId, run.PinnedModel.ReasoningEffort));
        var op = TenantApprovalRules.PrepareBackgroundStart(ids.NewId(), run.AgentInstanceId, value, agent.Instance.Revision, agent.Instance.PersonaRevision);
        op = TenantApprovalRules.ForRun(op, run);
        var previous = await approvals.FindRootApprovalAsync(reference.TenantId, run.Admission.ExecutionContext!.RootActivationId, agent.PrincipalId, op, ct).ConfigureAwait(false);
        if (previous is not null) return await DuplicateOutcome(state, run, previous, ct).ConfigureAwait(false);
        var compound = await TenantBackgroundCreationRules.CompoundReviewAsync(state, run.Admission.ExecutionContext.OriginalInitiatorPrincipalId, primary, authorizer, ct).ConfigureAwait(false);
        var decision = await operations.Evaluate(state, run.Admission.ExecutionContext, primary, AccessScope.ForTenant(reference.TenantId), ct, compound).ConfigureAwait(false);
        var at = time.GetUtcNow();
        var request = new TenantApprovalRequest(ids.NewId(), reference.TenantId, run.AgentRunId, run.SessionId, run.Admission.ExecutionContext.RootActivationId, agent.PrincipalId,
            run.Admission.ExecutionContext.OriginalInitiatorPrincipalId, decision.ApprovalKind ?? ApprovalKind.Review, op, TenantApprovalStatus.Pending, 1, at, at.AddHours(1), new(agent.Instance.Persona.Name));
        await backgroundCreations.RequireAsync(request, run, state, ct).ConfigureAwait(false);
        if (decision.Decision != OperationDecisionKind.RequiresApproval) return await PreparationOutcome(state, run, op, decision.Decision, decision.ReasonCode, null, ct).ConfigureAwait(false);
        var qualified = false;
        foreach (var human in state.Users.Where(u => u.Status == IdentityStatus.Active))
        {
            try { await TenantBackgroundCreationRules.ReviewerAsync(state, request, human.PrincipalId, authorizer, ct).ConfigureAwait(false); qualified = true; break; }
            catch (AccessDeniedException) { }
        }
        if (!qualified) return await PreparationOutcome(state, run, op, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
        return await PersistAsync(reference, run, state, agent, op, request.Snapshot, compound, revision, generation, ct).ConfigureAwait(false);
    }
}
