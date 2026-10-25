using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

public sealed partial class TenantSpaceService
{
    public sealed record ResourceMetadata(Guid TenantId, Guid SpaceId, Guid ResourceId, string LogicalPath, AgentDefinitionResourceKind Kind,
        string MediaType, string ContentSha256, long ByteLength, long Revision,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SourcePath);
    public sealed record ResourcePage(IReadOnlyList<ResourceMetadata> Items, int Total, int Page, int PageSize);
    private static ResourceMetadata Metadata(SpaceFileResource value, bool sourcePath) => new(value.TenantId, value.SpaceId, value.ResourceId, value.LogicalPath,
        value.Kind, value.MediaType, value.ContentSha256, value.ByteLength, value.Revision, sourcePath ? value.SourcePath : null);
    public sealed record ResourceAccess(ResourceMetadata Resource, IReadOnlyList<string> Permissions, long SecurityRevision, long SpaceRevision);
    public async ValueTask<ResourceAccess> ResourceAccessAsync(Guid actor, Guid tenant, Guid space, Guid resource, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        var state = await ReadState(actor, tenant, human, ct).ConfigureAwait(false);
        var owner = await ReadAsync(actor, tenant, space, ct, human).ConfigureAwait(false);
        var value = await (resourceSources ?? throw new AccessDeniedException("resource_source_unavailable")).FindSpaceResourceAsync(tenant, space, resource, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("resource_unavailable");
        var scope = TenantResourceScopes.Source(tenant, Guid.Empty, value.Key);
        await authorizer.RequireAsync(actor, "resources.read", scope, ct).ConfigureAwait(false);
        var permissions = new List<string>();
        foreach (var key in new[] { "resources.read", "resources.manage", "resources.delete", "iam.roles.read", "iam.roles.assign" })
            if ((await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed
                && (key is not ("resources.manage" or "resources.delete") || (await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("spaces.manage"), Scope(tenant, space), ct).ConfigureAwait(false)).Allowed)) permissions.Add(key);
        var canReadHome = (await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("workspaces.read"), TenantWorkspaceRules.Home(tenant, space), ct).ConfigureAwait(false)).Allowed;
        if (!canReadHome)
            permissions.Remove("resources.manage");
        await CompleteRead(actor, state, human, ct).ConfigureAwait(false);
        return new(Metadata(value, canReadHome), permissions, state.Tenant.Revision, owner.Revision);
    }
    public async ValueTask<ResourcePage> ResourcesAsync(Guid actor, Guid tenant, Guid space, int page = 0,
        HumanSessionReference? human = null, CancellationToken ct = default)
    {
        var state = await ReadState(actor, tenant, human, ct).ConfigureAwait(false);
        await ReadAsync(actor, tenant, space, ct, human).ConfigureAwait(false);
        var source = resourceSources ?? throw new AccessDeniedException("resource_source_unavailable");
        var rows = await source.ReadSpaceResourcesAsync(tenant, space, 0, 100, ct).ConfigureAwait(false);
        var visible = new List<SpaceFileResource>();
        foreach (var row in rows.Items)
            if ((await authorizer.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get("resources.read"),
                TenantResourceScopes.Source(tenant, Guid.Empty, row.Key), ct).ConfigureAwait(false)).Allowed) visible.Add(row);
        var canReadHome = (await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("workspaces.read"), TenantWorkspaceRules.Home(tenant, space), ct).ConfigureAwait(false)).Allowed;
        // Hidden source identities never affect a permission-filtered count.
        await CompleteRead(actor, state, human, ct).ConfigureAwait(false);
        var index = Math.Clamp(page, 0, 100_000);
        return new(visible.Skip(index * 50).Take(50).Select(row => Metadata(row, canReadHome)).ToArray(), visible.Count, index, 50);
    }
    public async ValueTask<SpaceFileResource> SaveResourceAsync(Guid actor, Guid tenant, Guid space, Guid resource,
        string sourcePath, string logicalPath, AgentDefinitionResourceKind kind, long expectedSecurityRevision,
        long expectedSpaceRevision, long expectedResourceRevision, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        if (expectedSecurityRevision < 1 || expectedSpaceRevision < 1 || expectedResourceRevision < 0) throw new ArgumentException("Use valid expected source revisions.");
        var before = await ReadState(actor, tenant, human, ct).ConfigureAwait(false);
        var owner = await ReadAsync(actor, tenant, space, ct, human).ConfigureAwait(false);
        await authorizer.RequireAsync(actor, "spaces.manage", Scope(tenant, space), ct).ConfigureAwait(false);
        var source = resourceSources ?? throw new AccessDeniedException("resource_source_unavailable");
        var current = await source.FindSpaceResourceAsync(tenant, space, resource, ct).ConfigureAwait(false);
        var permission = "resources.manage";
        var target = expectedResourceRevision == 0 ? AccessScope.ForTenant(tenant) : TenantResourceScopes.Source(tenant, Guid.Empty, SpaceResourceKeys.Create(space, resource));
        await authorizer.RequireAsync(actor, permission, target, ct).ConfigureAwait(false);
        await authorizer.RequireAsync(actor, "workspaces.read", TenantWorkspaceRules.Home(tenant, space), ct).ConfigureAwait(false);
        // A stable client-generated source ID makes a lost create acknowledgement safely discoverable.
        if (expectedResourceRevision == 0 && current is { Revision: 1 } && current.SourcePath == sourcePath
            && current.LogicalPath == logicalPath && current.Kind == kind)
        { await authorizer.RequireAsync(actor, "resources.read", TenantResourceScopes.Source(tenant, Guid.Empty, current.Key), ct).ConfigureAwait(false); await CompleteRead(actor, before, human, ct).ConfigureAwait(false); return current; }
        if (before.Tenant.Revision != expectedSecurityRevision || owner.Revision != expectedSpaceRevision
            || (current?.Revision ?? 0) != expectedResourceRevision) throw new SecurityConflictException("stale_revision");
        var capture = await (workspaceSources ?? throw new AccessDeniedException("resource_source_unavailable"))
            .CaptureResourceSourceTenantAsync(new TenantWorkspaceReference.SpaceHome(tenant, space), sourcePath, ct).ConfigureAwait(false);
        var observed = capture.Observation; var at = Now();
        var value = new SpaceFileResource(tenant, space, resource, logicalPath, kind, observed.ContentType, observed.Sha256Hex,
            observed.ByteSize, sourcePath, checked(expectedResourceRevision + 1), current?.CreatedAt ?? at, at);
        value.Validate();
        await (resourceContent ?? throw new AccessDeniedException("resource_source_unavailable"))
            .StoreVerifiedAsync(value.ContentSha256, capture.CopyContent(), ct).ConfigureAwait(false);
        await capture.RequireCurrentAsync(ct).ConfigureAwait(false);
        await CompleteRead(actor, before, human, ct).ConfigureAwait(false);
        await CommitResource(before, owner, value, expectedResourceRevision, actor, permission, human, ct).ConfigureAwait(false);
        return value;
    }
    public async ValueTask DeleteResourceAsync(Guid actor, Guid tenant, Guid space, Guid resource, long expectedSecurityRevision,
        long expectedSpaceRevision, long expectedResourceRevision, HumanSessionReference? human = null, CancellationToken ct = default)
    {
        var state = await ReadState(actor, tenant, human, ct).ConfigureAwait(false);
        var owner = await ReadAsync(actor, tenant, space, ct, human).ConfigureAwait(false);
        await authorizer.RequireAsync(actor, "spaces.manage", Scope(tenant, space), ct).ConfigureAwait(false);
        var current = await (resourceSources ?? throw new AccessDeniedException("resource_source_unavailable"))
            .FindSpaceResourceAsync(tenant, space, resource, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var scope = TenantResourceScopes.Source(tenant, Guid.Empty, current.Key);
        await authorizer.RequireAsync(actor, "resources.delete", scope, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != expectedSecurityRevision || owner.Revision != expectedSpaceRevision || current.Revision != expectedResourceRevision)
            throw new SecurityConflictException("stale_revision");
        state = state with { Assignments = state.Assignments.Where(a => a.Scope != scope).ToArray() };
        var at = Now();
        await CommitResource(state, owner, current with { Revision = checked(current.Revision + 1), UpdatedAt = at, DeletedAt = at },
            expectedResourceRevision, actor, "resources.delete", human, ct).ConfigureAwait(false);
    }
    private async ValueTask CommitResource(TenantSecurityState state, Space owner, SpaceFileResource value, long expected,
        Guid actor, string permission, HumanSessionReference? human, CancellationToken ct)
    {
        if (!await resourceSources!.TrySaveSpaceResourceAsync(state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } },
            state.Tenant.Revision, owner.Revision, value, expected,
            new(ids.NewId(), value.TenantId, actor, permission, value.Key, "allowed", "space_resource_updated", value.UpdatedAt), human, ct).ConfigureAwait(false))
            throw new SecurityConflictException("stale_revision");
    }
}
