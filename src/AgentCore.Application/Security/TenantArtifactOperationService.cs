using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantArtifactPublicationResult(OperationDecisionKind Decision, string ReasonCode, TenantOperationReceipt? Receipt = null);

public sealed class TenantArtifactOperationService(ITenantArtifactOperationStore effects, TenantOperationAuthorizer operations,
    TenantRunConfigurationResolver configuration, IIdGenerator ids, TimeProvider time, IamAuthorizer iam, ITenantApprovalStore approvals,
    TenantArtifactSourceTarget? workspaceSources = null)
{
    public ValueTask<TenantArtifactPublicationResult> ExecuteAsync(TenantRunReference reference, Guid generation,
        string callId, string arguments, CancellationToken ct = default) => ExecuteCoreAsync(reference, generation, callId, arguments, null, ct);
    public ValueTask<TenantArtifactPublicationResult> ExecuteWorkspaceAsync(TenantRunReference reference, Guid generation,
        string callId, string arguments, CancellationToken ct = default) => ExecuteCoreAsync(reference, generation, callId, arguments, TenantWorkspaceArtifactContract.Parse(arguments), ct);
    private async ValueTask<TenantArtifactPublicationResult> ExecuteCoreAsync(TenantRunReference reference, Guid generation,
        string callId, string arguments, TenantWorkspaceArtifactArguments? workspace, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(callId) || callId.Length > 200) throw new ArgumentException("Invalid trusted call identity.");
        var inline = workspace is null ? TenantArtifactToolContract.Parse(arguments) : null;
        var canonical = workspace?.CanonicalArguments ?? inline!.CanonicalArguments; var parametersHash = workspace?.ParametersHash ?? inline!.ParametersHash;
        var (run, state) = await operations.ResolveForDecisionAsync(reference, TenantArtifactToolContract.Permission, ExecutionAuthorizationPurpose.EffectEligibility, ct).ConfigureAwait(false);
        try
        {
            TenantLocalEffectRules.RequireDirectRun(run, generation, time.GetUtcNow());
            var previous = await effects.FindDirectOperationReceiptAsync(reference.TenantId, reference.RunId, callId, ct).ConfigureAwait(false);
            if (previous is not null) return new(OperationDecisionKind.Allowed, "operation_completed", TenantArtifactOperationRules.Match(previous, run, callId, parametersHash));
            var current = await configuration.ResolveAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
            var source = workspace is null ? null : await (workspaceSources ?? throw new AccessDeniedException("tenant_effect_unavailable"))
                .CaptureAsync(new(run.Owner, run.SessionId, run.AgentRunId, generation), workspace, ct).ConfigureAwait(false);
            var publication = new PreparedArtifactPublication(run.SessionId, canonical, source is null ? null : TenantArtifactSourceRules.Witness(source.Observation));
            var operation = TenantApprovalRules.PrepareArtifact(ids.NewId(), run.AgentInstanceId, publication, current.Agent.Instance.Revision, current.Agent.Instance.PersonaRevision);
            var correlated = await approvals.FindRootApprovalAsync(reference.TenantId, run.Admission.Activation.RootActivationId, current.Agent.PrincipalId, operation, ct).ConfigureAwait(false);
            if (correlated is not null) return new(correlated.Status == TenantApprovalStatus.Pending ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Denied, TenantApprovalRules.DuplicateReason(correlated));
            var scope = TenantSessionRules.Scope(reference.TenantId, run.SessionId);
            var decision = await operations.Check(state, run, TenantArtifactToolContract.Permission, scope, ct).ConfigureAwait(false);
            if (decision.Decision != OperationDecisionKind.Allowed) return new(decision.Decision, decision.ReasonCode);
            await TenantArtifactOperationRules.RequireAuthorityAsync(state, run, iam, ct).ConfigureAwait(false);
            if (!TenantFileExecutionContract.Allows(run, current.Configuration.Configuration.Definition) || !TenantArtifactToolContract.AllowsPublication(current.Configuration.Configuration.Definition, publication)) throw new AccessDeniedException("capability_unavailable");
            var commit = new TenantArtifactCommit(operation.OperationId, run.AgentRunId, generation, run.Revision, callId, canonical,
                parametersHash, current.Agent.Instance.Revision, TenantArtifactToolContract.ActionHash(run.SessionId, parametersHash), publication.Source, source);
            _ = TenantArtifactOperationRules.Validate(run, current.Agent, commit, time.GetUtcNow());
            var receipt = await effects.TryCommitArtifactAsync(state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } }, state.Tenant.Revision,
                commit, new(ids.NewId(), reference.TenantId, run.Owner.ExecutorPrincipalId!.Value, TenantArtifactOperationRules.AuditAction,
                    run.SessionId.ToString("D"), "allowed", "operation_completed", time.GetUtcNow()), ct).ConfigureAwait(false);
            if (receipt is null)
            {
                await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
                receipt = await effects.FindDirectOperationReceiptAsync(reference.TenantId, reference.RunId, callId, ct).ConfigureAwait(false);
            }
            return receipt is not null ? new(OperationDecisionKind.Allowed, "operation_completed", TenantArtifactOperationRules.Match(receipt, run, callId, parametersHash))
                : throw new SecurityConflictException("stale_revision");
        }
        catch (Exception ex) when (ex is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, TenantArtifactToolContract.Permission,
                ex is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)ex).ReasonCode, ExecutionAuthorizationPurpose.EffectEligibility, ct).ConfigureAwait(false);
            throw;
        }
    }
}
