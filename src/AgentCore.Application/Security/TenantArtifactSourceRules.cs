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
        value.Path, value.ContentType, value.ByteSize, value.Sha256Hex, value.ObservationHash);
    public static void ValidateWitness(PreparedArtifactSource value, string path)
    {
        if (value.Path != path || value.Working != path.StartsWith("/working/", StringComparison.Ordinal) || value.ByteSize < 0
            || value.ByteSize > Math.Min(AgentWorkspaceLimits.MaxFileBytes, ArtifactLimits.MaxBytesEach)
            || !Hex(value.Sha256Hex) || !Hex(value.ObservationHash) || string.IsNullOrWhiteSpace(value.ContentType) || value.ContentType.Length > 200
            || value.ContentType.Any(char.IsControl) || !System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(value.ContentType, out _))
            throw new ArgumentException("Invalid exact Artifact source.");
    }
    private static bool Hex(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static TenantWorkspaceReference Owner(AgentRun run, string path) => new(run.Owner.TenantId!.Value, run.AgentInstanceId,
        path.StartsWith("/working/", StringComparison.Ordinal) ? run.SessionId : null);
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(AgentRun run, string path)
    {
        yield return ("sessions.read", TenantSessionRules.Scope(run.Owner.TenantId!.Value, run.SessionId));
        var owner = Owner(run, path);
        if (owner.SessionId is null) yield return ("agents.read", AccessScope.ForResource(owner.TenantId, "agent", owner.InstanceId.ToString("D")));
        yield return ("workspaces.read", TenantWorkspaceRules.Scope(owner));
    }
    public static void Require(AgentRun run, TenantAgent agent, AgentDefinition current, TenantSession session, string path)
    {
        TenantWorkspaceEffectRules.Session(run, agent, session);
        TenantWorkspaceRules.Path(Owner(run, path), path, file: true);
        current = InstanceSettingsResolver.Resolve(current, agent.Instance.SettingsOverrides);
        if (run.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
            || run.Admission.ExecutionContext?.Origin is not (ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation))
            throw new AccessDeniedException("artifact_direct_chat_required");
        if (!TenantWorkspaceArtifactContract.Allows(run.Admission.Configuration!.Definition) || !TenantWorkspaceArtifactContract.Allows(current))
            throw new AccessDeniedException("capability_unavailable");
        var isolation = StateIsolationAccess.RestrictDefinition(run.Admission.Configuration.Definition, current).StateIsolationPolicy ?? StateIsolationPolicy.Default;
        TenantWorkspaceEffectRules.RequireHome(isolation, path, false);
    }
    public static async ValueTask RequireAuthorityAsync(TenantSecurityState state, AgentRun run, string path, IamAuthorizer iam, bool approved, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(run, path))
        {
            if (!(await iam.CheckOwnedTenantAsync(state, run.Owner.ExecutorPrincipalId!.Value, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("executor_read_required");
            if (!approved && await TenantSandboxReadOperationRules.NeedsReviewAsync(state, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false))
                throw new AccessDeniedException("artifact_source_review_required");
        }
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
