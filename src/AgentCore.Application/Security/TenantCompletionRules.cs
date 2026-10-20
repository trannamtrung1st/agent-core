using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantCompletionRules
{
    public static void Require(AgentRun parent, AgentDefinition current, string tool)
    {
        if (!TenantWaitRules.Allows(parent) || !TenantCompletionToolContract.IsTool(tool)
            || !RolePermissions.AllowsTool(parent.Admission.Configuration!.Definition, tool) || !RolePermissions.AllowsTool(current, tool))
            throw new AccessDeniedException("completion_unavailable");
    }

    public static TenantCompletionPage Page(IReadOnlyList<TenantCompletionView> visible, Guid? childSession,
        Guid? cursor, bool pendingOnly, int limit)
    {
        if (limit is < 1 or > 20 || childSession == Guid.Empty || cursor == Guid.Empty) throw new ArgumentException("Use bounded completion arguments.");
        var ordered = visible.OrderByDescending(v => v.Source.Run.CreatedAtUtc).ThenByDescending(v => v.Source.Run.AgentRunId).ToArray();
        if (childSession is { } session)
        {
            var view = ordered.SingleOrDefault(v => v.Source.Run.SessionId == session) ?? throw AgentCoreErrors.NotFound("Owned readable background task was not found.");
            return new([view], false, null, 0, false);
        }
        var count = ordered.Count(v => v.Inbox is { Status: CompletionInboxStatus.Pending });
        if (cursor is { } id)
        {
            var offset = Array.FindIndex(ordered, v => v.Source.Run.AgentRunId == id);
            if (offset < 0) throw AgentCoreErrors.NotFound("Owned readable completion cursor was not found.");
            ordered = ordered.Skip(offset + 1).ToArray();
        }
        if (pendingOnly) ordered = ordered.Where(v => v.Inbox is { Status: CompletionInboxStatus.Pending }).ToArray();
        var page = ordered.Take(limit).ToArray(); var more = ordered.Length > limit;
        return new(page, more, more ? page[^1].Source.Run.AgentRunId : null, Math.Min(count, AgentRunLimits.MaxListLimit), count >= AgentRunLimits.MaxListLimit);
    }

    public static void RequirePendingMutation(AgentRun parent, Guid session, long revision, string callId, Guid token, string? usage)
    {
        if (!AgentRunToolCallCheckpoint.TryRead(parent.Checkpoint, out var messages)) throw AgentCoreErrors.Conflict("Completion requires its pending call.");
        var tool = usage is null ? ToolCatalog.BackgroundTake : ToolCatalog.BackgroundAcknowledge;
        var call = AgentRunToolCallCheckpoint.PendingCalls(messages!).SingleOrDefault(c => c.Id == callId && c.Name == tool)
            ?? throw AgentCoreErrors.Conflict("Completion call is not pending.");
        using var json = JsonDocument.Parse(call.ArgumentsJson); var args = json.RootElement;
        var names = args.EnumerateObject().Select(p => p.Name).ToArray();
        var allowed = usage is null ? new[] { "backgroundSessionId", "revision" } : new[] { "backgroundSessionId", "revision", "token", "usage" };
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Any(n => !allowed.Contains(n))
            || args.GetProperty("backgroundSessionId").GetGuid() != session || args.GetProperty("revision").GetInt64() != revision
            || usage is not null && (args.GetProperty("token").GetGuid() != token || args.GetProperty("usage").GetString() != usage))
            throw AgentCoreErrors.Conflict("Completion differs from its pending call.");
    }

    public static CompletionInboxItem Mutate(TenantCompletionView view, AgentRun parent, long revision,
        string call, Guid token, string? usage, DateTimeOffset now)
    {
        var item = view.Inbox ?? throw AgentCoreErrors.NotFound("Reportable terminal completion was not found.");
        try { return usage is null ? item.Take(parent, revision, call, token, now) : item.Acknowledge(parent, revision, token, usage, now); }
        catch (AgentRunTransitionException) { throw AgentCoreErrors.Conflict("Completion revision or claim is stale."); }
    }

    public static async ValueTask<bool> CanReadPermissionAsync(TenantSecurityState state, AgentRun parent, string key, AccessScope scope,
        IamAuthorizer iam, CancellationToken ct)
    {
        var permission = PermissionCatalog.Get(key);
        if (!(await iam.CheckOwnedTenantAsync(state, parent.Owner.ExecutorPrincipalId!.Value, permission, scope, ct).ConfigureAwait(false)).Allowed) return false;
        var policy = TenantOperationPolicyService.Effective(state, permission);
        return policy.Mode != OperationApprovalMode.Always && (policy.Mode != OperationApprovalMode.WhenRequesterLacksPermission
            || parent.Admission.ExecutionContext?.OriginalInitiatorPrincipalId is not { } human
            || (await iam.CheckOwnedTenantAsync(state, human, permission, scope, ct).ConfigureAwait(false)).Allowed);
    }

    // Metadata only, independently filtered before the two-link page boundary; no artifact bytes.
    public static async ValueTask<IReadOnlyList<ArtifactRecord>> ArtifactsAsync(ITenantArtifactStore? artifacts,
        TenantSecurityState state, AgentRun parent, BackgroundCompletionCandidate source, IamAuthorizer iam, CancellationToken ct)
    {
        if (source.CompletionReadRequestId is not null) return source.ReviewedArtifacts ?? throw new AccessDeniedException("completion_source_unavailable");
        if (artifacts is null) return [];
        var run = source.CurrentTaskRun ?? source.Run; var result = new List<ArtifactRecord>(); Guid? cursor = null;
        do
        {
            var page = await artifacts.ListTenantAsync(state.Tenant.TenantId, run.SessionId, null, cursor, 20, run.AgentRunId, ct).ConfigureAwait(false);
            foreach (var item in page.Items)
                if (item.TenantId == state.Tenant.TenantId && item.SessionId == run.SessionId && item.AgentRunId == run.AgentRunId
                    && await CanReadPermissionAsync(state, parent, "artifacts.read", TenantArtifactRules.Scope(state.Tenant.TenantId, item.ArtifactId), iam, ct).ConfigureAwait(false))
                { result.Add(item); if (result.Count == 3) return result; }
            cursor = page.HasMore ? page.NextCursor : null;
        } while (cursor is not null);
        return result;
    }

    public sealed record Source(Guid SessionId, Guid RunId, IReadOnlyList<Guid> Artifacts, Guid? ReadRequestId = null);
    public static IReadOnlyList<Source> StoredSources(AgentRun run)
    {
        if (!AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return [];
        var result = new List<Source>();
        foreach (var message in messages!.Where(m => m.Role == ModelRole.Tool && (TenantCompletionToolContract.IsTool(m.Name ?? "") || m.Name == ToolCatalog.RequestAuthorization)))
        {
            using var doc = JsonDocument.Parse(message.Text); var root = doc.RootElement;
            void Add(JsonElement value)
            {
                if (!value.TryGetProperty("sourceAgentRunId", out var id) || id.ValueKind != JsonValueKind.String) return;
                var artifacts = value.TryGetProperty("artifacts", out var files) ? files.EnumerateArray().Select(a => a.GetProperty("artifactId").GetGuid()).ToArray() : [];
                result.Add(new(value.GetProperty("backgroundSessionId").GetGuid(), id.GetGuid(), artifacts, value.TryGetProperty("readRequestId", out var readId) && readId.ValueKind == JsonValueKind.String ? readId.GetGuid() : null));
            }
            if (root.TryGetProperty("completion", out var completion) && completion.ValueKind == JsonValueKind.Object) Add(completion);
            else if (root.TryGetProperty("completions", out var list)) foreach (var value in list.EnumerateArray()) Add(value);
            else Add(root);
        }
        return result;
    }

    public static async ValueTask RequireSourcesAsync(AgentRun parent, TenantSecurityState state, IamAuthorizer iam,
        Func<Guid, CancellationToken, ValueTask<TenantSession?>> sessions, Func<Guid, CancellationToken, ValueTask<AgentRun?>> runs,
        ITenantArtifactStore? artifacts, CancellationToken ct, Func<Guid, Guid, Guid, CancellationToken, ValueTask<PreparedCompletionRead>>? requireApproved = null)
    {
        foreach (var saved in StoredSources(parent))
        {
            var child = await TenantWaitRules.ChildAsync(parent, await sessions(saved.SessionId, ct).ConfigureAwait(false), runs, ct).ConfigureAwait(false);
            var source = await runs(saved.RunId, ct).ConfigureAwait(false);
            if (child is null || source?.Owner != parent.Owner || source.SessionId != saved.SessionId
                || (await TenantBackgroundLineage.InitialAsync(source, runs, ct).ConfigureAwait(false)).AgentRunId != child.Run.AgentRunId)
                throw new AccessDeniedException("completion_source_unavailable");
            PreparedCompletionRead? approved = null;
            if (saved.ReadRequestId is { } request)
                approved = requireApproved is null ? throw new AccessDeniedException("completion_source_unavailable")
                    : await requireApproved(saved.SessionId, saved.RunId, request, ct).ConfigureAwait(false);
            else if (!await TenantWaitRules.CanReadAsync(state, parent, child with { CurrentTaskRun = source }, iam, ct).ConfigureAwait(false))
                throw new AccessDeniedException("completion_source_unavailable");
            foreach (var id in saved.Artifacts)
            {
                var file = artifacts is null ? null : await artifacts.FindTenantAsync(state.Tenant.TenantId, saved.SessionId, id, ct).ConfigureAwait(false);
                if (file?.TenantId != state.Tenant.TenantId || file.AgentRunId != saved.RunId || file.SessionId != saved.SessionId
                    || (approved is not null ? !TenantCompletionReadRules.Artifacts(approved).Any(a => a.ArtifactId == id)
                        : !await CanReadPermissionAsync(state, parent, "artifacts.read", TenantArtifactRules.Scope(state.Tenant.TenantId, id), iam, ct).ConfigureAwait(false)))
                    throw new AccessDeniedException("completion_source_unavailable");
            }
        }
    }
}
