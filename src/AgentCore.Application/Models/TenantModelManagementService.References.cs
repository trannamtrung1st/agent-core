using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Models;

public sealed partial class TenantModelManagementService
{
    public async ValueTask<ModelReferencePage> ReferencesAsync(HumanSessionReference human, Guid tenant, Guid id, bool connection,
        ModelReferenceKind kind, int page = 0, int pageSize = 20, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(kind) || page is < 0 or > 100_000 || pageSize is < 1 or > 100)
            throw AgentCoreErrors.Validation("Model reference paging is invalid.");
        var state = await State(human, tenant, ct);
        await Require(state, human, connection ? "model-connections.read" : "models.read", TenantModelRules.Scope(tenant, id, connection), ct);
        async Task<IReadOnlyList<Guid>?> Visible(string permission, string resource)
        {
            var metadata = PermissionCatalog.Get(permission);
            var all = await authorizer.CheckTenantAsync(state, human.PrincipalId, metadata, AccessScope.ForTenant(tenant), ct);
            if (all.Allowed) return null;
            if (all.ReasonCode != "permission_denied") throw new AccessDeniedException(all.ReasonCode);
            var ids = new List<Guid>();
            foreach (var scope in state.Assignments.Where(a => a.PrincipalId == human.PrincipalId && a.Scope.ResourceType == resource).Select(a => a.Scope).Distinct())
                if (scope.TenantId == tenant && Guid.TryParseExact(scope.ResourceId, "D", out var candidate)
                    && (await authorizer.CheckTenantAsync(state, human.PrincipalId, metadata, scope, ct)).Allowed) ids.Add(candidate);
            return ids.Distinct().ToArray();
        }
        var (permission, type) = kind switch
        {
            ModelReferenceKind.Definitions => ("agent-definitions.read", "definition"),
            ModelReferenceKind.Instances => ("agents.read", "agent"),
            ModelReferenceKind.Automations => ("automations.read", "automation"),
            ModelReferenceKind.Sessions => ("sessions.read", "session"),
            ModelReferenceKind.Runs => ("agent-runs.read", "agent-run"),
            _ => ("models.read", "model-offering")
        };
        var visibility = new ModelReferenceVisibility(kind == ModelReferenceKind.Default ? null : await Visible(permission, type),
            kind is ModelReferenceKind.Automations or ModelReferenceKind.Runs ? await Visible("agents.read", "agent") : null,
            kind == ModelReferenceKind.Runs ? await Visible("sessions.read", "session") : null,
            connection ? await Visible("models.read", "model-offering") : [id]);
        var result = await store.ReadModelReferencesAsync(new(tenant, connection ? null : id, connection ? id : null,
            kind, page, pageSize, visibility), ct);
        if (result.SecurityRevision != state.Tenant.Revision) throw new SecurityConflictException("model_source_changed");
        await Complete(human, tenant, state.Tenant.Revision, ct);
        return result;
    }
}
