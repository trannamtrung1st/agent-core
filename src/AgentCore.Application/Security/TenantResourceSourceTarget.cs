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
// Workspace readers and durable read consent consume this source without taking over its authority checks.
public sealed class TenantResourceSourceTarget(TenantOperationAuthorizer operations, ITenantSessionStore sessions,
    TenantRunConfigurationResolver configurations, ITenantResourceSourceContentStore content, IamAuthorizer iam, TimeProvider time, ITenantManagementStore? management = null)
{
    internal sealed record Source(AgentRun Run, TenantSecurityState Security, TenantSession Session, TenantAgent Agent,
        TenantResourceSourceObservation Observation);
    public ValueTask<TenantResourceSource> CaptureAsync(TenantRuntimeFrame frame, string tool, string path, CancellationToken ct = default) =>
        CaptureForInspectionAsync(frame, tool, path, false, true, ct);
    internal ValueTask<TenantResourceSource> CaptureForInspectionAsync(TenantRuntimeFrame frame, string tool, string path, bool approved, bool live, CancellationToken ct) =>
        CaptureCore(frame, tool, path, approved, live, false, ct);
    internal ValueTask<TenantResourceSource> CaptureForSearchAsync(TenantRuntimeFrame frame, string path, bool approved, bool live, CancellationToken ct) =>
        CaptureCore(frame, ToolCatalog.WorkspaceSearch, path, approved, live, true, ct);
    internal ValueTask<TenantResourceSource> CaptureForArtifactAsync(TenantRuntimeFrame frame, string path, bool approved, bool live, CancellationToken ct) =>
        CaptureCore(frame, ToolCatalog.ArtifactsCreateFromWorkspace, path, approved, live, false, ct, publication: true);
    internal async ValueTask<TenantResourceSource> CaptureKnowledgeAsync(TenantRuntimeFrame frame, string identity, bool approved, bool live, CancellationToken ct)
    {
        var (run, _) = await operations.ResolveAsync(new(frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required"), frame.RunId), ct, retained: !live).ConfigureAwait(false);
        var selected = TenantKnowledgeReadContract.Resolve(run.Admission.Configuration!.Definition, run.Admission.Configuration.Resources, identity);
        return await CaptureCore(frame, ToolCatalog.KnowledgeRetrieve, selected.Resource.VirtualPath, approved, live, false, ct, identity).ConfigureAwait(false);
    }
    internal async ValueTask<Source> RequireKnowledgeMetadataAsync(TenantRuntimeFrame frame, string identity, CancellationToken ct, bool approved, bool live, bool audit)
    {
        var (run, _) = await operations.ResolveAsync(new(frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required"), frame.RunId), ct, retained: !live).ConfigureAwait(false);
        var selected = TenantKnowledgeReadContract.Resolve(run.Admission.Configuration!.Definition, run.Admission.Configuration.Resources, identity);
        return await RequireMetadataAsync(frame, ToolCatalog.KnowledgeRetrieve, selected.Resource.VirtualPath, ct, approved, live, audit, knowledgeIdentity: identity).ConfigureAwait(false);
    }
    private async ValueTask<TenantResourceSource> CaptureCore(TenantRuntimeFrame frame, string tool, string path, bool approved, bool live, bool search, CancellationToken ct, string? knowledgeIdentity = null, bool publication = false)
    {
        ValidatePath(path);
        var before = await RequireMetadataAsync(frame, tool, path, ct, approved, live, search: search, knowledgeIdentity: knowledgeIdentity, publication: publication).ConfigureAwait(false);
        var resource = before.Observation.Resource;
        DefinitionResourcePolicies.ValidateContentSize(resource.ByteLength);
        byte[] bytes;
        try
        {
            bytes = await content.ReadVerifiedResourceSourceAsync(resource.ContentSha256, resource.ByteLength, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("resource_content_unavailable");
            InstanceResourceRules.Verify(resource.ContentSha256, resource.ByteLength, bytes);
        }
        catch (SecurityConflictException e) when (e.ReasonCode == "workspace_source_changed")
        { throw new SecurityConflictException("resource_source_changed"); }
        catch (Exception e) when (e is AgentCore.Application.Sessions.AgentCoreException or IOException or UnauthorizedAccessException)
        { throw new AccessDeniedException("resource_content_unavailable"); }
        // Freeze before checking post-I/O authority; callers receive only a defensive copy of these verified bytes.
        var captured = new TenantResourceSource(before.Observation, bytes);
        var after = await RequireMetadataAsync(frame, tool, path, ct, approved, live, search: search, knowledgeIdentity: knowledgeIdentity, publication: publication).ConfigureAwait(false);
        // Persisting the same claim's ordinary response does not replace its pinned resource.
        // RequireMetadata repeats the current Session owner/lifecycle and source authority checks.
        if (before.Run.Revision != after.Run.Revision || before.Security.Tenant.Revision != after.Security.Tenant.Revision
            || before.Agent.Instance.Revision != after.Agent.Instance.Revision || before.Session.Owner != after.Session.Owner
            || before.Observation != after.Observation)
            throw new SecurityConflictException("resource_source_changed");
        var final = await operations.ResolveAsync(new(after.Observation.TenantId, frame.RunId), ct, retained: !live).ConfigureAwait(false);
        if (live) TenantRuntimeRules.Require(frame, after.Session.Owner, final.Run, time.GetUtcNow());
        if (final.Run.Revision != after.Run.Revision || final.State.Tenant.Revision != after.Security.Tenant.Revision)
            throw new SecurityConflictException("resource_source_changed");
        ct.ThrowIfCancellationRequested();
        return captured;
    }
    internal async ValueTask<Source> RequireMetadataAsync(TenantRuntimeFrame frame, string tool, string path, CancellationToken ct, bool approved = false, bool live = true, bool audit = false, bool search = false, string? knowledgeIdentity = null, bool publication = false)
    {
        ValidatePath(path);
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (run, state) = await operations.ResolveAsync(new(tenant, frame.RunId), ct, retained: !live).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (live) TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
        if (run.Owner != frame.Owner || run.SessionId != frame.SessionId || session.Owner.AgentInstanceId != run.AgentInstanceId || session.Owner.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId)
            throw new AccessDeniedException("resource_unavailable");
        if (!TenantTaskExecutionContract.Allows(run, run.Admission.Configuration!.Definition)
            || session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active
            || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended) throw new AccessDeniedException("resource_unavailable");
        if (tool == ToolCatalog.KnowledgeRetrieve && knowledgeIdentity is null) throw new AccessDeniedException("knowledge_source_unavailable");
        bool Allows(AgentDefinition definition) => publication && tool == ToolCatalog.ArtifactsCreateFromWorkspace
            ? TenantWorkspaceArtifactContract.Allows(definition) : !publication && TenantWorkspaceReadContract.Allows(definition, tool);
        if (!(TenantWorkspaceReadContract.IsFile(tool) || search && tool == ToolCatalog.WorkspaceSearch || publication && tool == ToolCatalog.ArtifactsCreateFromWorkspace) || !Allows(run.Admission.Configuration!.Definition))
            throw new AccessDeniedException("capability_unavailable");
        var admitted = run.Admission.Configuration.Resources.Where(r => r.VirtualPath == path).Take(2).ToArray();
        var resource = admitted.Length == 1 ? admitted[0] : throw new AccessDeniedException("resource_unavailable");
        var current = await configurations.CurrentResourcesForRunAsync(run, ct).ConfigureAwait(false);
        var retained = resource.SpaceSource is not null || management is null ? null : await management.ReadRetainedResourcePinAsync(new(tenant, run.AgentRunId), resource, ct).ConfigureAwait(false);
        if (retained is not null && retained.InstanceRevision != current.Snapshot.Agent.Instance.Revision) throw new SecurityConflictException("resource_source_changed");
        var targets = new[] { ("sessions.read", TenantSessionRules.Scope(tenant, run.SessionId)),
            ("agents.read", AccessScope.ForResource(tenant, "agent", run.AgentInstanceId.ToString("D"))),
            ("resources.read", retained?.Deleted == true ? AccessScope.ForTenant(tenant) : TenantResourceScopes.Source(tenant, run.AgentInstanceId, resource.Key)) };
        foreach (var (permission, scope) in targets)
        {
            if (!(await iam.CheckOwnedTenantAsync(state, run.Owner.ExecutorPrincipalId!.Value, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("executor_read_required");
            if (approved) continue;
            var decision = audit ? await operations.Check(state, run, permission, scope, ct, purpose: ExecutionAuthorizationPurpose.ToolBoundary).ConfigureAwait(false)
                : await operations.Evaluate(state, run.Admission.ExecutionContext!, permission, scope, ct).ConfigureAwait(false);
            if (decision.Decision == OperationDecisionKind.RequiresApproval) throw new AccessDeniedException("resource_read_review_required");
            if (decision.Decision != OperationDecisionKind.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        if (current.Snapshot.Agent.PrincipalId != run.Owner.ExecutorPrincipalId || !TenantTaskExecutionContract.Allows(run, current.Definition) || !Allows(current.Definition))
            throw new AccessDeniedException("capability_unavailable");
        if (tool == ToolCatalog.KnowledgeRetrieve)
        {
            var pinned = TenantKnowledgeReadContract.Resolve(run.Admission.Configuration.Definition, run.Admission.Configuration.Resources, knowledgeIdentity!);
            var liveSource = TenantKnowledgeReadContract.Resolve(current.Definition, retained is null ? current.Resources : run.Admission.Configuration.Resources, knowledgeIdentity!);
            if (pinned.Resource != resource || liveSource.Resource != resource || pinned.Source != liveSource.Source) throw new SecurityConflictException("knowledge_source_changed");
        }
        var actual = current.Resources.SingleOrDefault(r => r.Key == resource.Key);
        if (retained is null && actual != resource || resource.Key.StartsWith("definition:", StringComparison.Ordinal)
            && (current.Snapshot.Agent.Instance.DefinitionId != run.DefinitionId || current.Snapshot.Agent.Instance.ActiveVersion != run.DefinitionVersion))
            throw new SecurityConflictException("resource_source_changed");
        var revision = resource.SpaceSource?.Revision ?? retained?.ResourceRevision ?? (resource.Key.StartsWith("instance:", StringComparison.Ordinal)
            ? current.Snapshot.Resources.InstanceResources.Single(r => "instance:" + r.ResourceId.ToString("D") == resource.Key).Revision
            : current.Snapshot.Resources.DefinitionStates.SingleOrDefault(r => "definition:" + r.ResourceId.ToString("D") == resource.Key)?.Revision ?? 0);
        return new(run, state, session, current.Snapshot.Agent,
            new(tenant, run.AgentInstanceId, run.SessionId, resource, run.DefinitionId, run.DefinitionVersion, revision));
    }
    internal static void ValidatePath(string path, bool collection = false)
    {
        // Virtual paths match manifest entries literally; only their verified digest reaches the filesystem.
        if (string.IsNullOrWhiteSpace(path) || Encoding.UTF8.GetByteCount(path) > 4095 || path.Contains('\\') || path.Any(char.IsControl)
            || path.Split('/').Skip(1).Any(p => p.Length == 0 || p is "." or "..")
            || !(collection && path is "/agent/resources" or "/agent/instance/resources" or "/space/resources") && !path.StartsWith("/agent/resources/", StringComparison.Ordinal) && !path.StartsWith("/agent/instance/resources/", StringComparison.Ordinal) && !path.StartsWith("/space/resources/", StringComparison.Ordinal))
            throw new ArgumentException("Use an exact admitted resource file path.");
    }
}
