using AgentCore.Application.Ports;
using AgentCore.Application.Models;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Providers.Synthetic;

// Explicit offline composition only. Real composition never resolves this reader.
public sealed class SyntheticTenantModelCatalogReader(IIamStore security, ITenantModelStore models) : ITenantModelCatalogReader
{
    public SyntheticTenantModelCatalogReader(IIamStore security)
        : this(security, security as ITenantModelStore ?? throw new ArgumentException("A tenant model store is required.", nameof(security))) { }

    public async ValueTask<IModelCatalog> ReadAsync(TenantSecurityState authorizedState, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorizedState);
        var source = await models.ReadModelCatalogSourceAsync(authorizedState.Tenant.TenantId, ct).ConfigureAwait(false);
        var current = await security.ReadTenantAsync(authorizedState.Tenant.TenantId, ct).ConfigureAwait(false);
        if (current?.Tenant.Status != IdentityStatus.Active || authorizedState.Tenant.Status != IdentityStatus.Active)
            throw new AccessDeniedException("tenant_unavailable");
        if (source.TenantId != authorizedState.Tenant.TenantId || source.SecurityRevision != authorizedState.Tenant.Revision
            || current.Tenant.Revision != source.SecurityRevision) throw new SecurityConflictException("model_source_changed");
        return new OfflineCatalog(new TenantModelCatalog(source), SyntheticModelCatalog.Create(source.TenantId));
    }

    // One immutable selection projection in the explicitly selected offline profile.
    // Once tenant settings exist, even an intentionally cleared or unavailable default
    // must remain unavailable. A failed tenant route never selects a fixture instead.
    private sealed class OfflineCatalog : IModelCatalog
    {
        private readonly string? defaultEffort;
        public OfflineCatalog(TenantModelCatalog tenant, IModelCatalog fixture)
        {
            RoutingPolicy = tenant.RoutingPolicy; RoutingRevision = tenant.RoutingRevision;
            Models = Array.AsReadOnly(tenant.Models.Concat(fixture.Models).ToArray());
            DefaultOfferingId = tenant.Settings.Revision == 0 ? fixture.DefaultOfferingId : tenant.DefaultOfferingId;
            defaultEffort = tenant.Settings.Revision == 0 ? fixture.DefaultReasoningEffort : tenant.DefaultReasoningEffort;
        }
        public AgentCore.Domain.Models.AutoRoutingPolicy RoutingPolicy { get; }
        public long RoutingRevision { get; }
        public string DefaultOfferingId { get; }
        public string? DefaultReasoningEffort => defaultEffort;
        public IReadOnlyList<ModelDescriptor> Models { get; }
        public ModelDescriptor Default => Get(DefaultOfferingId) ?? throw new AgentCoreException("ModelUnavailable",
            "Configure an enabled model connection and choose a tenant default before starting a conversation.", 409);
        public ModelDescriptor? Get(string key) => Models.FirstOrDefault(model => model.OfferingId == key);
    }
}
