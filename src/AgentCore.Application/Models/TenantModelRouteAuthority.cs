using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Models;

public sealed class TenantModelRouteAuthority(IIamStore security, ITenantModelStore models) : ITenantModelRouteAuthority
{
    public async ValueTask<ModelRouteSnapshot> RequireAsync(ModelRoutePin route, ModelInvocation invocation, ModelPurpose purpose, CancellationToken ct = default)
    {
        if (invocation is null || invocation.Context.TenantId != route.TenantId || invocation.Route != route || !Enum.IsDefined(purpose))
            throw new AccessDeniedException("model_route_scope_mismatch");
        await invocation.RequireCurrent(ct).ConfigureAwait(false);
        var current = await models.FindModelRouteAsync(route.TenantId, route.OfferingId, ct).ConfigureAwait(false)
            ?? throw new AgentCoreException("ModelUnavailable", "The admitted model is unavailable. Configure a connection and select an eligible model for a new Run.", 409);
        var offering = current.Offering; var connection = current.Connection;
        if (!offering.Enabled || offering.Retired || offering.Observation == ModelObservation.NotReturned || !connection.Enabled)
            throw new AgentCoreException("ModelUnavailable", "The admitted model or connection is unavailable.", 409);
        var witness = ModelRouteRules.Pin(current);
        if (witness != route) throw new AgentCoreException("ModelRouteStale", "The admitted model route changed. Select a current model for a new Run.", 409);
        if (!ModelRouteRules.Supports(offering.Capabilities, connection.Kind, purpose))
            throw new AgentCoreException("ModelCapabilityUnsupported", "This model has not declared the required instruction, tool and semantic-output contract.", 409);
        await invocation.RequireCurrent(ct).ConfigureAwait(false);
        var state = await security.ReadTenantAsync(route.TenantId, ct).ConfigureAwait(false);
        if (state?.Tenant.Status != IdentityStatus.Active) throw new AccessDeniedException("tenant_unavailable");
        if (state.Tenant.Revision != current.SecurityRevision) throw new SecurityConflictException("model_source_changed");
        return current;
    }
}
