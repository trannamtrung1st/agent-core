using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;
public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareAutomationMutationAsync(TenantRunReference reference, ModelToolCall call,
        long expectedRunRevision, Guid generation, CancellationToken ct = default)
    {
        var permission = TenantAutomationMutationRules.Permission(call.Name);
        try
        {
            await operations.ResolveForDecisionAsync(reference, permission, ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            var service = (automationAdmissions ?? throw new AccessDeniedException("tenant_effect_unavailable")).Mutations;
            var prepared = await service.PrepareAsync(reference, generation, call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
            var reader = prepared.Source.Reader;
            if (reader.Run.Revision != expectedRunRevision) throw new SecurityConflictException("stale_revision");
            var decision = await service.DecisionAsync(prepared, ct).ConfigureAwait(false);
            if (decision.Decision != OperationDecisionKind.Denied) prepared = await service.VerifyAsync(prepared, ct).ConfigureAwait(false);
            var op = TenantApprovalRules.PrepareAutomationMutation(ids.NewId(), reader.Run.AgentInstanceId, prepared.Value, reader.Agent.Instance.Revision, reader.Agent.Instance.PersonaRevision);
            var prior = await approvals.FindRootApprovalAsync(reference.TenantId, reader.Run.Admission.Activation.RootActivationId, reader.Agent.PrincipalId, op, ct).ConfigureAwait(false);
            if (prior is not null) return await DuplicateOutcome(reader.Security, reader.Run, prior, ct).ConfigureAwait(false);
            decision = await service.DecisionAsync(prepared, ct).ConfigureAwait(false);
            if (decision.Decision != OperationDecisionKind.RequiresApproval) return await PreparationOutcome(reader.Security, reader.Run, op, decision.Decision, decision.ReasonCode, null, ct).ConfigureAwait(false);
            var at = time.GetUtcNow(); var request = new TenantApprovalRequest(ids.NewId(), reference.TenantId, reader.Run.AgentRunId, reader.Run.SessionId,
                reader.Run.Admission.ExecutionContext!.RootActivationId, reader.Agent.PrincipalId, reader.Run.Admission.ExecutionContext.OriginalInitiatorPrincipalId,
                decision.ApprovalKind!.Value, op, TenantApprovalStatus.Pending, 1, at, at.AddHours(1), new(reader.Agent.Instance.Persona.Name));
            var qualified = false;
            foreach (var human in reader.Security.Users.Where(u => u.Status == IdentityStatus.Active))
            {
                try { await TenantAutomationMutationRules.ReviewerAsync(request, prepared.Source, human.PrincipalId, authorizer, ct).ConfigureAwait(false); qualified = true; break; }
                catch (AccessDeniedException) { }
            }
            if (!qualified) return await PreparationOutcome(reader.Security, reader.Run, op, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
            return await PersistAsync(reference, reader.Run, reader.Security, reader.Agent, op, request.Snapshot, true, expectedRunRevision, generation, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, permission, error is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)error).ReasonCode, ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false); throw;
        }
    }
    private async ValueTask<bool> AutomationMutationIndependentAsync(TenantApprovalRequest request, TenantAutomationMutationObservation source, CancellationToken ct)
    {
        foreach (var (key, scope) in TenantAutomationMutationRules.Targets(source, request.Operation.AutomationMutation!).Append((request.Permission, TenantApprovalRules.Scope(request.TenantId, request.Operation))).Distinct())
            if (TenantOperationPolicyService.Effective(source.Reader.Security, PermissionCatalog.Get(key)).IndependentReview
                && (key == request.Permission || await TenantSandboxReadOperationRules.NeedsReviewAsync(source.Reader.Security, request.OriginalInitiatorPrincipalId, key, scope, authorizer, ct).ConfigureAwait(false))) return true;
        return false;
    }
    private static IReadOnlyList<TenantApprovalFieldChange> AutomationMutationChanges(PreparedAutomationMutation value)
    {
        static string? Timing(Automation? a) => a is null ? null : AgentCore.Application.Triggers.AutomationRules.Describe(a)
            + string.Concat(a.Triggers.Select(t => $"\nTrigger: {t.TriggerId:D} · revision {t.Revision} · {(t.Enabled ? "Enabled" : "Disabled")}"
                + (t.Configuration is FilteredEventTrigger e ? $"\nFilter: {e.FilterExpression ?? "None"}\nDispatch: {e.Dispatch.Mode} · window {e.Dispatch.WindowSeconds}" : "")));
        var old = value.Current; var next = value.Proposed;
        IReadOnlyList<TenantApprovalFieldChange> changes = [new("Automation", old?.Name ?? "No existing Automation", next.Name), new("Instructions", old?.Instructions, next.Instructions),
            new("State", old?.Status.ToString(), next.Status.ToString()), new("Timing and sources", Timing(old), Timing(next)),
            new("Model", old is null ? "No existing Automation" : old.ModelOverrideOfferingId ?? "Agent default", next.ModelOverrideOfferingId ?? "Agent default"),
            new("Reasoning", old is null ? "No existing Automation" : old.ModelOverrideReasoningEffort ?? "Agent default", next.ModelOverrideReasoningEffort ?? "Agent default"),
            new("Requirements", old is null ? null : $"Tools: {old.RequiresTools} · Vision: {old.RequiresVision}", $"Tools: {next.RequiresTools} · Vision: {next.RequiresVision}"),
            new("Execution destination", old is null ? "No existing Automation" : old.ExecutionTarget.SessionId?.ToString("D") ?? "Independent Background work", next.ExecutionTarget.SessionId?.ToString("D") ?? "Independent Background work"),
            new("Completion report", old is null ? "No existing Automation" : old.CompletionDelivery.SessionId?.ToString("D") ?? "None", next.CompletionDelivery.SessionId?.ToString("D") ?? "None"),
            new("Saved revision", old?.Revision.ToString(), next.Revision.ToString()),
            new("Semantic verification", null, value.SemanticReviewRequired ? "Human verification is required for this exact Before/After change." : "Aligned with the actual admitted request or task.", false)];
        return value.Invocation is { } invocation ? [
            new("Manual occurrence", "Not admitted", "Admit one exact manual occurrence. Admission is separate from execution completion."),
            new("Occurrence model", null, CapturedModelLabel(invocation.Model.OfferingId, invocation.Model.ReasoningEffort, invocation.Model.Binding), false),
            .. changes] : changes;
    }
}
