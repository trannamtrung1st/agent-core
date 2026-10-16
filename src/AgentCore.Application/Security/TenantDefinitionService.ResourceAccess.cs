using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record DefinitionResourcePermission(Guid ResourceId, AccessScope Scope, bool CanManage, bool CanDelete);
public sealed record DefinitionResourceAccess(bool AllRead, bool CanCreate, bool CanEvaluate, bool CanPublish, IReadOnlyList<DefinitionResourcePermission> Items);
public sealed record TenantDefinitionDraftState(AgentDefinitionDraft Draft, IReadOnlyList<AgentDefinitionDraftResource> Resources,
    IReadOnlyList<DefinitionEvaluationScenario> Scenarios, IReadOnlyList<DefinitionEvaluationResult> Results, DefinitionResourceAccess ResourceAccess);
public sealed record TenantPublicationResourceState(IReadOnlyList<AgentDefinitionPublicationResource> Items, bool AllRead);

public sealed partial class TenantDefinitionService
{
    private async ValueTask<TenantSecurityState> ResourceState(Guid actor, Guid tenant, Guid definition, CancellationToken ct)
    {
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        await RequireResourcePermission(state, actor, "agent-definitions.read", Scope(tenant, definition), ct).ConfigureAwait(false);
        return state;
    }
    private async ValueTask<bool> ResourceAllowed(TenantSecurityState state, Guid actor, string permission, AccessScope scope, CancellationToken ct)
    {
        var result = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false);
        if (!result.Allowed && result.ReasonCode != "permission_denied") throw new AccessDeniedException(result.ReasonCode);
        return result.Allowed;
    }
    private async ValueTask RequireResourcePermission(TenantSecurityState state, Guid actor, string permission, AccessScope scope, CancellationToken ct)
    {
        var result = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false);
        if (!result.Allowed) throw new AccessDeniedException(result.ReasonCode);
    }
    private async ValueTask RequireDestinationResources(TenantSecurityState state, Guid actor, int count, CancellationToken ct)
    {
        if (count == 0) return;
        await RequireResourcePermission(state, actor, "resources.read", AccessScope.ForTenant(state.Tenant.TenantId), ct).ConfigureAwait(false);
        await RequireResourcePermission(state, actor, "resources.manage", AccessScope.ForTenant(state.Tenant.TenantId), ct).ConfigureAwait(false);
    }
    private async ValueTask<(AgentDefinitionDraftResource[] Items, bool All)> ReadableDraftResources(TenantSecurityState state, Guid actor, Guid definition, Guid draft,
        IReadOnlyList<AgentDefinitionDraftResource> resources, CancellationToken ct) =>
        await authorizer.VisibleResourcesAsync(state, actor, "resources.read", resources,
            r => TenantDefinitionResourceScopes.Draft(state.Tenant.TenantId, definition, draft, r.ResourceId), ct).ConfigureAwait(false);
    private async ValueTask RequireEvaluationResources(TenantSecurityState state, Guid actor, TenantDefinitionDraftSnapshot snapshot, CancellationToken ct)
    {
        var visible = await ReadableDraftResources(state, actor, Guid.Parse(snapshot.Draft.DefinitionId), snapshot.Draft.DraftId, snapshot.Resources, ct).ConfigureAwait(false);
        if (visible.Items.Length != snapshot.Resources.Count) throw new AccessDeniedException("permission_denied");
    }
    private async ValueTask<AgentDefinitionDraftResourceBatchBound> VisibleResourceMutation(Guid actor, Guid tenant, Guid definition, Guid draft,
        AgentDefinitionDraftResourceBatchBound result, CancellationToken ct)
    {
        var state = await ResourceState(actor, tenant, definition, ct).ConfigureAwait(false);
        var visible = await ReadableDraftResources(state, actor, definition, draft, result.Resources, ct).ConfigureAwait(false);
        return result with { Resources = visible.Items };
    }
}
