using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantArtifactMaterialization(string DisplayName, string ContentType, byte[] Bytes);

// The incumbent Artifact owner combines this storage evidence with current IAM, Run and consent fences.
public static class TenantArtifactSourceRules
{
    public static PreparedArtifactSource Witness(TenantWorkspaceArtifactObservation value) => new(value.Owner.SessionId is not null,
        value.Path, value.ContentType, value.ByteSize, value.Sha256Hex, value.ObservationHash, value.ResourceKey, value.ResourceRevision, value.AttachmentId, value.EntryId, value.AttachmentName);
    public static void ValidateWitness(PreparedArtifactSource value, string path)
    {
        if (TenantAttachmentArtifactRules.IsPath(path))
        {
            if (value.AttachmentId != TenantAttachmentArtifactRules.Id(path) || value.EntryId is null || value.EntryId == Guid.Empty
                || string.IsNullOrWhiteSpace(value.AttachmentName) || value.AttachmentName.Length > 200
                || !value.Working || value.ResourceKey is not null || value.ResourceRevision is not null || value.ByteSize > AttachmentLimits.MaxBytesEach)
                throw new ArgumentException("Use an exact bound Attachment source witness.");
        }
        else if (value.AttachmentId is not null || value.EntryId is not null || value.AttachmentName is not null) throw new ArgumentException("Unexpected Attachment source witness.");
        if (TenantWorkspaceReadContract.IsResourcePath(path))
        {
            TenantResourceSourceTarget.ValidatePath(path);
            if (value.Working || value.ResourceKey is not { } key || value.ResourceRevision is null or < 0
                || !TenantAgentChildScopes.IsResourceKey(key))
                throw new ArgumentException("Use an exact resource source witness.");
        }
        else if (value.ResourceKey is not null || value.ResourceRevision is not null) throw new ArgumentException("Unexpected resource source witness.");
        if (value.Path != path || value.Working != SessionPath(path) || value.ByteSize < 0
            || value.ByteSize > Math.Min(AgentWorkspaceLimits.MaxFileBytes, ArtifactLimits.MaxBytesEach)
            || !Hex(value.Sha256Hex) || !Hex(value.ObservationHash) || string.IsNullOrWhiteSpace(value.ContentType) || value.ContentType.Length > 200
            || value.ContentType.Any(char.IsControl) || !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(value.ContentType, out _))
            throw new ArgumentException("Invalid exact Artifact source.");
    }
    private static bool Hex(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static TenantWorkspaceReference Owner(AgentRun run, string path) => new(run.Owner.TenantId!.Value, run.AgentInstanceId,
        SessionPath(path) ? run.SessionId : null);
    private static bool SessionPath(string path) => path.StartsWith("/working/", StringComparison.Ordinal) || TenantAttachmentArtifactRules.IsPath(path);
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(AgentRun run, string path, string? resourceKey = null)
    {
        if (TenantWorkspaceReadContract.IsResourcePath(path))
            resourceKey ??= run.Admission.Configuration!.Resources.SingleOrDefault(r => r.VirtualPath == path)?.Key ?? throw new AccessDeniedException("resource_unavailable");
        return Targets(run.Owner.TenantId!.Value, run.AgentInstanceId, run.SessionId, SessionPath(path), resourceKey, TenantAttachmentArtifactRules.IsPath(path) ? TenantAttachmentArtifactRules.Id(path) : null);
    }
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(Guid tenant, Guid instance, Guid session, bool working, string? resourceKey = null, Guid? attachmentId = null)
    {
        yield return ("sessions.read", TenantSessionRules.Scope(tenant, session));
        if (attachmentId is { } id) { yield return ("attachments.read", TenantAttachmentRules.Scope(tenant, id)); yield break; }
        var owner = new TenantWorkspaceReference(tenant, instance, working ? session : null);
        if (owner.SessionId is null) yield return ("agents.read", AccessScope.ForResource(owner.TenantId, "agent", owner.InstanceId.ToString("D")));
        yield return resourceKey is null ? ("workspaces.read", TenantWorkspaceRules.Scope(owner))
            : ("resources.read", TenantAgentChildScopes.Scope(tenant, instance, "resource", resourceKey));
    }
    public static void Require(AgentRun run, TenantAgent agent, AgentDefinition current, TenantSession session, string path, PreparedArtifactSource? source = null)
    {
        TenantWorkspaceEffectRules.Session(run, agent, session);
        if (TenantWorkspaceReadContract.IsResourcePath(path))
        {
            TenantResourceSourceTarget.ValidatePath(path);
            var pinned = run.Admission.Configuration!.Resources.SingleOrDefault(r => r.VirtualPath == path) ?? throw new AccessDeniedException("resource_unavailable");
            if (source is not null && (source.ResourceKey != pinned.Key || source.ResourceRevision is null or < 0 || source.Sha256Hex != pinned.ContentSha256
                || source.ByteSize != pinned.ByteLength || source.ContentType != pinned.MediaType)) throw new SecurityConflictException("resource_source_changed");
        }
        else if (TenantAttachmentArtifactRules.IsPath(path))
        {
            var id = TenantAttachmentArtifactRules.Id(path);
            if (source is not null && source.AttachmentId != id) throw new SecurityConflictException("attachment_source_changed");
        }
        else TenantWorkspaceRules.Path(Owner(run, path), path, file: true);
        current = InstanceSettingsResolver.Resolve(current, agent.Instance.SettingsOverrides);
        if (run.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
            || run.Admission.ExecutionContext?.Origin is not (ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation))
            throw new AccessDeniedException("artifact_direct_chat_required");
        if (!TenantWorkspaceArtifactContract.Allows(run.Admission.Configuration!.Definition) || !TenantWorkspaceArtifactContract.Allows(current))
            throw new AccessDeniedException("capability_unavailable");
        var isolation = StateIsolationAccess.RestrictDefinition(run.Admission.Configuration.Definition, current).StateIsolationPolicy ?? StateIsolationPolicy.Default;
        if (!TenantWorkspaceReadContract.IsResourcePath(path) && !TenantAttachmentArtifactRules.IsPath(path)) TenantWorkspaceEffectRules.RequireHome(isolation, path, false);
    }
    public static async ValueTask RequireAuthorityAsync(TenantSecurityState state, AgentRun run, string path, IamAuthorizer iam, bool approved, CancellationToken ct, bool ownedSources = false)
    {
        foreach (var (key, scope) in Targets(run, path))
        {
            if (!(await iam.CheckOwnedTenantAsync(state, run.Owner.ExecutorPrincipalId!.Value, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("executor_read_required");
            if (!approved && await NeedsReviewAsync(state, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, key, scope, iam, ct, ownedSources).ConfigureAwait(false))
                throw new AccessDeniedException("artifact_source_review_required");
        }
    }
    internal static async ValueTask<bool> NeedsReviewAsync(TenantSecurityState state, Guid? requester, string key, AccessScope scope,
        IamAuthorizer iam, CancellationToken ct, bool ownedSources)
    {
        var permission = PermissionCatalog.Get(key); var policy = TenantOperationPolicyService.Effective(state, permission);
        return policy.Mode == OperationApprovalMode.Always || policy.Mode == OperationApprovalMode.WhenRequesterLacksPermission && requester is { } human
            && !(await (ownedSources ? iam.CheckOwnedTenantAsync(state, human, permission, scope, ct) : iam.CheckTenantAsync(state, human, permission, scope, ct)).ConfigureAwait(false)).Allowed;
    }
    public static async ValueTask<TenantArtifactMaterialization> MaterializeAsync(AgentRun run, PreparedArtifactPublication publication,
        ITenantWorkspaceArtifactSource? source, CancellationToken ct)
    {
        var args = TenantArtifactToolContract.ParsePublication(publication);
        if (publication.Source is not { } saved)
        {
            if (source is not null) throw new SecurityConflictException("operation_mismatch");
            return new(args.DisplayName, args.ContentType!, Encoding.UTF8.GetBytes(args.Content!));
        }
        if (source is null || source.Observation.Owner != Owner(run, args.SourcePath!) || Witness(source.Observation) != saved)
            throw new SecurityConflictException("workspace_source_changed");
        await source.RequireCurrentAsync(ct).ConfigureAwait(false);
        var bytes = source.CopyContent();
        if (bytes.LongLength != saved.ByteSize || Convert.ToHexStringLower(SHA256.HashData(bytes)) != saved.Sha256Hex)
            throw new SecurityConflictException("workspace_source_changed");
        await source.RequireCurrentAsync(ct).ConfigureAwait(false);
        return new(args.DisplayName, args.ContentType ?? saved.ContentType, bytes);
    }
}
