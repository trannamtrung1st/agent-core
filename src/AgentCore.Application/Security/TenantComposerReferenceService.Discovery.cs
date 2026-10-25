using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantComposerReferenceService
{
    private sealed record Cursor(Guid Tenant, Guid Instance, string Category, string Search, int Page = 0,
        string? Path = null, Guid? Artifact = null, Guid? Space = null);
    public async ValueTask<TenantComposerPage> DiscoverAsync(Guid tenant, Guid instance, TenantSessionInitiator human,
        string category, string? search = null, string? cursor = null, CancellationToken ct = default, Guid? spaceId = null)
    {
        search = search?.Trim() ?? "";
        if (search.Length > 200 || cursor?.Length > 8192 || category is not ("homeFile" or "session" or "backgroundSession" or "artifact" or "agentRun" or "skill" or "invocation"))
            throw AgentCoreErrors.Validation("Invalid reference category, search or cursor.");
        var position = new Cursor(tenant, instance, category, search, Space: spaceId);
        if (cursor is not null)
        {
            try
            {
                position = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(cursor), UserMessageContent.Json)!;
                if (position is null || position.Tenant != tenant || position.Instance != instance || position.Space != spaceId || position.Category != category
                    || position.Search != search || position.Page is < 0 or > 100000) throw new FormatException();
            }
            catch (Exception error) when (error is FormatException or JsonException) { throw AgentCoreErrors.Validation("Invalid reference cursor. Search again."); }
        }
        var (state, agent) = await Human(tenant, instance, human, ct).ConfigureAwait(false);
        await Permission(state, human.PrincipalId, "agents.read", AgentScope(tenant, instance), ct).ConfigureAwait(false);
        bool Matches(string value) => value.Contains(search, StringComparison.OrdinalIgnoreCase);
        var items = new List<TenantComposerChoice>(); Cursor? next = null;
        var readableSessions = category is "session" or "backgroundSession" or "artifact" or "agentRun"
            ? await Readable(state, human.PrincipalId, "sessions.read", "session", ct).ConfigureAwait(false) : null;
        // Native positions survive filtered/empty pages. No source body or read receipt is touched.
        for (var scan = 0; scan < 40; scan++)
        {
            ct.ThrowIfCancellationRequested(); next = null;
            if (category == "homeFile")
            {
                var owner = await HomeOwner(tenant, instance, spaceId ?? throw new AccessDeniedException("space_context_required"), ct).ConfigureAwait(false);
                await Permission(state, human.PrincipalId, "spaces.read", TenantSpaceService.Scope(tenant, owner.SpaceId), ct).ConfigureAwait(false);
                await Permission(state, human.PrincipalId, "workspaces.read", TenantWorkspaceRules.Scope(owner), ct).ConfigureAwait(false);
                if (position.Path is not null) TenantWorkspaceRules.Path(owner, position.Path.TrimEnd('/'));
                var page = await files.SearchTenantAsync(owner, search, position.Path, 40, ct).ConfigureAwait(false);
                foreach (var file in page.Items.Where(f => !f.Directory && Matches(f.LogicalPath)))
                    items.Add(new(file.LogicalPath, Label(file.LogicalPath), file.ContentType + (file.ByteSize > MaxBodyBytes || !TextType(file.ContentType) ? " · Metadata only" : ""), category,
                        Reference: new(category, SpaceId: owner.SpaceId, Path: file.LogicalPath)));
                next = page.NextPath is { } path ? position with { Path = path } : null;
            }
            else if (category is "skill" or "invocation")
            {
                var catalog = await Skills(tenant, instance, ct).ConfigureAwait(false);
                var visible = new List<AgentCore.Domain.Definitions.EffectiveSkill>();
                foreach (var skill in catalog.OrderBy(s => s.Key, StringComparer.Ordinal))
                    if ((await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("skills.read"), SkillScope(tenant, instance, skill.Key), ct).ConfigureAwait(false)).Allowed
                        && Matches(skill.Name + " " + skill.Description + " " + skill.Key)) visible.Add(skill);
                foreach (var skill in visible.Skip(position.Page * 40).Take(40))
                    items.Add(new(skill.Key, Label(skill.Name), skill.Key + " · " + Label(skill.Description), category,
                        category == "invocation" ? skill.Key : null, category == "skill" ? new(category, AgentInstanceId: instance, SkillKey: skill.Key) : null));
                if ((position.Page + 1) * 40 < visible.Count) next = position with { Page = position.Page + 1 };
            }
            else if (category == "agentRun")
            {
                var allowed = await Readable(state, human.PrincipalId, "agent-runs.read", "agent-run", ct).ConfigureAwait(false);
                var page = await runs.ListTenantSessionRunsAsync(tenant, null, allowed, position.Page, 40, ct, instance, readableSessions).ConfigureAwait(false);
                foreach (var run in page.Items.Where(r => Matches($"{r.AgentRunId:D} {r.Admission.Activation.Kind} {r.Status}")))
                    items.Add(new(run.AgentRunId.ToString("D"), $"{run.Admission.Activation.Kind} · {run.AgentRunId.ToString("D")[..8]}", run.Status.ToString(), category,
                        Reference: new(category, SessionId: run.SessionId, AgentRunId: run.AgentRunId, SelectedRevision: run.Revision)));
                if ((position.Page + 1) * 40 < page.Total) next = position with { Page = position.Page + 1 };
            }
            else
            {
                var page = await sessions.ListTenantSessionsAsync(tenant, readableSessions, position.Page, category == "artifact" ? 1 : 40, ct,
                    includeArchived: false, surface: category == "backgroundSession" ? SessionSurface.BackgroundWork : category == "session" ? SessionSurface.ChatList : null, instanceId: instance, recentFirst: true).ConfigureAwait(false);
                if (category == "artifact")
                {
                    if (page.Items.FirstOrDefault() is { } session)
                    {
                        var allowed = await Readable(state, human.PrincipalId, "artifacts.read", "artifact", ct).ConfigureAwait(false);
                        var owned = await artifacts.ListTenantAsync(tenant, session.Owner.SessionId, allowed, position.Artifact, 40, ct: ct).ConfigureAwait(false);
                        foreach (var file in owned.Items.Where(f => Matches(f.DisplayName + " " + f.ArtifactId)))
                            items.Add(new(file.ArtifactId.ToString("D"), Label(file.DisplayName), Label(session.Snapshot.Title) + (file.ByteSize > MaxBodyBytes || !TextType(file.ContentType) ? " · Metadata only" : ""), category,
                                Reference: new(category, SessionId: file.SessionId, ArtifactId: file.ArtifactId)));
                        if (owned.HasMore) next = position with { Artifact = owned.NextCursor };
                        else if (position.Page + 1 < page.Total) next = position with { Page = position.Page + 1, Artifact = null };
                    }
                }
                else
                {
                    foreach (var session in page.Items.Where(s => s.Snapshot.Surfaces.HasFlag(SessionSurface.BackgroundWork) == (category == "backgroundSession") && Matches(s.Snapshot.Title + " " + s.Owner.SessionId)))
                        items.Add(new(session.Owner.SessionId.ToString("D"), Label(session.Snapshot.Title), session.Owner.SessionId.ToString("D"), category,
                            Reference: new(category, SessionId: session.Owner.SessionId, SelectedRevision: session.Snapshot.Revision)));
                    if ((position.Page + 1) * 40 < page.Total) next = position with { Page = position.Page + 1 };
                }
            }
            if (items.Count > 0 || next is null) break;
            position = next;
        }
        await HumanFence(tenant, instance, human, state, agent, ct).ConfigureAwait(false);
        return new(items.AsReadOnly(), next is null ? null : Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(next, UserMessageContent.Json)));
    }
    private async ValueTask<IReadOnlyList<Guid>?> Readable(TenantSecurityState state, Guid actor, string permission, string type, CancellationToken ct)
    {
        if ((await iam.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get(permission), AccessScope.ForTenant(state.Tenant.TenantId), ct).ConfigureAwait(false)).Allowed) return null;
        var result = new List<Guid>();
        foreach (var scope in state.Assignments.Where(a => a.PrincipalId == actor).Select(a => a.Scope).Distinct())
            if (scope.TenantId == state.Tenant.TenantId && scope.ResourceType == type && Guid.TryParseExact(scope.ResourceId, "D", out var id)
                && (await iam.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed) result.Add(id);
        return result.AsReadOnly();
    }
}
