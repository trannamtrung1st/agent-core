using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Source preparation only; publication/Run/receipt authority stays with the existing Artifact owner.
public sealed record TenantArtifactSourceObservationTarget(AgentRun Run, TenantSecurityState State, TenantAgent Agent, ITenantWorkspaceArtifactSource Source);
public sealed class TenantArtifactSourceTarget(TenantOperationAuthorizer operations, ITenantSessionStore sessions,
    TenantRunConfigurationResolver configurations, ITenantWorkspaceArtifactSourceStore files, IamAuthorizer iam, TimeProvider time)
{
    public async ValueTask<ITenantWorkspaceArtifactSource> CaptureAsync(TenantRuntimeFrame frame, TenantWorkspaceArtifactArguments args, CancellationToken ct = default) =>
        (await CaptureCoreAsync(frame, args, false, true, ct).ConfigureAwait(false)).Source;
    public ValueTask<TenantArtifactSourceObservationTarget> PrepareAsync(TenantRuntimeFrame frame, TenantWorkspaceArtifactArguments args, CancellationToken ct = default) =>
        CaptureCoreAsync(frame, args, true, true, ct);
    private async ValueTask<TenantArtifactSourceObservationTarget> CaptureCoreAsync(TenantRuntimeFrame frame, TenantWorkspaceArtifactArguments args, bool review, bool live, CancellationToken ct)
    {
        var reference = new TenantRunReference(frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required"), frame.RunId);
        async ValueTask<(AgentRun Run, TenantSecurityState State, TenantAgent Agent, TenantSession Session)> Require()
        {
            var (run, state) = await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
            var current = await configurations.CurrentPolicyAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
            var session = await sessions.FindTenantSessionAsync(reference.TenantId, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            if (live) TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
            else if (frame.Owner != run.Owner || frame.RunId != run.AgentRunId || frame.SessionId != run.SessionId
                || run.CancellationRequested || run.Status is not (AgentRunStatus.Running or AgentRunStatus.WaitingForApproval)) throw new AccessDeniedException("tenant_runtime_unavailable");
            if (run.Admission.ExecutionContext?.Origin != ExecutionOrigin.ApprovalContinuation) TenantArtifactReviewRules.HumanRoot(run);
            TenantArtifactSourceRules.Require(run, current.Agent, current.Definition, session, args.Path);
            await TenantArtifactSourceRules.RequireAuthorityAsync(state, run, args.Path, iam, review, ct).ConfigureAwait(false);
            return (run, state, current.Agent, session);
        }
        var before = await Require().ConfigureAwait(false);
        ITenantWorkspaceArtifactSource source;
        try { source = await files.CaptureArtifactSourceTenantAsync(TenantArtifactSourceRules.Owner(before.Run, args.Path), args.Path, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is AgentCoreException or IOException or UnauthorizedAccessException) { throw new AccessDeniedException("artifact_content_unavailable"); }
        var after = await Require().ConfigureAwait(false);
        if (before.Run.Revision != after.Run.Revision || before.State.Tenant.Revision != after.State.Tenant.Revision
            || before.Agent.Instance.Revision != after.Agent.Instance.Revision || before.Agent.Instance.PersonaRevision != after.Agent.Instance.PersonaRevision
            || before.Session.Owner != after.Session.Owner || before.Session.Snapshot.Revision != after.Session.Snapshot.Revision)
            throw new SecurityConflictException("workspace_source_changed");
        var witness = TenantArtifactSourceRules.Witness(source.Observation); TenantArtifactSourceRules.ValidateWitness(witness, args.Path);
        if (source.Observation.Owner != TenantArtifactSourceRules.Owner(after.Run, args.Path)) throw new SecurityConflictException("workspace_source_changed");
        return new(after.Run, after.State, after.Agent, source);
    }

    public async ValueTask<ITenantWorkspaceArtifactSource> RequireSavedAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct = default)
    {
        var value = request.Operation.ArtifactPublication ?? throw new SecurityConflictException("operation_mismatch");
        var saved = value.Source ?? throw new SecurityConflictException("operation_mismatch");
        var current = await configurations.CurrentPolicyAsync(request.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        var sourceRun = request.RunId != run.AgentRunId ? await operations.FindRunAsync(new(request.TenantId, request.RunId), ct).ConfigureAwait(false) : null;
        TenantApprovalRules.Validate(request, run, current.Agent, source: sourceRun);
        _ = TenantArtifactReviewRules.Require(request, run, current.Agent, current.Definition,
            await sessions.FindTenantSessionAsync(request.TenantId, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable"), sourceRun);
        var target = await CaptureCoreAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim?.Generation ?? Guid.Empty),
            TenantWorkspaceArtifactContract.Parse(value.ArgumentsJson), true, false, ct).ConfigureAwait(false);
        if (target.Run.Revision != run.Revision || target.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision
            || target.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision || TenantArtifactSourceRules.Witness(target.Source.Observation) != saved)
            throw new SecurityConflictException("workspace_source_changed");
        return target.Source;
    }
}
