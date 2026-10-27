using System.Text.Json;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Tasks;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareTaskDelegationAsync(TenantRunReference reference, string arguments,
        long revision, Guid generation, CancellationToken ct = default)
    {
        try { return await PrepareTaskDelegationCoreAsync(reference, arguments, revision, generation, ct); }
        catch (Exception error) when (error is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, "tasks.delegate", error is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)error).ReasonCode,
                ExecutionAuthorizationPurpose.ApprovalPreparation, ct);
            throw;
        }
    }
    private async ValueTask<TenantApprovalPreparationResult> PrepareTaskDelegationCoreAsync(TenantRunReference reference, string arguments,
        long revision, Guid generation, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(arguments) > 64_000) throw new ArgumentException("Task consent arguments exceed their bound.");
        using var json = JsonDocument.Parse(arguments, new JsonDocumentOptions { MaxDepth = 12 });
        using var schema = JsonDocument.Parse(TenantTaskToolContract.Descriptors().Single(d => d.Name == "tasks.delegate").ModelDefinition.ParametersJson);
        TenantTaskToolService.Closed(json.RootElement, schema.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name),
            schema.RootElement.GetProperty("required").EnumerateArray().Select(p => p.GetString()!));
        var requested = TenantTaskToolService.ParseDelegate(json.RootElement);
        var (run, state) = await operations.ResolveForDecisionAsync(reference, "tasks.delegate", ExecutionAuthorizationPurpose.ApprovalPreparation, ct);
        TenantLocalEffectRules.RequireDirectRun(run, generation, time.GetUtcNow());
        if (run.Revision != revision) throw new SecurityConflictException("stale_revision");
        var source = await (security as ITenantSessionStore ?? throw new AccessDeniedException("tenant_effect_unavailable"))
            .FindTenantSessionAsync(reference.TenantId, run.SessionId, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var (agent, current) = await configuration.CurrentPolicyAsync(reference.TenantId, run.AgentInstanceId, ct);
        var actor = new TaskActor(Agent: new(run.Owner, run.SessionId, run.AgentRunId, generation), Tool: "tasks.delegate");
        await TaskAuthority.SourceAsync(state, actor, source, agent, run, current, authorizer, time.GetUtcNow(), "sessions.read", true, ct);
        if (await approvals.FindTaskDelegationApprovalAsync(reference.TenantId, $"run:{run.AgentRunId:D}", requested.RequestKey, ct) is { } previous)
        {
            TenantApprovalRules.MatchTaskDelegationRequest(previous, requested);
            return await DuplicateOutcome(state, run, previous, ct);
        }
        var preparation = await (taskDelegations ?? throw new AccessDeniedException("tenant_effect_unavailable"))
            .PrepareConsentAsync(reference.TenantId, run.SessionId, actor, requested, state.Tenant.Revision, ct);
        if (preparation.Replay is not null) throw new SecurityConflictException("task_already_admitted");
        var admission = preparation.Admission ?? throw new InvalidOperationException("Prepared Task assignment is missing.");
        var reserved = new PreparedTaskDelegation(admission.Task, generation, admission.WorkerSession?.Entries.Single().EntryId,
            admission.WorkerRun?.ActivationId, admission.WorkerRun?.ResponseId);
        var operation = TenantApprovalRules.ForRun(TenantApprovalRules.PrepareTaskDelegation(ids.NewId(), agent.Instance.InstanceId, reserved,
            agent.Instance.Revision, agent.Instance.PersonaRevision), run);
        var compound = await TenantTaskConsentRules.CompoundReviewAsync(state, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, operation, authorizer, ct);
        var decision = await operations.Evaluate(state, run.Admission.ExecutionContext, "tasks.delegate",
            TenantSessionRules.Scope(reference.TenantId, run.SessionId), ct, compound);
        if (decision.Decision != OperationDecisionKind.RequiresApproval)
            return await PreparationOutcome(state, run, operation, decision.Decision, decision.ReasonCode, null, ct);
        var at = time.GetUtcNow(); var expires = at.AddHours(1) < admission.Task.DeadlineAt ? at.AddHours(1) : admission.Task.DeadlineAt;
        var request = new TenantApprovalRequest(ids.NewId(), reference.TenantId, run.AgentRunId, run.SessionId, run.Admission.ExecutionContext.RootActivationId,
            agent.PrincipalId, run.Admission.ExecutionContext.OriginalInitiatorPrincipalId, decision.ApprovalKind!.Value, operation,
            TenantApprovalStatus.Pending, 1, at, expires, new(agent.Instance.Persona.Name));
        var qualified = false;
        foreach (var human in state.Users.Where(u => u.Status == IdentityStatus.Active))
        {
            try { await TenantTaskConsentRules.ReviewerAsync(state, request, human.PrincipalId, authorizer, ct); qualified = true; break; }
            catch (AccessDeniedException) { }
        }
        if (!qualified) return await PreparationOutcome(state, run, operation, OperationDecisionKind.Denied, "no_eligible_approver", null, ct);
        // Persistence is owned by the ordinary approval transaction, never Task preparation.
        return await PersistAsync(reference, run, state, agent, operation, request.Snapshot, compound, revision, generation, ct);
    }
}
