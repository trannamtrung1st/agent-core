using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAgentSkillItem(InstanceSkillView Skill, bool CanManage, bool CanCustomize);
public sealed record TenantAgentSkillDetail(TenantAgent Agent, TenantAgentSkillItem Item, long SecurityRevision);
public sealed record TenantAgentSkillEditor(TenantAgent Agent, IReadOnlyList<TenantAgentSkillItem> Items, int Total,
    int Page, int PageSize, long SecurityRevision, bool CanCreate, bool AllReadable);
public sealed record TenantAgentResourceItem(InstanceResourceView Resource, bool CanManage, bool CanDelete, bool CanCopy);
public sealed record TenantAgentResourceDetail(TenantAgent Agent, TenantAgentResourceItem Item, long SecurityRevision);
public sealed record TenantAgentResourceEditor(TenantAgent Agent, IReadOnlyList<TenantAgentResourceItem> Items, int Total,
    int Page, int PageSize, long SecurityRevision, bool CanCreate, bool AllReadable);

public sealed partial class TenantAgentConfigurationService
{
    private sealed record ChildCatalog<T>(TenantAgent Agent, T[] Items, TenantSecurityState State, bool AllReadable);

    public async ValueTask<TenantAgentSkillDetail> ReadSkillDetailAsync(Guid actor, Guid tenant, Guid id, string key, CancellationToken ct = default)
    {
        var catalog = await ReadSkillCatalog(actor, tenant, id, ct).ConfigureAwait(false);
        var skill = catalog.Items.SingleOrDefault(item => item.Key == key) ?? throw new AccessDeniedException("resource_unavailable");
        return new(catalog.Agent, new(skill, await CanChangeChild(catalog.State, actor, catalog.Agent, "skills.manage", key, ct).ConfigureAwait(false),
            skill.Origin == SkillOrigin.Definition && await CanCreateChild(catalog.State, actor, catalog.Agent, "skills", ct).ConfigureAwait(false)), catalog.State.Tenant.Revision);
    }

    public async ValueTask<TenantAgentSkillEditor> ReadSkillEditorAsync(Guid actor, Guid tenant, Guid id,
        int page = 0, int pageSize = 30, string? search = null, CancellationToken ct = default)
    {
        var query = ChildQuery(page, pageSize, search);
        var catalog = await ReadSkillCatalog(actor, tenant, id, ct).ConfigureAwait(false);
        var canCreate = await CanCreateChild(catalog.State, actor, catalog.Agent, "skills", ct).ConfigureAwait(false);
        var visible = catalog.Items.Where(s => Matches(query, s.Key, s.Name, s.Description, s.Origin.ToString(), s.Projection.ToString(),
            string.Join(' ', s.RequiredCapabilities))).OrderBy(s => s.Origin.ToString(), StringComparer.Ordinal)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Key, StringComparer.Ordinal).ToArray();
        var items = new List<TenantAgentSkillItem>();
        foreach (var skill in visible.Skip(page * pageSize).Take(pageSize))
            items.Add(new(skill, await CanChangeChild(catalog.State, actor, catalog.Agent, "skills.manage", skill.Key, ct).ConfigureAwait(false),
                skill.Origin == SkillOrigin.Definition && canCreate));
        return new(catalog.Agent, items, visible.Length, page, pageSize, catalog.State.Tenant.Revision, canCreate, catalog.AllReadable);
    }

    public async ValueTask<TenantAgentResourceEditor> ReadResourceEditorAsync(Guid actor, Guid tenant, Guid id,
        int page = 0, int pageSize = 30, string? search = null, CancellationToken ct = default)
    {
        var query = ChildQuery(page, pageSize, search);
        var catalog = await ReadResourceCatalog(actor, tenant, id, ct).ConfigureAwait(false);
        var canCreate = await CanCreateChild(catalog.State, actor, catalog.Agent, "resources", ct).ConfigureAwait(false);
        var visible = catalog.Items.Where(r => Matches(query, r.Key, r.LogicalPath, r.Origin, r.Kind.ToString(), r.MediaType))
            .OrderBy(r => r.LogicalPath, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Key, StringComparer.Ordinal).ToArray();
        var items = new List<TenantAgentResourceItem>();
        foreach (var resource in visible.Skip(page * pageSize).Take(pageSize))
            items.Add(new(resource, await CanChangeChild(catalog.State, actor, catalog.Agent, "resources.manage", resource.Key, ct).ConfigureAwait(false),
                resource.Origin == "Instance" && await CanChangeChild(catalog.State, actor, catalog.Agent, "resources.delete", resource.Key, ct).ConfigureAwait(false),
                resource.Origin == "Definition" && canCreate));
        return new(catalog.Agent, items, visible.Length, page, pageSize, catalog.State.Tenant.Revision, canCreate, catalog.AllReadable);
    }

    public async ValueTask<TenantAgentResourceDetail> ReadResourceDetailAsync(Guid actor, Guid tenant, Guid id, string key, CancellationToken ct = default)
    {
        var catalog = await ReadResourceCatalog(actor, tenant, id, ct).ConfigureAwait(false);
        var resource = catalog.Items.SingleOrDefault(item => item.Key == key) ?? throw new AccessDeniedException("resource_unavailable");
        return new(catalog.Agent, new(resource,
            await CanChangeChild(catalog.State, actor, catalog.Agent, "resources.manage", key, ct).ConfigureAwait(false),
            resource.Origin == "Instance" && await CanChangeChild(catalog.State, actor, catalog.Agent, "resources.delete", key, ct).ConfigureAwait(false),
            resource.Origin == "Definition" && await CanCreateChild(catalog.State, actor, catalog.Agent, "resources", ct).ConfigureAwait(false)), catalog.State.Tenant.Revision);
    }

    private async ValueTask<bool> CanCreateChild(TenantSecurityState state, Guid actor, TenantAgent agent, string family, CancellationToken ct) =>
        agent.Instance.Lifecycle == AgentInstanceLifecycle.Active
        && (await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(family + ".read"), AccessScope.ForTenant(agent.TenantId), ct).ConfigureAwait(false)).Allowed
        && (await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(family + ".manage"), AccessScope.ForTenant(agent.TenantId), ct).ConfigureAwait(false)).Allowed;

    // The catalog already proved read access to this exact child. Hints never authorize the later command.
    private async ValueTask<bool> CanChangeChild(TenantSecurityState state, Guid actor, TenantAgent agent, string permission, string key, CancellationToken ct) =>
        agent.Instance.Lifecycle == AgentInstanceLifecycle.Active
        && (await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), ChildScope(agent.TenantId, agent.Instance.InstanceId, permission, key), ct).ConfigureAwait(false)).Allowed;

    private static string ChildQuery(int page, int pageSize, string? search)
    {
        if (page is < 0 or > 100_000 || pageSize is < 1 or > 100 || search?.Length > 128)
            throw new ArgumentException("Invalid child catalog query.");
        return search?.Trim() ?? "";
    }
    private static bool Matches(string query, params string[] fields) =>
        query.Length == 0 || fields.Any(value => value.Contains(query, StringComparison.OrdinalIgnoreCase));
}
