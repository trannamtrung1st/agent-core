using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Models;

public sealed class TenantModelCatalogReader(ITenantModelStore models, IIamStore security) : ITenantModelCatalogReader
{
    public async ValueTask<IModelCatalog> ReadAsync(TenantSecurityState authorizedState, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorizedState);
        if (authorizedState.Tenant.Status != IdentityStatus.Active) throw new AccessDeniedException("tenant_unavailable");
        var tenant = authorizedState.Tenant.TenantId;
        var source = await models.ReadModelCatalogSourceAsync(tenant, ct).ConfigureAwait(false);
        var current = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false);
        if (current?.Tenant.Status != IdentityStatus.Active) throw new AccessDeniedException("tenant_unavailable");
        if (source.TenantId != tenant || source.SecurityRevision != authorizedState.Tenant.Revision
            || current.Tenant.Revision != source.SecurityRevision) throw new SecurityConflictException("model_source_changed");
        return new TenantModelCatalog(source);
    }
}

// An immutable value read from one tenant transaction. No ambient tenant or cross-request client cache.
public sealed class TenantModelCatalog : IModelCatalog
{
    public TenantModelCatalog(ModelCatalogSourceSnapshot source)
    {
        TenantId = source.TenantId; SecurityRevision = source.SecurityRevision;
        Settings = source.Settings with { };
        Models = Array.AsReadOnly(source.Routes.Where(r => r.Connection.Enabled && r.Offering.Enabled && !r.Offering.Retired && r.Offering.Observation != ModelObservation.NotReturned
                && ModelRouteRules.Supports(r.Offering.Capabilities, r.Connection.Kind, ModelPurpose.Conversation))
            .Select(r =>
            {
                if (r.Offering.TenantId != TenantId || r.Connection.TenantId != TenantId || r.SecurityRevision != SecurityRevision)
                    throw new AccessDeniedException("model_route_scope_mismatch");
                return ModelDescriptor.FromBinding(new(ModelRouteRules.Pin(r), r.Offering.Capabilities, r.Offering.Label, r.Connection.Label));
            }).OrderBy(d => d.DisplayName, StringComparer.Ordinal).ThenBy(d => d.OfferingId, StringComparer.Ordinal).ToArray());
        DefaultOfferingId = source.Settings.DefaultOfferingId?.ToString("D") ?? "";
    }
    public AutoRoutingPolicy RoutingPolicy => Settings.Routing ?? new();
    public long RoutingRevision => Settings.Revision;
    public Guid TenantId { get; }
    public long SecurityRevision { get; }
    public TenantModelSettings Settings { get; }
    public string DefaultOfferingId { get; }
    public string? DefaultReasoningEffort => Settings.DefaultReasoningEffort ?? Get(DefaultOfferingId)?.DefaultReasoningEffort;
    public IReadOnlyList<ModelDescriptor> Models { get; }
    public ModelDescriptor Default => Get(DefaultOfferingId) ?? throw new AgentCoreException("ModelUnavailable",
        "Configure an enabled model connection and choose a tenant default before starting a conversation.", 409);
    public ModelDescriptor? Get(string key) => Models.FirstOrDefault(d => d.OfferingId == key);
}
