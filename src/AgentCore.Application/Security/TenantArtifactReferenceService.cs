using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Metadata identity validation only. It grants neither file bytes nor a recipient's read access.
public sealed class TenantArtifactReferenceService(ITenantArtifactStore artifacts, ITenantSessionStore sessions,
    TenantOperationAuthorizer operations, TenantRunConfigurationResolver configurations, IamAuthorizer iam, TimeProvider time)
{
    public IArtifactReferenceAuthorizer Bind(TenantRuntimeFrame frame) => new Bound(this, frame);
    private sealed class Bound(TenantArtifactReferenceService owner, TenantRuntimeFrame frame) : IArtifactReferenceAuthorizer
    {
        public bool IsAuthorized(Guid sessionId, string artifactId) => false;
        public ValueTask<IArtifactReferenceAuthorizer> PrepareAsync(Guid sessionId, IReadOnlyList<string> artifactIds, CancellationToken ct = default) =>
            owner.PrepareAsync(frame, sessionId, artifactIds, ct);
    }
    private async ValueTask<IArtifactReferenceAuthorizer> PrepareAsync(TenantRuntimeFrame frame, Guid sessionId, IReadOnlyList<string> ids, CancellationToken ct)
    {
        var allowed = new HashSet<Guid>();
        try
        {
            if (sessionId != frame.SessionId || frame.Owner.TenantId is not { } tenant) throw new AccessDeniedException("tenant_runtime_scope_mismatch");
            var reference = new TenantRunReference(tenant, frame.RunId);
            var (run, state) = await operations.ResolveForDecisionAsync(reference, "artifacts.read", ExecutionAuthorizationPurpose.Inspection, ct).ConfigureAwait(false);
            Require(run, frame);
            var session = await sessions.FindTenantSessionAsync(tenant, sessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            if (session.Owner.AgentInstanceId != run.AgentInstanceId || session.Owner.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId) throw new AccessDeniedException("resource_unavailable");
            var current = await configurations.ResolveAsync(tenant, run.AgentInstanceId, ct).ConfigureAwait(false);
            static bool Capability(AgentCore.Domain.Definitions.AgentDefinition definition) => TenantArtifactToolContract.Allows(definition) || TenantWorkspaceArtifactContract.Allows(definition) || RolePermissions.AllowsTool(definition, ToolCatalog.ArtifactsVerify) || TenantBrowserAuthorizationContract.Allowed(definition).Count > 0;
            if (!Capability(run.Admission.Configuration!.Definition) || !Capability(current.Configuration.Configuration.Definition)) throw new AccessDeniedException("capability_unavailable");
            if (!(await iam.CheckOwnedTenantAsync(state, run.Owner.ExecutorPrincipalId!.Value, PermissionCatalog.Get("sessions.read"), TenantSessionRules.Scope(tenant, sessionId), ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("executor_read_required");
            // A response is bounded independently of how many untrusted references a model proposes.
            foreach (var text in ids.Take(32).Distinct(StringComparer.Ordinal))
            {
                if (!Guid.TryParse(text, out var id) || id == Guid.Empty || await artifacts.FindTenantAsync(tenant, sessionId, id, ct).ConfigureAwait(false) is null) continue;
                var scope = TenantArtifactRules.Scope(tenant, id);
                if (!(await iam.CheckOwnedTenantAsync(state, run.Owner.ExecutorPrincipalId.Value, PermissionCatalog.Get("artifacts.read"), scope, ct).ConfigureAwait(false)).Allowed) continue;
                if ((await operations.Check(state, run, "artifacts.read", scope, ct, purpose: ExecutionAuthorizationPurpose.Inspection).ConfigureAwait(false)).Decision == OperationDecisionKind.Allowed) allowed.Add(id);
            }
            var (after, security) = await operations.ResolveAsync(reference, ct).ConfigureAwait(false); Require(after, frame);
            var final = await sessions.FindTenantSessionAsync(tenant, sessionId, ct).ConfigureAwait(false);
            if (security.Tenant.Revision != state.Tenant.Revision || final?.Owner != session.Owner || final.Snapshot.Revision != session.Snapshot.Revision) allowed.Clear();
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException) { allowed.Clear(); }
        return new PreparedArtifactReferences(sessionId, allowed);
    }
    private void Require(AgentRun run, TenantRuntimeFrame frame)
    {
        if (run.Owner != frame.Owner || run.SessionId != frame.SessionId || run.AgentRunId != frame.RunId
            || run.Admission.ExecutionContext?.Origin is not (ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation or ExecutionOrigin.ConfigurationContinuation)
            || run.Admission.OutputContract != AgentRunOutputContract.ConversationResponse) throw new AccessDeniedException("artifact_reference_unavailable");
        TenantLocalEffectRules.RequireDirectRun(run, frame.Generation, time.GetUtcNow());
    }
}
