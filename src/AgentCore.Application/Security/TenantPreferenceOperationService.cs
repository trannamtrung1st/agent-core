using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantPreferenceResult(OperationDecisionKind Decision, string ReasonCode, TenantOperationReceipt? Receipt = null);

public sealed class TenantPreferenceOperationService(ITenantPreferenceOperationStore effects, TenantOperationAuthorizer operations,
    TenantRunConfigurationResolver configuration, IModelCatalog models, ProviderAliasSet aliases, IIdGenerator ids, TimeProvider time, ITenantApprovalStore approvals)
{
    public async ValueTask<TenantPreferenceResult> ExecuteAsync(TenantRunReference reference, Guid generation, string callId, string arguments, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(callId) || callId.Length > 200) throw new ArgumentException("Invalid trusted call identity.");
        var patch = TenantPreferenceToolContract.Parse(arguments);
        var (run, state) = await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
        TenantPreferenceOperationRules.RequireRun(run, generation, time.GetUtcNow());
        var saved = await effects.FindPreferenceReceiptAsync(reference.TenantId, run.AgentRunId, callId, ct).ConfigureAwait(false);
        if (saved is not null) return new(OperationDecisionKind.Allowed, "operation_completed", TenantPreferenceOperationRules.Match(saved, run, callId, patch.ParametersHash));
        var scope = AccessScope.ForResource(reference.TenantId, "agent", run.AgentInstanceId.ToString("D"));
        if ((await operations.Check(state, run.Admission.ExecutionContext!, "agents.read", scope, ct).ConfigureAwait(false)).Decision != OperationDecisionKind.Allowed)
            return new(OperationDecisionKind.Denied, "executor_read_required");
        var decision = await operations.Check(state, run.Admission.ExecutionContext!, TenantPreferenceToolContract.Permission, scope, ct).ConfigureAwait(false);
        if (decision.Decision != OperationDecisionKind.Allowed) return new(decision.Decision, decision.ReasonCode);
        var (target, baseline) = await configuration.BaselineAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        var prepared = TenantApprovalRules.PreparePreferences(ids.NewId(), run.AgentInstanceId, TenantPreferenceToolContract.Parameters(patch),
            target.Instance.Revision, target.Instance.PersonaRevision);
        var previous = await approvals.FindRootApprovalAsync(reference.TenantId, run.Admission.Activation.RootActivationId,
            target.PrincipalId, prepared, ct).ConfigureAwait(false);
        if (previous is not null) return new(previous.Status == TenantApprovalStatus.Pending ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Denied,
            TenantApprovalRules.DuplicateReason(previous));
        var current = AgentCore.Application.Agents.InstanceSettingsResolver.Resolve(baseline, target.Instance.SettingsOverrides)
            with { SelfManagement = AgentCore.Domain.Definitions.ManagementPolicyResolver.Resolve(baseline, target.Instance) };
        if (!TenantPreferenceToolContract.Allows(run.Admission.Configuration!.Definition) || !TenantPreferenceToolContract.Allows(current))
            return new(OperationDecisionKind.Denied, "management_unavailable");
        _ = AgentInstanceSettingsService.ResolvePatch(baseline, target.Instance.SettingsOverrides ?? new(), "conversationPolicy", patch.Set, patch.Clear, models, aliases);
        var at = time.GetUtcNow();
        var commit = new TenantPreferenceCommit(prepared.OperationId, run.AgentRunId, generation, run.Revision, callId, patch.CanonicalArguments,
            patch.ParametersHash, target.Instance.Revision, TenantPreferenceToolContract.ActionHash(run.AgentInstanceId, target.Instance.Revision, patch.ParametersHash));
        var audit = new AuthorizationAudit(ids.NewId(), reference.TenantId, run.Owner.ExecutorPrincipalId!.Value, "operations.preferences.apply", run.AgentInstanceId.ToString("D"), "allowed", "operation_completed", at);
        TenantOperationReceipt? receipt;
        try { receipt = await effects.TryCommitPreferencesAsync(state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } }, state.Tenant.Revision, commit, audit, ct).ConfigureAwait(false); }
        catch (AccessDeniedException ex) when (ex.ReasonCode is "already_pending" or "previously_rejected" or "previously_expired" or "previously_cancelled" or "already_approved")
        { return new(ex.ReasonCode == "already_pending" ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Denied, ex.ReasonCode); }
        if (receipt is null)
        {
            await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
            receipt = await effects.FindPreferenceReceiptAsync(reference.TenantId, run.AgentRunId, callId, ct).ConfigureAwait(false);
        }
        return receipt is not null ? new(OperationDecisionKind.Allowed, "operation_completed", TenantPreferenceOperationRules.Match(receipt, run, callId, patch.ParametersHash))
            : throw new SecurityConflictException("stale_revision");
    }
}
