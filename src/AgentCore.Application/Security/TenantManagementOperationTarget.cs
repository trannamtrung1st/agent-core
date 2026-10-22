using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantManagementMutationObservation(TenantManagementSource Source, PreparedManagementMutation Value, bool SourcesChanged = false);

// Existing Instance configuration preparation plus management observations; no separate effect owner.
public sealed class TenantManagementOperationTarget(ITenantManagementStore store, TenantAgentConfigurationService configuration,
    TenantOperationAuthorizer operations, TenantPreferenceOperationService preferences, TimeProvider time, TenantManagementReadService reads, IIdGenerator ids, IamAuthorizer iam)
{
    public TenantManagementReadService Reads { get; } = reads;
    public async ValueTask<TenantManagementMutationObservation> PrepareAsync(TenantRunReference reference, Guid? generation, string tool, string arguments,
        CancellationToken ct, PreparedManagementMutation? expected = null, Guid? inspectionHuman = null, long? committedSkillRevision = null)
    {
        var args = TenantManagementMutationContract.Parse(tool, arguments);
        var before = await store.ReadTenantManagementSourceAsync(reference, generation, inspectionHuman, ct).ConfigureAwait(false);
        if (inspectionHuman is not null && TenantManagementSkillRules.IsTool(tool))
            await store.RequireTenantManagementSkillSourcesAsync(reference, generation, tool, args.Json, inspectionHuman, ct).ConfigureAwait(false);
        try
        {
            TenantManagementMutationRules.Eligible(before.Reader, tool);
            if (inspectionHuman is null && TenantManagementSkillRules.IsTool(tool))
                await store.RequireTenantManagementSkillSourcesAsync(reference, generation, tool, args.Json, inspectionHuman, ct).ConfigureAwait(false);
            if (expected is not null && expected.Skill?.Changed != false) _ = TenantManagementMutationRules.Change(before.Reader, expected, before.Snapshot);
            PreparedManagementMutation value;
            if (TenantManagementSkillRules.IsTool(tool))
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds((expected?.Skill?.PreparedAt ?? time.GetUtcNow()).ToUnixTimeMilliseconds());
                await configuration.ValidateManagementSkillAsync(before, args, at, ct).ConfigureAwait(false);
                value = TenantManagementSkillRules.Build(before, args, at);
            }
            else
            {
                var change = await configuration.PrepareManagementCommandAsync(before, args, ct).ConfigureAwait(false);
                value = TenantManagementMutationRules.Build(before, args, change, time.GetUtcNow());
            }
            if (expected is not null && !TenantManagementMutationRules.Matches(expected, value)) throw new AccessDeniedException("management_source_changed");
            var after = await store.ReadTenantManagementSourceAsync(reference, generation, inspectionHuman, ct).ConfigureAwait(false);
            if (before.Reader.Security.Tenant.Revision != after.Reader.Security.Tenant.Revision || before.Reader.Run.Revision != after.Reader.Run.Revision
                || TenantManagementReadRules.ConfigurationHash(before.Reader.Agent.Instance) != TenantManagementReadRules.ConfigurationHash(after.Reader.Agent.Instance))
                throw new SecurityConflictException("management_source_changed");
            return new(after, expected ?? value);
        }
        catch (Exception error) when (inspectionHuman is not null && expected is not null && error is AccessDeniedException or SecurityConflictException or AgentCore.Application.Sessions.AgentCoreException)
        {
            if (expected.Skill is not null && before.Reader.Agent.Instance.Revision != committedSkillRevision && !(await iam.CheckOwnedTenantAsync(before.Reader.Security, inspectionHuman.Value,
                PermissionCatalog.Get("skills.read"), AccessScope.ForTenant(reference.TenantId), ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approval_unavailable");
            return new(before, expected, true);
        }
        catch (AgentCore.Application.Sessions.AgentCoreException error) { throw new ArgumentException("The management command violates current configured constraints.", error); }
    }
    public ValueTask<OperationAuthorizationDecision> DecisionAsync(TenantManagementMutationObservation observed, CancellationToken ct)
    {
        var reader = observed.Source.Reader; var skill = observed.Value.Skill is not null;
        return operations.Check(reader.Security, reader.Run, skill ? "skills.manage" : "agents.update",
            skill ? TenantManagementSkillRules.Scope(reader, observed.Value) : TenantManagementReadRules.Scope(reader), ct,
            managementReviewRequired: !skill || !TenantManagementSkillRules.AutoApplies(reader, observed.Value.ToolName));
    }
    public async ValueTask<string> ExecuteAsync(TenantRuntimeFrame frame, ModelToolCall call, int budget, CancellationToken ct)
    {
        if (budget < 768) return AgentCore.Application.Execution.AgentRunToolCallCheckpoint.FinishRequiredResult(budget);
        if (TenantManagementSkillRules.IsTool(call.Name) && store is ITenantDirectOperationStore direct)
        {
            var reference = new TenantRunReference(frame.Owner.TenantId!.Value, frame.RunId);
            if (await direct.FindDirectOperationReceiptAsync(reference.TenantId, reference.RunId, call.Id, ct).ConfigureAwait(false) is { } saved)
            {
                var current = await store.ReadTenantManagementSourceAsync(reference, frame.Generation, ct: ct).ConfigureAwait(false);
                var args = TenantManagementMutationContract.Parse(call.Name, call.ArgumentsJson);
                saved = TenantLocalEffectRules.MatchDirectReceipt(saved, current.Reader.Run, call.Id, TenantManagementSkillRules.ParametersHash(call.Name, args.Json));
                return SkillResult(saved);
            }
        }
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
        if (observed.Value.Skill is { } skill && decision.Decision != OperationDecisionKind.Denied)
        {
            if (!skill.Changed) return SkillResult(null, skill.Result, observed.Source.Reader.Agent.Instance.Revision);
            if (decision.Decision == OperationDecisionKind.Allowed)
            {
                var reader = observed.Source.Reader;
                var operation = TenantApprovalRules.PrepareManagementMutation(ids.NewId(), reader.Run.AgentInstanceId, observed.Value, reader.Agent.Instance.Revision, reader.Agent.Instance.PersonaRevision);
                var commit = new TenantManagementSkillCommit(operation, frame.RunId, frame.Generation, reader.Run.Revision, call.Id,
                    TenantManagementSkillRules.ParametersHash(call.Name, observed.Value.ArgumentsJson));
                var audit = new AuthorizationAudit(ids.NewId(), reader.Security.Tenant.TenantId, reader.Agent.PrincipalId, "operations.skills.apply", reader.Run.AgentInstanceId.ToString("D"), "allowed", TenantManagementMutationRules.Completed, time.GetUtcNow());
                var receipt = await store.TryCommitManagementSkillAsync(reader.Security with { Tenant = reader.Security.Tenant with { Revision = checked(reader.Security.Tenant.Revision + 1) } }, reader.Security.Tenant.Revision, commit, audit, ct).ConfigureAwait(false)
                    ?? throw new SecurityConflictException("management_source_changed");
                return SkillResult(receipt);
            }
        }
        return JsonSerializer.Serialize(new { decision = decision.Decision == OperationDecisionKind.RequiresApproval ? "requiresApproval" : "denied", reasonCode = decision.ReasonCode,
            applied = false, operation = "mutate_management", replayAllowed = false });
    }
    private static string SkillResult(TenantOperationReceipt? receipt, TenantManagementSkillResult? unchanged = null, long? unchangedInstanceRevision = null)
    {
        var result = unchanged ?? receipt!.ManagementSkill!.Result;
        return JsonSerializer.Serialize(new { decision = "allowed", reasonCode = receipt?.ReasonCode ?? "operation_unchanged",
            applied = receipt?.Outcome == TenantOperationOutcome.Completed, unchanged = receipt is null, instanceRevision = receipt?.InstanceRevision ?? unchangedInstanceRevision, operationId = receipt?.OperationId,
            skill = TenantManagementSkillRules.SkillMetadata(result), instanceSkill = TenantManagementSkillRules.SkillMetadata(result, true),
            definitionSkill = TenantManagementSkillRules.DefinitionMetadata(result), deletedKey = result.Deleted ? result.Key : null,
            changesApplyTo = "nextRun", replayAllowed = false });
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
    public async ValueTask<TenantManagementMutationObservation> InspectionAsync(TenantApprovalRequest request, Guid human, CancellationToken ct, TenantOperationReceipt? recorded = null) => await PrepareAsync(new(request.TenantId, request.RunId), null,
        request.Operation.ManagementMutation!.ToolName, request.Operation.ManagementMutation.ArgumentsJson, ct, request.Operation.ManagementMutation, human,
        TenantManagementSkillRules.CompletedChange(request, recorded) ? recorded!.InstanceRevision : null).ConfigureAwait(false);
    public static async ValueTask ReviewerAsync(TenantApprovalRequest request, TenantManagementMutationObservation observed, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        if (observed.SourcesChanged) throw new AccessDeniedException("management_source_changed");
        await TenantManagementMutationRules.ReviewerAsync(request, observed.Source.Reader, observed.Value, human, iam, ct).ConfigureAwait(false);
    }
}
