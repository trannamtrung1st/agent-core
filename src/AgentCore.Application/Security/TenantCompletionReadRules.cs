using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantCompletionReadRules
{
    public const string Reason = "completion_read_authorized";
    public const string AuditAction = "operations.background.completion.read";
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static IReadOnlyList<PreparedArtifactVerification> Artifacts(PreparedCompletionRead read)
    {
        if (read.ArtifactsJson.Length > 8192) throw new ArgumentException("Completion metadata exceeds its bound.");
        var files = JsonSerializer.Deserialize<PreparedArtifactVerification[]>(read.ArtifactsJson) ?? throw new ArgumentException("Completion metadata is required.");
        if (files.Length > 3 || files.Select(a => a.ArtifactId).Distinct().Count() != files.Length || files.Any(a => a.ArtifactId == Guid.Empty
            || a.SessionId != read.ChildSessionId || a.ByteSize < 0 || a.DisplayName.Length is < 1 or > 200 || a.ContentType.Length is < 1 or > 200
            || a.Sha256Hex.Length != 64 || a.Sha256Hex.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))))
            throw new ArgumentException("Invalid exact completion metadata.");
        return files;
    }
    public static PreparedTenantOperation Lookup(AgentRun parent, BackgroundCompletionCandidate child) =>
        new(parent.AgentRunId, parent.AgentInstanceId, null, 1, 1, "", "", CompletionRead: Witness(parent, child, []));
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(Guid tenant, PreparedCompletionRead read) =>
        new[] { ("sessions.read", TenantSessionRules.Scope(tenant, read.ChildSessionId)),
            ("agent-runs.read", TenantRunRules.Scope(tenant, read.InitialRunId)), ("agent-runs.read", TenantRunRules.Scope(tenant, read.SourceRunId)) }
        .Concat(TenantCompletionReadRules.Artifacts(read).Select(a => ("artifacts.read", TenantArtifactRules.Scope(tenant, a.ArtifactId)))).Distinct();

    public static PreparedCompletionRead Witness(AgentRun parent, BackgroundCompletionCandidate child, IReadOnlyList<ArtifactRecord> artifacts)
    {
        var source = child.CurrentTaskRun ?? child.Run;
        if (!source.IsTerminal || source.Result?.OutcomeKind == AgentRunOutcomeKind.Continued) throw new AccessDeniedException("completion_not_ready");
        var files = artifacts.Take(3).ToArray();
        var summary = source.Result?.Text ?? source.Failure?.Summary ?? (source.Status == AgentRunStatus.Cancelled ? "Background work was cancelled." : null);
        var json = JsonSerializer.Serialize(new { untrusted = true, tenantCompletionRead = true, backgroundSessionId = source.SessionId,
            parentSessionId = parent.SessionId, agentRunId = child.Run.AgentRunId, sourceAgentRunId = source.AgentRunId, status = source.Status.ToString(),
            outcome = source.Result?.OutcomeKind.ToString(), summary = summary is null ? null : ToolJsonResults.ClipUtf8Prefix(summary, 2400),
            updatedAtUtc = source.UpdatedAtUtc, progress = source.Progress?.Summary is { } progress ? ToolJsonResults.ClipUtf8Prefix(progress, 320) : null,
            attentionRequired = source.Result?.AttentionRequired == true || source.Status == AgentRunStatus.Failed, failureCode = source.Failure?.Code,
            artifacts = files.Take(2).Select(a => new { artifactId = a.ArtifactId, name = ToolJsonResults.ClipUtf8Prefix(a.DisplayName, 160),
                contentType = ToolJsonResults.ClipUtf8Prefix(a.ContentType, 80), a.ByteSize }), artifactsHasMore = files.Length > 2 });
        return new(parent.SessionId, source.SessionId, child.Run.AgentRunId, source.AgentRunId, source.Revision,
            ToolJsonResults.ClipUtf8Prefix(child.Run.Admission.TaskIntent?.Text ?? "Background task", 4000), json, Hash(json),
            JsonSerializer.Serialize(files.Select(TenantArtifactVerificationRules.Witness).ToArray()));
    }
    public static bool Same(PreparedCompletionRead left, PreparedCompletionRead right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
    public static void Require(PreparedCompletionRead read, AgentRun parent, TenantAgent agent, AgentDefinition current, BackgroundCompletionCandidate child)
    {
        var source = child.CurrentTaskRun ?? child.Run;
        if (!TenantWaitRules.Allows(parent) || !RolePermissions.AllowsTool(parent.Admission.Configuration!.Definition, ToolCatalog.BackgroundInspect)
            || !RolePermissions.AllowsTool(current, ToolCatalog.BackgroundInspect) || parent.Owner != child.Run.Owner || source.Owner != parent.Owner
            || read.ParentSessionId != parent.SessionId || read.ChildSessionId != source.SessionId || read.InitialRunId != child.Run.AgentRunId
            || read.SourceRunId != source.AgentRunId || read.SourceRevision != source.Revision || !source.IsTerminal || source.Result?.OutcomeKind == AgentRunOutcomeKind.Continued
            || agent.Instance.InstanceId != parent.AgentInstanceId || agent.PrincipalId != parent.Owner.ExecutorPrincipalId
            || Hash(read.ObservationJson) != read.ObservationHash || TenantCompletionReadRules.Artifacts(read).Count > 3)
            throw new AccessDeniedException("completion_source_changed");
    }
    public static async ValueTask PermissionsAsync(TenantSecurityState state, PreparedCompletionRead read, Guid principal, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(state.Tenant.TenantId, read))
            if (!(await iam.CheckOwnedTenantAsync(state, principal, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("executor_read_required");
    }
    public static async ValueTask<bool> NeedsReviewAsync(TenantSecurityState state, PreparedCompletionRead read, Guid? requester, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(state.Tenant.TenantId, read))
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, key, scope, iam, ct).ConfigureAwait(false)) return true;
        return false;
    }
    public static async ValueTask<bool> IndependentAsync(TenantSecurityState state, PreparedCompletionRead read, Guid? requester, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(state.Tenant.TenantId, read))
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview
                && await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, key, scope, iam, ct).ConfigureAwait(false)) return true;
        return false;
    }
    public static async ValueTask ReviewerAsync(TenantSecurityState state, PreparedCompletionRead read, Guid? requester, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        if (!state.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("approver_unqualified");
        await PermissionsAsync(state, read, human, iam, ct).ConfigureAwait(false);
        foreach (var (key, scope) in Targets(state.Tenant.TenantId, read))
        {
            if (key != "agent-runs.read" && !await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, key, scope, iam, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview && requester == human)
                throw new AccessDeniedException("independent_reviewer_required");
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        }
    }
    public static void ValidateEffect(TenantApprovalRequest request, TenantApprovalDecision decision, TenantOperationGrant? grant,
        AgentRun run, TenantOperationCommit commit, AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request, decision, grant, run, commit, source);
        if (request.Operation.CompletionRead is null || request.Kind != ApprovalKind.Review || grant is not null
            || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || commit.Outcome == TenantOperationOutcome.Completed && commit.ReasonCode != Reason
            || commit.ReasonCode.Length is < 1 or > 128 || commit.ReasonCode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new SecurityConflictException("operation_mismatch");
    }
}
