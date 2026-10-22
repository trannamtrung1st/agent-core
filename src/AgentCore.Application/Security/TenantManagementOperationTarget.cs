using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantManagementMutationObservation(TenantManagementSource Source, PreparedManagementMutation Value, bool SourcesChanged = false);

// Existing Instance configuration preparation plus management observations; no separate effect owner.
public sealed class TenantManagementOperationTarget(ITenantManagementStore store, TenantAgentConfigurationService configuration,
    TenantOperationAuthorizer operations, TenantPreferenceOperationService preferences, TimeProvider time, TenantManagementReadService reads)
{
    public TenantManagementReadService Reads { get; } = reads;
    public async ValueTask<TenantManagementMutationObservation> PrepareAsync(TenantRunReference reference, Guid? generation, string tool, string arguments,
        CancellationToken ct, PreparedManagementMutation? expected = null, Guid? inspectionHuman = null)
    {
        var args = TenantManagementMutationContract.Parse(tool, arguments);
        var before = await store.ReadTenantManagementSourceAsync(reference, generation, inspectionHuman, ct).ConfigureAwait(false);
        try
        {
            TenantManagementMutationRules.Eligible(before.Reader, tool);
            if (expected is not null) _ = TenantManagementMutationRules.Change(before.Reader, expected);
            var change = await configuration.PrepareManagementCommandAsync(before, args, ct).ConfigureAwait(false);
            var value = TenantManagementMutationRules.Build(before, args, change, time.GetUtcNow());
            if (expected is not null && !TenantManagementMutationRules.Matches(expected, value)) throw new AccessDeniedException("management_source_changed");
            var after = await store.ReadTenantManagementSourceAsync(reference, generation, inspectionHuman, ct).ConfigureAwait(false);
            if (before.Reader.Security.Tenant.Revision != after.Reader.Security.Tenant.Revision || before.Reader.Run.Revision != after.Reader.Run.Revision
                || TenantManagementReadRules.ConfigurationHash(before.Reader.Agent.Instance) != TenantManagementReadRules.ConfigurationHash(after.Reader.Agent.Instance))
                throw new SecurityConflictException("management_source_changed");
            return new(after, expected ?? value);
        }
        catch (Exception error) when (inspectionHuman is not null && expected is not null && error is AccessDeniedException or SecurityConflictException or AgentCore.Application.Sessions.AgentCoreException)
        { return new(before, expected, true); }
        catch (AgentCore.Application.Sessions.AgentCoreException error) { throw new ArgumentException("The management command violates current configured constraints.", error); }
    }
    public ValueTask<OperationAuthorizationDecision> DecisionAsync(TenantManagementMutationObservation observed, CancellationToken ct) => operations.Check(observed.Source.Reader.Security,
        observed.Source.Reader.Run, "agents.update", TenantManagementReadRules.Scope(observed.Source.Reader), ct, managementReviewRequired: true);
    public async ValueTask<string> ExecuteAsync(TenantRuntimeFrame frame, ModelToolCall call, int budget, CancellationToken ct)
    {
        if (budget < 768) return AgentCore.Application.Execution.AgentRunToolCallCheckpoint.FinishRequiredResult(budget);
        if (TenantManagementMutationContract.Preference(call.Name, call.ArgumentsJson) is { } patch)
        {
            var source = await PreferenceSourceAsync(new(frame.Owner.TenantId!.Value, frame.RunId), frame.Generation, call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
            var reader = source.Reader;
            if (TenantPreferenceToolContract.Allows(reader.Run.Admission.Configuration!.Definition)
                && TenantPreferenceToolContract.Allows(reader.Current with { SelfManagement = AgentCore.Domain.Definitions.ManagementPolicyResolver.Resolve(reader.Baseline, reader.Agent.Instance) }))
            {
                var result = await preferences.ExecuteAsync(new(frame.Owner.TenantId.Value, frame.RunId), frame.Generation, call.Id, patch.CanonicalArguments, ct,
                    expectedInstanceRevision: TenantManagementMutationContract.Parse(call.Name, call.ArgumentsJson).ExpectedInstanceRevision).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { decision = result.Decision == OperationDecisionKind.Allowed ? "allowed" : result.Decision == OperationDecisionKind.RequiresApproval ? "requiresApproval" : "denied",
                    reasonCode = result.ReasonCode, applied = result.Receipt?.Outcome == TenantOperationOutcome.Completed, instanceRevision = result.Receipt?.InstanceRevision,
                    operationId = result.Receipt?.OperationId, changesApplyTo = "nextRun", replayAllowed = false });
            }
            var review = await operations.Check(reader.Security, reader.Run, "agents.update", TenantManagementReadRules.Scope(reader), ct, managementReviewRequired: true).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { decision = review.Decision == OperationDecisionKind.RequiresApproval ? "requiresApproval" : "denied", reasonCode = review.ReasonCode, applied = false, operation = "mutate_management", replayAllowed = false });
        }
        var observed = await PrepareAsync(new(frame.Owner.TenantId!.Value, frame.RunId), frame.Generation, call.Name, call.ArgumentsJson, ct).ConfigureAwait(false);
        var decision = await DecisionAsync(observed, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { decision = decision.Decision == OperationDecisionKind.RequiresApproval ? "requiresApproval" : "denied", reasonCode = decision.ReasonCode,
            applied = false, operation = "mutate_management", replayAllowed = false });
    }
    public async ValueTask<TenantManagementSource> PreferenceSourceAsync(TenantRunReference reference, Guid? generation, string tool, string arguments, CancellationToken ct)
    {
        var args = TenantManagementMutationContract.Parse(tool, arguments);
        if (TenantManagementMutationContract.Preference(tool, arguments) is null) throw new ArgumentException("Use an exact conversation preference patch.");
        var source = await store.ReadTenantManagementSourceAsync(reference, generation, ct: ct).ConfigureAwait(false);
        TenantManagementMutationRules.Eligible(source.Reader, tool);
        if (args.ExpectedInstanceRevision != source.Reader.Agent.Instance.Revision) throw new SecurityConflictException("stale_revision");
        return source;
    }
    public async ValueTask<TenantManagementMutationObservation> InspectionAsync(TenantApprovalRequest request, Guid human, CancellationToken ct) => await PrepareAsync(new(request.TenantId, request.RunId), null,
        request.Operation.ManagementMutation!.ToolName, request.Operation.ManagementMutation.ArgumentsJson, ct, request.Operation.ManagementMutation, human).ConfigureAwait(false);
    public static async ValueTask ReviewerAsync(TenantApprovalRequest request, TenantManagementMutationObservation observed, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        if (observed.SourcesChanged) throw new AccessDeniedException("management_source_changed");
        await TenantManagementMutationRules.ReviewerAsync(request, observed.Source.Reader, observed.Value, human, iam, ct).ConfigureAwait(false);
    }
}
