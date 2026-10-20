using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantWorkspaceRuntimeReadService
{
    private sealed record ResourceEntry(string Path, bool Directory, TenantResourceSourceObservation Source);
    private sealed record ResourceCollection(IReadOnlyList<ResourceEntry> Entries, IReadOnlyList<TenantResourceSourceObservation> Sources,
        string MembersJson, string Hash, bool Truncated, int Scanned);
    private static bool Within(string path, string root) => path == root || path.StartsWith(root + "/", StringComparison.Ordinal);
    private async ValueTask<Read> RequireCollection(TenantRuntimeFrame frame, string tool, TenantWorkspaceReadArguments args,
        CancellationToken ct, bool audit, bool approved, bool live)
    {
        TenantResourceSourceTarget.ValidatePath(args.Path, true);
        if (args.Cursor is { } cursor && tool == ToolCatalog.WorkspaceList)
        {
            TenantResourceSourceTarget.ValidatePath(cursor, true);
            if (!Within(cursor, args.Path) || cursor != args.Path && cursor[(args.Path.Length + 1)..].Contains('/')) throw new ArgumentException("Use an exact resource child cursor.");
        }
        var tenant = frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (run, state) = await operations.ResolveAsync(new(tenant, frame.RunId), ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, frame.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (live) TenantRuntimeRules.Require(frame, session.Owner, run, time.GetUtcNow());
        if (run.Owner != frame.Owner || run.SessionId != frame.SessionId || session.Owner.AgentInstanceId != run.AgentInstanceId || session.Owner.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId
            || !TenantTaskExecutionContract.Allows(run, run.Admission.Configuration!.Definition)
            || session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active
            || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended) throw new AccessDeniedException("resource_unavailable");
        var current = await configurations.CurrentResourcesAsync(tenant, run.AgentInstanceId, ct).ConfigureAwait(false);
        var agent = current.Snapshot.Agent;
        if (!TenantTaskExecutionContract.Allows(run, current.Definition)) throw new AccessDeniedException("capability_unavailable");
        if (agent.PrincipalId != run.Owner.ExecutorPrincipalId || !TenantWorkspaceReadContract.Allows(run.Admission.Configuration!.Definition, tool)
            || !TenantWorkspaceReadContract.Allows(current.Definition, tool)) throw new AccessDeniedException("capability_unavailable");
        async ValueTask Policy(string key, AccessScope scope)
        {
            if (approved) return;
            var decision = audit ? await operations.Check(state, run, key, scope, ct, purpose: ExecutionAuthorizationPurpose.ToolBoundary).ConfigureAwait(false)
                : await operations.Evaluate(state, run.Admission.ExecutionContext!, key, scope, ct).ConfigureAwait(false);
            if (decision.Decision == OperationDecisionKind.RequiresApproval) throw new AccessDeniedException("resource_read_review_required");
            if (decision.Decision != OperationDecisionKind.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        foreach (var (key, scope) in new[] { ("sessions.read", TenantSessionRules.Scope(tenant, run.SessionId)),
            ("agents.read", AccessScope.ForResource(tenant, "agent", run.AgentInstanceId.ToString("D"))) })
        {
            if (!(await iam.CheckOwnedTenantAsync(state, agent.PrincipalId, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("executor_read_required");
            await Policy(key, scope).ConfigureAwait(false);
        }
        // Ownership comes from the current scoped graph. Filtering raw grants first prevents hidden children from affecting paging/bounds.
        var actual = current.Resources.ToDictionary(r => r.Key, StringComparer.Ordinal);
        var eligible = new List<TenantResourceSourceObservation>();
        foreach (var resource in run.Admission.Configuration.Resources.OrderBy(r => r.VirtualPath, StringComparer.Ordinal))
        {
            if (!Within(resource.VirtualPath, args.Path) || !actual.TryGetValue(resource.Key, out var now) || now != resource
                || resource.Key.StartsWith("definition:", StringComparison.Ordinal) && (agent.Instance.DefinitionId != run.DefinitionId || agent.Instance.ActiveVersion != run.DefinitionVersion)) continue;
            var scope = TenantAgentChildScopes.Scope(tenant, run.AgentInstanceId, "resource", resource.Key);
            if (!(await iam.CheckTenantAsync(state, agent.PrincipalId, PermissionCatalog.Get("resources.read"), scope, ct).ConfigureAwait(false)).Allowed) continue;
            var revision = resource.Key.StartsWith("instance:", StringComparison.Ordinal)
                ? current.Snapshot.Resources.InstanceResources.Single(r => "instance:" + r.ResourceId.ToString("D") == resource.Key).Revision
                : current.Snapshot.Resources.DefinitionStates.SingleOrDefault(r => "definition:" + r.ResourceId.ToString("D") == resource.Key)?.Revision ?? 0;
            eligible.Add(new(tenant, run.AgentInstanceId, run.SessionId, resource, run.DefinitionId, run.DefinitionVersion, revision));
        }
        ResourceEntry Entry(TenantResourceSourceObservation source)
        {
            var path = source.Resource.VirtualPath;
            if (path == args.Path) return new(path, false, source);
            var relative = path[(args.Path.Length + 1)..]; var slash = relative.IndexOf('/');
            return new(slash < 0 ? path : args.Path + "/" + relative[..slash], slash >= 0, source);
        }
        ResourceEntry[] entries; TenantResourceSourceObservation[] selected; bool truncated; int scanned;
        if (tool == ToolCatalog.WorkspaceList)
        {
            entries = eligible.Select(Entry).GroupBy(e => e.Path, StringComparer.Ordinal)
                .Select(g => g.OrderByDescending(e => e.Directory).ThenBy(e => e.Source.Resource.VirtualPath, StringComparer.Ordinal).First())
                .Where(e => args.Cursor is null || string.CompareOrdinal(e.Path, args.Cursor) > 0).OrderBy(e => e.Path, StringComparer.Ordinal).Take(args.Limit + 1).ToArray();
            selected = entries.Select(e => e.Source).Distinct().ToArray(); truncated = entries.Length > args.Limit; scanned = 0;
        }
        else
        {
            var shallow = eligible.Where(e => e.Resource.VirtualPath == args.Path || e.Resource.VirtualPath[(args.Path.Length + 1)..].Count(c => c == '/') <= WorkspaceSearchLimits.MaxDepth).Take(WorkspaceSearchLimits.MaxFilesScanned + 1).ToArray();
            var deep = eligible.FirstOrDefault(e => e.Resource.VirtualPath != args.Path && e.Resource.VirtualPath[(args.Path.Length + 1)..].Count(c => c == '/') > WorkspaceSearchLimits.MaxDepth);
            selected = shallow.Concat(deep is null ? [] : new[] { deep }).ToArray(); entries = [];
            truncated = shallow.Length > WorkspaceSearchLimits.MaxFilesScanned || deep is not null;
            scanned = Math.Min(shallow.Length, WorkspaceSearchLimits.MaxFilesScanned);
        }
        foreach (var source in selected) await Policy("resources.read", TenantAgentChildScopes.Scope(tenant, run.AgentInstanceId, "resource", source.Resource.Key)).ConfigureAwait(false);
        var members = JsonSerializer.Serialize(selected.Select(s => new PreparedResourceReadMember(s.Resource.Key, s.ResourceRevision)).OrderBy(r => r.Key, StringComparer.Ordinal));
        var fingerprint = Hash(JsonSerializer.Serialize(new { tool, args, sources = selected, entries = entries.Select(e => new { e.Path, e.Directory }), truncated, scanned }));
        return new(run, state, session, agent, new(tenant, run.AgentInstanceId), Collection: new(entries, selected, members, fingerprint, truncated, scanned));
    }
    private async ValueTask<(string Output, string Hash)> InspectCollection(TenantRuntimeFrame frame, ModelToolCall call,
        TenantWorkspaceReadArguments args, Read before, int budget, CancellationToken ct, bool approved, bool live)
    {
        var collection = before.Collection!;
        object Witness(string hash) => new { tool = call.Name, arguments = args, resourceCollection = true, observationHash = hash };
        if (call.Name == ToolCatalog.WorkspaceList)
        {
            var count = Math.Min(args.Limit, collection.Entries.Count);
            while (true)
            {
                var truncated = collection.Truncated || count < Math.Min(args.Limit, collection.Entries.Count);
                var result = JsonSerializer.Serialize(new { path = args.Path, truncated, nextPath = truncated && count > 0 ? collection.Entries[count - 1].Path : null,
                    entries = collection.Entries.Take(count).Select(e => new { path = e.Path, directory = e.Directory, kind = e.Directory ? "resource-directory" : "resource",
                        byteSize = e.Directory ? (long?)null : e.Source.Resource.ByteLength, contentType = e.Directory ? null : e.Source.Resource.MediaType }),
                    untrusted = true, tenantWorkspaceRead = true, workspaceRead = Witness(collection.Hash) });
                if (Encoding.UTF8.GetByteCount(result) <= budget && (count > 0 || collection.Entries.Count == 0)) return (result, collection.Hash);
                if (count == 0) return (ToolJsonResults.FitToBudget(budget, """{"error":"output_limit"}"""), collection.Hash);
                count--;
            }
        }
        var matches = new List<SearchMatch>(); var observed = new List<object>(); var truncatedSearch = collection.Truncated;
        foreach (var source in collection.Sources.Take(collection.Scanned))
        {
            ct.ThrowIfCancellationRequested(); var file = source.Resource;
            var relative = file.VirtualPath == args.Path ? Path.GetFileName(file.VirtualPath) : file.VirtualPath[(args.Path.Length + 1)..];
            if (!WorkspaceGlob.Matches(relative, args.Glob)) continue;
            string? snippet = null;
            if (file.ByteLength <= WorkspaceSearchLimits.MaxFileBytesToScan)
            {
                var captured = await (resources ?? throw new AccessDeniedException("tenant_effect_unavailable")).CaptureForSearchAsync(frame, file.VirtualPath, approved, live, ct).ConfigureAwait(false);
                if (captured.Observation != source) throw new SecurityConflictException("resource_source_changed");
                var bytes = captured.CopyContent();
                if (WorkspaceSearchText.TryReadText(bytes, out var text) && text.Contains(args.Query!, StringComparison.OrdinalIgnoreCase)) snippet = WorkspaceSearchText.Snippet(text, args.Query!);
                observed.Add(new { file.Key, verified = true });
            }
            if (file.VirtualPath.Contains(args.Query!, StringComparison.OrdinalIgnoreCase) || snippet is not null)
            {
                if (matches.Count < args.MaxResults) matches.Add(new(file.VirtualPath, snippet is null ? "name" : "content", snippet ?? Path.GetFileName(file.VirtualPath)));
                else truncatedSearch = true;
            }
        }
        var hash = Hash(JsonSerializer.Serialize(new { collection.Hash, observed, truncatedSearch })); var size = matches.Count;
        while (true)
        {
            var output = JsonSerializer.Serialize(new { path = args.Path, query = args.Query, truncated = truncatedSearch || size < matches.Count,
                matches = matches.Take(size).Select(m => new { path = m.Path, kind = m.Kind, snippet = m.Snippet }), untrusted = true,
                tenantWorkspaceRead = true, workspaceRead = Witness(hash) });
            if (Encoding.UTF8.GetByteCount(output) <= budget && (size > 0 || matches.Count == 0)) return (output, hash);
            if (size == 0) return (ToolJsonResults.FitToBudget(budget, """{"error":"output_limit"}"""), hash);
            size--;
        }
    }
}
