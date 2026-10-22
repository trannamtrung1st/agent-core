using System.Text.Json;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public sealed record TenantAutomationMutationPreparation(TenantAutomationMutationObservation Source, PreparedAutomationMutation Value);
public sealed class TenantAutomationMutationService(ITenantAutomationMutationStore store, TenantOperationAuthorizer operations, IamAuthorizer iam,
    ITenantApprovalStore approvals, ITriggerCommandAuthorizer semantic, IModelCatalog models, IIdGenerator ids, TimeProvider time, IEventFilterEvaluator? filters = null,
    TenantRunConfigurationResolver? configurations = null)
{
    public async ValueTask<TenantAutomationMutationPreparation> PrepareAsync(TenantRunReference reference, Guid? generation, string tool, string arguments,
        CancellationToken ct, PreparedAutomationMutation? expected = null, Guid? human = null)
    {
        var source = await store.ObserveTenantAutomationMutationAsync(reference, generation, tool, arguments, expected, human, ct).ConfigureAwait(false);
        var args = TenantAutomationMutationContract.Parse(tool, arguments); var at = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        if (generation is not null && source.Reader.Run.Claim?.LeaseExpiresAtUtc <= time.GetUtcNow()) throw new SecurityConflictException("execution_claim_expired");
        if (expected is not null) { await CurrentAsync(source, expected, ct).ConfigureAwait(false); return new(source, expected); }
        if (tool == ToolCatalog.AutomationRun)
        {
            var resolved = await ResolveInvocationAsync(source, ct).ConfigureAwait(false); var current = source.Current!;
            var model = source.Conversation is null ? ExecutionModelPolicy.Resolve(models, resolved.Configuration.Configuration.Definition, source.Reader.Agent.Instance, current).Pin
                : TenantAutomationConversationRules.Model(current, source.Conversation, resolved.Configuration.Configuration.Definition, models);
            if (model is null) throw new AccessDeniedException("automation_model_unavailable");
            var invocation = new PreparedAutomationInvocation(current.ExecutionTarget.SessionId ?? ids.NewSessionId(), ids.NewId(), ids.NewId(), ids.NewId(), ids.NewId(),
                TenantAutomationRules.Hash(resolved.Configuration), model, source.Conversation is null ? null : new(source.Conversation.Snapshot.Revision, source.Conversation.Snapshot.DurableLastEntrySequence));
            var next = current.WithScheduleAdvance(current.Status, current.NextOccurrenceAtUtc, current.OccurrenceCount, current.Revision + 1, at, null);
            var plan = new PreparedAutomationMutation(source.Reader.Run.SessionId, source.Reader.Root.SessionId, tool, args.Json, current, next,
                TenantAutomationMutationRules.IntentHash(source, args), false, invocation);
            TenantAutomationMutationRules.Validate(source, plan); await CurrentAsync(source, plan, ct).ConfigureAwait(false); return new(source, plan);
        }
        var change = TriggerScheduleCommands.NormalizeTenant(source.Reader.Current, source.Current, args, source.Reader.Run.SessionId, source.Reader.Root.CreatedAtUtc, ids);
        var provenance = source.Current?.Provenance.WithUpdated(at) ?? new TriggerProvenance(TriggerAuthorizationOrigin.AgentTask, null, null, at, at, change.PresetId, change.PresetVersion);
        var proposed = tool == ToolCatalog.AutomationDelete ? source.Current!.WithCancellation(source.Current.Revision + 1, at)
            : TenantAutomationAuthoring.Build(source.Reader.Agent, source.Reader.Current, source.Current, change, provenance, at, models, ids, filters);
        var value = new PreparedAutomationMutation(source.Reader.Run.SessionId, source.Reader.Root.SessionId, tool, args.Json, source.Current, proposed,
            TenantAutomationMutationRules.IntentHash(source, args), false);
        TenantAutomationMutationRules.Validate(source, value); await CurrentAsync(source, value, ct).ConfigureAwait(false); return new(source, value);
    }
    private async ValueTask CurrentAsync(TenantAutomationMutationObservation source, PreparedAutomationMutation value, CancellationToken ct)
    {
        await TenantAutomationMutationRules.RawAsync(source, value, iam, ct).ConfigureAwait(false);
        if (value.ToolName == ToolCatalog.AutomationRun)
        {
            await TenantAutomationInvocationRules.RequesterAsync(source, iam, ct).ConfigureAwait(false);
            var resolved = await ResolveInvocationAsync(source, ct).ConfigureAwait(false);
            if (TenantAutomationRules.Hash(resolved.Configuration) != value.Invocation!.ConfigurationHash) throw new SecurityConflictException("automation_configuration_changed");
            var pin = source.Conversation is null ? ExecutionModelPolicy.Resolve(models, resolved.Configuration.Configuration.Definition, source.Reader.Agent.Instance, source.Current!).Pin
                : TenantAutomationConversationRules.Model(source.Current!, source.Conversation, resolved.Configuration.Configuration.Definition, models);
            if (pin is null || JsonSerializer.Serialize(pin) != JsonSerializer.Serialize(value.Invocation.Model)) throw new AccessDeniedException("automation_model_unavailable");
            return;
        }
        if (value.ToolName is ToolCatalog.AutomationDelete or ToolCatalog.AutomationDisable) return;
        var reader = source.Reader; var proposed = value.Proposed;
        await TenantCompletionDestinationRules.AuthoringAsync(reader.Security, reader.Agent.PrincipalId, proposed, source.ReportDestination, iam, ct).ConfigureAwait(false);
        if (TenantAutomationConversationRules.Existing(proposed))
        {
            var target = TenantAutomationConversationRules.Require(proposed, source.Conversation);
            await TenantAutomationConversationRules.AuthoringAsync(reader.Security, reader.Agent.PrincipalId, proposed, target, iam, ct).ConfigureAwait(false);
            _ = TenantAutomationConversationRules.Model(proposed, target, reader.Current, models);
        }
        if (proposed.Status == AutomationStatus.Active && (proposed.IsSchedule && proposed.NextOccurrenceAtUtc is null
            || !TenantAutomationConversationRules.Existing(proposed) && !ExecutionModelPolicy.Resolve(models, reader.Current, reader.Agent.Instance, proposed).Accepted))
            throw new AccessDeniedException("automation_model_unavailable");
    }
    public async ValueTask<OperationAuthorizationDecision> DecisionAsync(TenantAutomationMutationPreparation prepared, CancellationToken ct)
    {
        var source = prepared.Source; var value = prepared.Value; var reader = source.Reader;
        var primary = TenantAutomationMutationRules.Primary(reader.Security.Tenant.TenantId, value.ToolName, value.Proposed); var review = value.SemanticReviewRequired;
        foreach (var (key, scope) in TenantAutomationMutationRules.Targets(source, value).Distinct())
        {
            var result = await operations.Check(reader.Security, reader.Run, key, scope, ct).ConfigureAwait(false);
            if (result.Decision == OperationDecisionKind.Denied || result.ApprovalKind == ApprovalKind.ExceptionalGrant)
                return new(OperationDecisionKind.Denied, "automation_source_authority_required", TenantAutomationMutationRules.Permission(value.ToolName), primary);
            review |= result.Decision == OperationDecisionKind.RequiresApproval;
        }
        return await operations.Check(reader.Security, reader.Run, TenantAutomationMutationRules.Permission(value.ToolName), primary, ct, review).ConfigureAwait(false);
    }
    public async ValueTask<TenantAutomationMutationPreparation> VerifyAsync(TenantAutomationMutationPreparation prepared, CancellationToken ct)
    {
        var value = prepared.Value; var source = prepared.Source;
        using var args = JsonDocument.Parse(value.ArgumentsJson);
        // Existing configuration must not reach a classifier before its required
        // source Review. The human then judges this exact saved Before/After.
        if (source.Current is not null)
            foreach (var (key, scope) in TenantAutomationMutationRules.Targets(source, value).Distinct())
                if (await TenantSandboxReadOperationRules.NeedsReviewAsync(source.Reader.Security, source.Reader.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false))
                    return prepared with { Value = value with { SemanticReviewRequired = true } };
        var exact = JsonSerializer.SerializeToElement(value);
        var action = JsonSerializer.SerializeToElement(new { tool = value.ToolName, arguments = args.RootElement,
            current = exact.GetProperty("Current"), proposed = exact.GetProperty("Proposed"),
            invocation = value.Invocation,
            referenceTimeUtc = source.Reader.Root.CreatedAtUtc, sessionId = source.Reader.Run.SessionId });
        if (System.Text.Encoding.UTF8.GetByteCount(action.GetRawText()) > 32768)
            return prepared with { Value = value with { SemanticReviewRequired = true } };
        var verdict = await semantic.AuthorizeAsync(new(source.IntentText, TriggerAuthorization.ActionForTool(value.ToolName), value.ToolName, action,
            value.IntentHash, AdmittedTask: source.AdmittedTask), ct).ConfigureAwait(false);
        if (verdict.Decision is TriggerCommandAuthorizationDecision.Deny or TriggerCommandAuthorizationDecision.Ambiguous)
            throw new AccessDeniedException(verdict.Decision == TriggerCommandAuthorizationDecision.Deny ? "automation_intent_denied" : "automation_intent_ambiguous");
        return prepared with { Value = value with { SemanticReviewRequired = verdict.Decision != TriggerCommandAuthorizationDecision.Allow } };
    }
    public async ValueTask<string> ExecuteAsync(TenantRuntimeFrame frame, ModelToolCall call, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(call.Id) || call.Id.Length > 200) throw new ArgumentException("Use a trusted call identity.");
        var args = TenantAutomationMutationContract.Parse(call.Name, call.ArgumentsJson); var reference = new TenantRunReference(frame.Owner.TenantId!.Value, frame.RunId);
        var hash = TenantAutomationMutationRules.ParametersHash(call.Name, args.Json);
        if (await store.FindDirectOperationReceiptAsync(reference.TenantId, reference.RunId, call.Id, ct).ConfigureAwait(false) is { } old)
        {
            var (run, _) = await operations.ResolveAsync(reference, ct).ConfigureAwait(false); return Result(TenantLocalEffectRules.MatchDirectReceipt(old, run, call.Id, hash), true);
        }
        var before = await PrepareAsync(reference, frame.Generation, call.Name, args.Json, ct).ConfigureAwait(false);
        var decision = await DecisionAsync(before, ct).ConfigureAwait(false);
        if (decision.Decision == OperationDecisionKind.Allowed)
        {
            before = await VerifyAsync(before, ct).ConfigureAwait(false); decision = await DecisionAsync(before, ct).ConfigureAwait(false);
        }
        if (decision.Decision != OperationDecisionKind.Allowed) return JsonSerializer.Serialize(new { decision = decision.Decision == OperationDecisionKind.Denied ? "denied" : "requiresApproval", reasonCode = decision.ReasonCode, applied = false });
        var reader = before.Source.Reader; var prepared = TenantApprovalRules.PrepareAutomationMutation(ids.NewId(), reader.Run.AgentInstanceId, before.Value, reader.Agent.Instance.Revision, reader.Agent.Instance.PersonaRevision);
        var prior = await approvals.FindRootApprovalAsync(reference.TenantId, reader.Run.Admission.Activation.RootActivationId, reader.Agent.PrincipalId, prepared, ct).ConfigureAwait(false);
        if (prior is not null) return JsonSerializer.Serialize(new { decision = prior.Status == TenantApprovalStatus.Pending ? "requiresApproval" : "denied", reasonCode = TenantApprovalRules.DuplicateReason(prior), applied = false });
        var state = reader.Security; var receipt = await store.TryCommitTenantAutomationMutationAsync(state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } }, state.Tenant.Revision,
            new(prepared.OperationId, reference.RunId, frame.Generation, reader.Run.Revision, call.Id, hash, before.Value, reader.Agent.Instance.Revision, prepared.ActionHash,
                await InvocationAdmissionAsync(before, ct).ConfigureAwait(false)),
            new(ids.NewId(), reference.TenantId, reader.Agent.PrincipalId, "operations.automations.mutate", before.Value.Proposed.AutomationId.ToString("D"), "allowed", TenantAutomationMutationRules.Completed, time.GetUtcNow()), ct).ConfigureAwait(false);
        return receipt is null ? throw new SecurityConflictException("stale_revision") : Result(receipt, false);
    }
    public static string Result(TenantOperationReceipt receipt, bool existing) => JsonSerializer.Serialize(new { decision = "allowed", applied = !existing, alreadyCompleted = existing,
        operationId = receipt.OperationId, automationId = receipt.AutomationMutation?.AutomationId, revision = receipt.AutomationMutation?.Revision, reasonCode = receipt.ReasonCode,
        occurrenceAdmitted = receipt.AutomationMutation?.ToolName == ToolCatalog.AutomationRun, executionCompleted = false,
        admittedSessionId = receipt.AutomationMutation?.InvocationSessionId, admittedRunId = receipt.AutomationMutation?.InvocationRunId });
    private async ValueTask<(TenantAgent Agent, AgentCore.Application.Execution.ResolvedAgentRunConfiguration Configuration)> ResolveInvocationAsync(TenantAutomationMutationObservation source, CancellationToken ct) =>
        await (configurations ?? throw new AccessDeniedException("tenant_effect_unavailable")).ResolveAsync(source.Reader.Security.Tenant.TenantId, source.Reader.Run.AgentInstanceId, ct).ConfigureAwait(false);
    public async ValueTask<TenantAutomationAdmission?> InvocationAdmissionAsync(TenantAutomationMutationPreparation prepared, CancellationToken ct)
    {
        if (prepared.Value.Invocation is null) return null;
        var resolved = await ResolveInvocationAsync(prepared.Source, ct).ConfigureAwait(false);
        return TenantAutomationInvocationRules.Build(prepared.Source, prepared.Value, resolved.Configuration, TriggerScheduleCalculator.Truncate(time.GetUtcNow()));
    }
    public async ValueTask<TenantAutomationMutationPreparation> InspectionAsync(TenantApprovalRequest request, Guid human, CancellationToken ct) =>
        await PrepareAsync(new(request.TenantId, request.RunId), null, request.Operation.AutomationMutation!.ToolName, request.Operation.AutomationMutation.ArgumentsJson, ct, request.Operation.AutomationMutation, human).ConfigureAwait(false);
}
