using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

public sealed record TenantPreferenceResult(OperationDecisionKind Decision, string ReasonCode, TenantOperationReceipt? Receipt = null);

public sealed class TenantPreferenceOperationService(ITenantPreferenceOperationStore effects, TenantOperationAuthorizer operations,
    TenantRunConfigurationResolver configuration, ITenantModelCatalogReader modelCatalogs, ProviderAliasSet aliases, IIdGenerator ids, TimeProvider time, ITenantApprovalStore approvals)
{
    public async ValueTask<TenantPreferenceResult> ExecuteAsync(TenantRunReference reference, Guid generation, string callId, string arguments, CancellationToken ct = default, long? expectedInstanceRevision = null)
    {
        if (string.IsNullOrWhiteSpace(callId) || callId.Length > 200) throw new ArgumentException("Invalid trusted call identity.");
        var patch = TenantPreferenceToolContract.Parse(arguments);
        var (run, state) = await operations.ResolveForDecisionAsync(reference, TenantPreferenceToolContract.Permission, ExecutionAuthorizationPurpose.EffectEligibility, ct).ConfigureAwait(false);
        try { return await ExecuteCoreAsync(reference, generation, callId, patch, run, state, expectedInstanceRevision, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is AccessDeniedException or SecurityConflictException)
        {
            var reason = ex is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)ex).ReasonCode;
            await operations.RecordFailureAsync(reference, TenantPreferenceToolContract.Permission, reason, ExecutionAuthorizationPurpose.EffectEligibility, ct).ConfigureAwait(false);
            throw;
        }
    }
    private async ValueTask<TenantPreferenceResult> ExecuteCoreAsync(TenantRunReference reference, Guid generation, string callId,
        TenantPreferencePatch patch, AgentRun run, TenantSecurityState state, long? expectedInstanceRevision, CancellationToken ct)
    {
        TenantLocalEffectRules.RequireDirectRun(run, generation, time.GetUtcNow());
        var saved = await effects.FindDirectOperationReceiptAsync(reference.TenantId, run.AgentRunId, callId, ct).ConfigureAwait(false);
        if (saved is not null) return new(OperationDecisionKind.Allowed, "operation_completed", TenantLocalEffectRules.MatchDirectReceipt(saved, run, callId, patch.ParametersHash));
        var scope = AccessScope.ForResource(reference.TenantId, "agent", run.AgentInstanceId.ToString("D"));
        if ((await operations.Check(state, run, "agents.read", scope, ct).ConfigureAwait(false)).Decision != OperationDecisionKind.Allowed)
        {
            await RecordDenial(state, run, "executor_read_required", ct).ConfigureAwait(false);
            return new(OperationDecisionKind.Denied, "executor_read_required");
        }
        var decision = await operations.Check(state, run, TenantPreferenceToolContract.Permission, scope, ct).ConfigureAwait(false);
        if (decision.Decision != OperationDecisionKind.Allowed) return new(decision.Decision, decision.ReasonCode);
        var (target, baseline) = await configuration.BaselineAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        if (expectedInstanceRevision is { } expected && target.Instance.Revision != expected) throw new SecurityConflictException("stale_revision");
        var prepared = TenantApprovalRules.PreparePreferences(ids.NewId(), run.AgentInstanceId, TenantPreferenceToolContract.Parameters(patch),
            target.Instance.Revision, target.Instance.PersonaRevision);
        var previous = await approvals.FindRootApprovalAsync(reference.TenantId, run.Admission.Activation.RootActivationId,
            target.PrincipalId, prepared, ct).ConfigureAwait(false);
        if (previous is not null)
        {
            await RecordDenial(state, run, TenantApprovalRules.DuplicateReason(previous), ct).ConfigureAwait(false);
            return new(previous.Status == TenantApprovalStatus.Pending ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Denied, TenantApprovalRules.DuplicateReason(previous));
        }
        var current = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(baseline, target.Instance.SettingsOverrides)
            with { SelfManagement = AgentCore.Domain.Definitions.ManagementPolicyResolver.Resolve(baseline, target.Instance) };
        if (!TenantPreferenceToolContract.Allows(run.Admission.Configuration!.Definition) || !TenantPreferenceToolContract.Allows(current))
        {
            await RecordDenial(state, run, "management_unavailable", ct).ConfigureAwait(false);
            return new(OperationDecisionKind.Denied, "management_unavailable");
        }
        var models = await modelCatalogs.ReadAsync(state, ct).ConfigureAwait(false);
        _ = InstanceSettingsRules.ResolvePatch(baseline, target.Instance.SettingsOverrides ?? new(), "conversationPolicy", patch.Set, patch.Clear, models, aliases);
        var at = time.GetUtcNow();
        var commit = new TenantPreferenceCommit(prepared.OperationId, run.AgentRunId, generation, run.Revision, callId, patch.CanonicalArguments,
            patch.ParametersHash, target.Instance.Revision, TenantPreferenceToolContract.ActionHash(run.AgentInstanceId, target.Instance.Revision, patch.ParametersHash));
        var audit = new AuthorizationAudit(ids.NewId(), reference.TenantId, run.Owner.ExecutorPrincipalId!.Value, "operations.preferences.apply", run.AgentInstanceId.ToString("D"), "allowed", "operation_completed", at);
        TenantOperationReceipt? receipt;
        try { receipt = await effects.TryCommitPreferencesAsync(state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } }, state.Tenant.Revision, commit, audit, ct).ConfigureAwait(false); }
        catch (AccessDeniedException ex) when (ex.ReasonCode is "already_pending" or "previously_rejected" or "previously_expired" or "previously_cancelled" or "already_approved")
        {
            await operations.RecordFailureAsync(reference, TenantPreferenceToolContract.Permission, ex.ReasonCode, ExecutionAuthorizationPurpose.EffectEligibility, ct).ConfigureAwait(false);
            return new(ex.ReasonCode == "already_pending" ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Denied, ex.ReasonCode);
        }
        if (receipt is null)
        {
            await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
            receipt = await effects.FindDirectOperationReceiptAsync(reference.TenantId, run.AgentRunId, callId, ct).ConfigureAwait(false);
        }
        return receipt is not null ? new(OperationDecisionKind.Allowed, "operation_completed", TenantLocalEffectRules.MatchDirectReceipt(receipt, run, callId, patch.ParametersHash))
            : throw new SecurityConflictException("stale_revision");
    }
    private ValueTask RecordDenial(TenantSecurityState state, AgentCore.Domain.Conversation.AgentRun run, string reason, CancellationToken ct) =>
        operations.RecordAsync(state, run, new(OperationDecisionKind.Denied, reason, TenantPreferenceToolContract.Permission,
            AccessScope.ForResource(state.Tenant.TenantId, "agent", run.AgentInstanceId.ToString("D"))), ExecutionAuthorizationPurpose.EffectEligibility, ct);

}
