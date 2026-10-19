using System.Text;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantResourceSourceObservation(Guid TenantId, Guid InstanceId, Guid SessionId, EffectiveAgentResource Resource,
    string DefinitionId, int DefinitionVersion, long ResourceRevision);

// Verified captured bytes are data, never a permission or reusable consent. No IAM callback is carried into storage.
public sealed class TenantResourceSource
{
    private readonly byte[] content;
    internal TenantResourceSource(TenantResourceSourceObservation observation, byte[] bytes) { Observation = observation; content = bytes.ToArray(); }
    public TenantResourceSourceObservation Observation { get; }
    public byte[] CopyContent() => content.ToArray();
}

// Resolve only an exact admitted resource through the actual current tenant/Instance binding.
// The public workspace projection and durable read consent remain separate consumers of this source boundary.
public sealed class TenantResourceSourceTarget(TenantOperationAuthorizer operations, ITenantSessionStore sessions,
    TenantRunConfigurationResolver configurations, ITenantResourceSourceContentStore content, IamAuthorizer iam, TimeProvider time)
{
    private sealed record Source(AgentRun Run, TenantSecurityState Security, TenantSession Session, TenantAgent Agent,
        TenantResourceSourceObservation Observation);
    public async ValueTask<TenantResourceSource> CaptureAsync(TenantRuntimeFrame frame, string tool, string path, CancellationToken ct = default)
    {
        ValidatePath(path);
        var before = await Require(frame, tool, path, ct).ConfigureAwait(false);
        var resource = before.Observation.Resource;
        DefinitionResourcePolicies.ValidateContentSize(resource.ByteLength);
        byte[] bytes;
        try
        {
            bytes = await content.ReadVerifiedResourceSourceAsync(resource.ContentSha256, resource.ByteLength, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("resource_content_unavailable");
            AgentInstanceResourceService.Verify(resource.ContentSha256, resource.ByteLength, bytes);
        }
        catch (SecurityConflictException e) when (e.ReasonCode == "workspace_source_changed")
        { throw new SecurityConflictException("resource_source_changed"); }
        catch (Exception e) when (e is AgentCore.Application.Sessions.AgentCoreException or IOException or UnauthorizedAccessException)
        { throw new AccessDeniedException("resource_content_unavailable"); }
        // Freeze before checking post-I/O authority; callers receive only a defensive copy of these verified bytes.
        var captured = new TenantResourceSource(before.Observation, bytes);
        var after = await Require(frame, tool, path, ct).ConfigureAwait(false);
        if (before.Run.Revision != after.Run.Revision || before.Security.Tenant.Revision != after.Security.Tenant.Revision
            || before.Agent.Instance.Revision != after.Agent.Instance.Revision || before.Session.Owner != after.Session.Owner
            || before.Session.Snapshot.Revision != after.Session.Snapshot.Revision || before.Observation != after.Observation)
            throw new SecurityConflictException("resource_source_changed");
        var final = await operations.ResolveAsync(new(after.Observation.TenantId, frame.RunId), ct).ConfigureAwait(false);
        TenantRuntimeRules.Require(frame, after.Session.Owner, final.Run, time.GetUtcNow());
        if (final.Run.Revision != after.Run.Revision || final.State.Tenant.Revision != after.Security.Tenant.Revision)
            throw new SecurityConflictException("resource_source_changed");
        ct.ThrowIfCancellationRequested();
        return captured;
    }
    private async ValueTask<Source> Require(TenantRuntimeFrame frame, string tool, string path, CancellationToken ct)
    {
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (run, state) = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
        if (run.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
            || run.Admission.ExecutionContext?.Origin is not (ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation)
            || session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active
            || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended) throw new AccessDeniedException("resource_unavailable");
        if (!TenantWorkspaceReadContract.IsFile(tool) || !TenantWorkspaceReadContract.Allows(run.Admission.Configuration!.Definition, tool))
            throw new AccessDeniedException("capability_unavailable");
        var admitted = run.Admission.Configuration.Resources.Where(r => r.VirtualPath == path).Take(2).ToArray();
        var resource = admitted.Length == 1 ? admitted[0] : throw new AccessDeniedException("resource_unavailable");
        var targets = new[] { ("sessions.read", TenantSessionRules.Scope(tenant, run.SessionId)),
            ("agents.read", AccessScope.ForResource(tenant, "agent", run.AgentInstanceId.ToString("D"))),
            ("resources.read", TenantAgentChildScopes.Scope(tenant, run.AgentInstanceId, "resource", resource.Key)) };
        foreach (var (permission, scope) in targets)
        {
            if (!(await iam.CheckOwnedTenantAsync(state, run.Owner.ExecutorPrincipalId!.Value, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("executor_read_required");
            var decision = await operations.Evaluate(state, run.Admission.ExecutionContext!, permission, scope, ct).ConfigureAwait(false);
            if (decision.Decision == OperationDecisionKind.RequiresApproval) throw new AccessDeniedException("resource_read_review_required");
            if (decision.Decision != OperationDecisionKind.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        var current = await configurations.CurrentResourcesAsync(tenant, run.AgentInstanceId, ct).ConfigureAwait(false);
        if (current.Snapshot.Agent.PrincipalId != run.Owner.ExecutorPrincipalId || !TenantWorkspaceReadContract.Allows(current.Definition, tool))
            throw new AccessDeniedException("capability_unavailable");
        var actual = current.Resources.SingleOrDefault(r => r.Key == resource.Key);
        if (actual != resource || resource.Key.StartsWith("definition:", StringComparison.Ordinal)
            && (current.Snapshot.Agent.Instance.DefinitionId != run.DefinitionId || current.Snapshot.Agent.Instance.ActiveVersion != run.DefinitionVersion))
            throw new SecurityConflictException("resource_source_changed");
        var revision = resource.Key.StartsWith("instance:", StringComparison.Ordinal)
            ? current.Snapshot.Resources.InstanceResources.Single(r => "instance:" + r.ResourceId.ToString("D") == resource.Key).Revision
            : current.Snapshot.Resources.DefinitionStates.SingleOrDefault(r => "definition:" + r.ResourceId.ToString("D") == resource.Key)?.Revision ?? 0;
        return new(run, state, session, current.Snapshot.Agent,
            new(tenant, run.AgentInstanceId, run.SessionId, resource, run.DefinitionId, run.DefinitionVersion, revision));
    }
    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Encoding.UTF8.GetByteCount(path) > 4095 || path.Contains('\\') || path.Any(char.IsControl)
            || path.IndexOfAny(['*', '?', '[', ']']) >= 0 || path.Split('/').Skip(1).Any(p => p.Length == 0 || p is "." or ".." || Encoding.UTF8.GetByteCount(p) > 255)
            || !path.StartsWith("/agent/resources/", StringComparison.Ordinal) && !path.StartsWith("/agent/instance/resources/", StringComparison.Ordinal))
            throw new ArgumentException("Use an exact admitted resource file path.");
    }
}
