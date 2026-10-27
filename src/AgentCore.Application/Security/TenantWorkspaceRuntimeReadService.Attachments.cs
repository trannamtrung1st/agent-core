using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantWorkspaceRuntimeReadService
{
    private sealed record AttachmentCollection(IReadOnlyList<AttachmentRecord> Items, string MembersJson, string Hash, bool Truncated);
    private static string AttachmentPath(Guid id) => "/attachments/" + id.ToString("D");

    private async ValueTask<Read> RequireAttachments(TenantRuntimeFrame frame, string tool, TenantWorkspaceReadArguments args,
        CancellationToken ct, bool audit, bool approved, bool live)
    {
        if (args.Path != "/attachments" || tool is not (ToolCatalog.WorkspaceList or ToolCatalog.WorkspaceSearch)) throw new ArgumentException("Use an exact Attachment namespace observation.");
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (run, state) = await operations.ResolveAsync(new(tenant, frame.RunId), ct, retained: !live).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (live) TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
        var current = await configurations.CurrentPolicyAsync(tenant, run.AgentInstanceId, ct).ConfigureAwait(false);
        TenantAttachmentRules.Session(session.Owner, session, current.Agent, true);
        if (run.Owner != frame.Owner || run.SessionId != frame.SessionId || session.Owner.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId
            || session.Owner.AgentInstanceId != run.AgentInstanceId || current.Agent.PrincipalId != run.Owner.ExecutorPrincipalId
            || !TenantTaskExecutionContract.Allows(run, current.Definition)
            || !TenantWorkspaceReadContract.Allows(run.Admission.Configuration!.Definition, tool) || !TenantWorkspaceReadContract.Allows(current.Definition, tool)
            || !TenantAttachmentReadContract.Allows(run.Admission.Configuration.Definition) || !TenantAttachmentReadContract.Allows(current.Definition)) throw new AccessDeniedException("capability_unavailable");
        async ValueTask Policy(string key, AccessScope scope)
        {
            if (approved) return;
            var decision = audit ? await operations.Check(state, run, key, scope, ct, purpose: ExecutionAuthorizationPurpose.ToolBoundary).ConfigureAwait(false)
                : await operations.Evaluate(state, run.Admission.ExecutionContext!, key, scope, ct).ConfigureAwait(false);
            if (decision.Decision == OperationDecisionKind.RequiresApproval) throw new AccessDeniedException("attachment_read_review_required");
            if (decision.Decision != OperationDecisionKind.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        var parent = TenantSessionRules.Scope(tenant, run.SessionId);
        if (!(await iam.CheckOwnedTenantAsync(state, current.Agent.PrincipalId, PermissionCatalog.Get("sessions.read"), parent, ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("executor_read_required");
        await Policy("sessions.read", parent).ConfigureAwait(false);
        // Filter raw child IAM and bound state before paging/scanning. Hidden and pending files never affect bounds.
        List<Guid>? allowed = null;
        if (!(await iam.CheckTenantAsync(state, current.Agent.PrincipalId, PermissionCatalog.Get("attachments.read"), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false)).Allowed)
        {
            allowed = [];
            foreach (var scope in state.Assignments.Where(a => a.PrincipalId == current.Agent.PrincipalId).Select(a => a.Scope).Distinct())
                if (TenantAttachmentRules.TryId(scope, out var id) && (await iam.CheckOwnedTenantAsync(state, current.Agent.PrincipalId, PermissionCatalog.Get("attachments.read"), scope, ct).ConfigureAwait(false)).Allowed) allowed.Add(id);
        }
        var store = attachments ?? throw new AccessDeniedException("tenant_effect_unavailable");
        var maximum = tool == ToolCatalog.WorkspaceList ? args.Limit : WorkspaceSearchLimits.MaxFilesScanned;
        Guid? cursor = args.Cursor is null ? null : TenantAttachmentArtifactRules.Id(args.Cursor);
        var selected = new List<AttachmentRecord>(); var more = false;
        do
        {
            var page = await store.ListTenantAsync(tenant, frame.SessionId, allowed, cursor, Math.Min(100, maximum + 1 - selected.Count), ct, boundOnly: true).ConfigureAwait(false);
            selected.AddRange(page.Items); more = page.HasMore;
            if (page.NextCursor is null || selected.Count > maximum) break;
            if (cursor == page.NextCursor) throw new SecurityConflictException("attachment_source_changed");
            cursor = page.NextCursor;
        } while (more);
        foreach (var source in selected)
        {
            if (source.TenantId != tenant || source.SessionId != frame.SessionId || source.State != AttachmentState.Bound || source.EntryId is null
                || !(await iam.CheckOwnedTenantAsync(state, current.Agent.PrincipalId, PermissionCatalog.Get("attachments.read"), TenantAttachmentRules.Scope(tenant, source.AttachmentId), ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("executor_read_required");
            await Policy("attachments.read", TenantAttachmentRules.Scope(tenant, source.AttachmentId)).ConfigureAwait(false);
        }
        var members = JsonSerializer.Serialize(selected.Select(s => new PreparedAttachmentReadMember(s.AttachmentId, s.EntryId!.Value, s.Sha256Hex)).OrderBy(s => s.AttachmentId.ToString("D"), StringComparer.Ordinal));
        var hash = Hash(JsonSerializer.Serialize(new { tool, args, sources = selected.Select(s => TenantAttachmentContextRules.Witness(s, null)), truncated = more || selected.Count > maximum }));
        return new(run, state, session, current.Agent, new TenantWorkspaceReference.SessionWorking(tenant, run.SessionId), Attachments: new(selected, members, hash, more || selected.Count > maximum));
    }

    private async ValueTask<(string Output, string Hash)> InspectAttachments(TenantRuntimeFrame frame, ModelToolCall call,
        TenantWorkspaceReadArguments args, Read before, int budget, CancellationToken ct, bool approved, bool live)
    {
        var collection = before.Attachments!;
        object Witness() => new { tool = call.Name, arguments = args, attachmentCollection = true, observationHash = collection.Hash };
        string Fit(int count, Func<int, string> serialize)
        {
            while (true)
            {
                var output = serialize(count);
                if (Encoding.UTF8.GetByteCount(output) <= budget && (count > 0 || collection.Items.Count == 0)) return output;
                if (count == 0) return ToolJsonResults.FitToBudget(budget, """{"error":"output_limit"}""");
                count--;
            }
        }
        if (call.Name == ToolCatalog.WorkspaceList)
        {
            var size = Math.Min(args.Limit, collection.Items.Count);
            return (Fit(size, count => JsonSerializer.Serialize(new { path = args.Path, truncated = collection.Truncated || count < size,
                nextPath = (collection.Truncated || count < size) && count > 0 ? AttachmentPath(collection.Items[count - 1].AttachmentId) : null,
                entries = collection.Items.Take(count).Select(s => new { path = AttachmentPath(s.AttachmentId), directory = false, kind = "attachment", displayName = s.DisplayName, byteSize = s.ByteSize, contentType = s.ContentType }),
                untrusted = true, tenantWorkspaceRead = true, workspaceRead = Witness() })), collection.Hash);
        }
        var matches = new List<SearchMatch>(); var truncated = collection.Truncated;
        foreach (var source in collection.Items.Take(WorkspaceSearchLimits.MaxFilesScanned))
        {
            ct.ThrowIfCancellationRequested();
            if (matches.Count >= args.MaxResults) { truncated = true; break; }
            // Attachment names are labels, never filesystem selectors. Globs and name queries use those labels.
            if (!WorkspaceGlob.Matches(source.DisplayName, args.Glob)) continue;
            var name = source.DisplayName.Contains(args.Query!, StringComparison.OrdinalIgnoreCase);
            string? snippet = null;
            if (source.ByteSize <= WorkspaceSearchLimits.MaxFileBytesToScan && AttachmentMedia.IsReadableText(source.ContentType))
            {
                var bytes = await attachments!.ReadTenantContentAsync(frame.Owner.TenantId!.Value, frame.SessionId, source.AttachmentId, ct).ConfigureAwait(false);
                TenantAttachmentRules.Content(source, bytes);
                if (WorkspaceSearchText.TryReadText(bytes, out var text) && text.Contains(args.Query!, StringComparison.OrdinalIgnoreCase)) snippet = WorkspaceSearchText.Snippet(text, args.Query!);
            }
            if (name || snippet is not null) matches.Add(new(AttachmentPath(source.AttachmentId), snippet is null ? "name" : "content", snippet ?? source.DisplayName));
        }
        // The collection fingerprint includes all bounded candidate hashes, including negative/glob-skipped observations.
        await Fence(frame, call.Name, args, before, ct, approved, live).ConfigureAwait(false);
        var countMatches = matches.Count;
        while (true)
        {
            var output = JsonSerializer.Serialize(new { path = args.Path, query = args.Query, truncated = truncated || countMatches < matches.Count,
                matches = matches.Take(countMatches).Select(m => new { path = m.Path, kind = m.Kind, snippet = m.Snippet }), untrusted = true, tenantWorkspaceRead = true, workspaceRead = Witness() });
            if (Encoding.UTF8.GetByteCount(output) <= budget && (countMatches > 0 || matches.Count == 0)) return (output, collection.Hash);
            if (countMatches == 0) return (ToolJsonResults.FitToBudget(budget, """{"error":"output_limit"}"""), collection.Hash);
            countMatches--;
        }
    }
}
