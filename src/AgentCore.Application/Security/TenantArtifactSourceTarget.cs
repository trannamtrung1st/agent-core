using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Source preparation only; publication/Run/receipt authority stays with the existing Artifact owner.
public sealed class TenantArtifactSourceTarget(TenantOperationAuthorizer operations, ITenantSessionStore sessions,
    TenantRunConfigurationResolver configurations, ITenantWorkspaceArtifactSourceStore files, IamAuthorizer iam, TimeProvider time)
{
    public async ValueTask<ITenantWorkspaceArtifactSource> CaptureAsync(TenantRuntimeFrame frame, TenantWorkspaceArtifactArguments args, CancellationToken ct = default)
    {
        var reference = new TenantRunReference(frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required"), frame.RunId);
        async ValueTask<(AgentRun Run, TenantSecurityState State, TenantAgent Agent, TenantSession Session)> Require()
        {
            var (run, state) = await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
            var current = await configurations.CurrentPolicyAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
            var session = await sessions.FindTenantSessionAsync(reference.TenantId, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
            TenantArtifactReviewRules.HumanRoot(run);
            TenantArtifactSourceRules.Require(run, current.Agent, current.Definition, session, args.Path);
            await TenantArtifactSourceRules.RequireAuthorityAsync(state, run, args.Path, iam, false, ct).ConfigureAwait(false);
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
        return source;
    }
}
