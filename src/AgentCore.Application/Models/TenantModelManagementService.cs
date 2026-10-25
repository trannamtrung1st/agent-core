using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Models;

public sealed record ProviderConnectionChange(ModelProviderKind Kind, string Label, string? Endpoint, Guid? CredentialId,
    bool NoCredential, bool Enabled);
public sealed record ModelOfferingChange(Guid ConnectionId, Guid? PresetId, string ExternalModelId, string Label,
    ModelTransport Transport, ModelCapabilityProfile Capabilities, bool Enabled, bool Retired, bool RestoreAvailability = false);
public sealed record ModelAdministrationAccess(bool CanCreateConnection, bool CanCreateOffering, bool CanManageDefault,
    IReadOnlyList<Guid> ManageConnectionIds, IReadOnlyList<Guid> TestConnectionIds, IReadOnlyList<Guid> ManageOfferingIds);
public sealed record ModelOfferingAvailability(Guid OfferingId, bool Available, string Reason);
public sealed record TenantModelAdminView(IReadOnlyList<ProviderConnection> Connections, IReadOnlyList<ModelOffering> Offerings,
    TenantModelSettings Settings, IReadOnlyList<ModelPreset> Presets, long SecurityRevision, IReadOnlyList<ModelOfferingAvailability> Availability, ModelAdministrationAccess Access);

public sealed partial class TenantModelManagementService(IIamStore security, ITenantModelStore store, ITenantCredentialStore credentials,
    IdentityService identity, IamAuthorizer authorizer, ModelEndpointPolicy endpoints, IIdGenerator ids, TimeProvider time,
    IModelMetadataTransport? metadata = null)
{
    public async ValueTask<TenantModelAdminView> ReadAsync(HumanSessionReference human, Guid tenant, CancellationToken ct = default)
    {
        var state = await State(human, tenant, ct);
        var modelState = await store.ReadTenantModelsAsync(tenant, ct);
        var offerings = await authorizer.VisibleResourcesAsync(state, human.PrincipalId, "models.read", modelState.Offerings,
            o => TenantModelRules.Scope(tenant, o.OfferingId), ct);
        var connections = await authorizer.VisibleResourcesAsync(state, human.PrincipalId, "model-connections.read", modelState.Connections,
            c => TenantModelRules.Scope(tenant, c.ConnectionId, true), ct);
        var presets = await store.ReadModelPresetsAsync(ct);
        var source = await store.ReadModelCatalogSourceAsync(tenant, ct);
        if (source.TenantId != tenant || source.SecurityRevision != state.Tenant.Revision)
            throw new SecurityConflictException("model_source_changed");
        var availability = offerings.Items.Select(offering =>
        {
            var connection = modelState.Connections.SingleOrDefault(c => c.ConnectionId == offering.ConnectionId);
            var reason = offering.Retired ? "retired" : !offering.Enabled ? "disabled"
                : connection is not { Enabled: true } ? "connection_unavailable"
                : offering.Observation == ModelObservation.NotReturned ? "not_returned"
                : !source.Routes.Any(r => r.Offering.OfferingId == offering.OfferingId) ? "credential_unavailable"
                : !ModelRouteRules.Supports(offering.Capabilities, offering.Kind, ModelPurpose.Conversation) ? "capabilities_unverified" : "available";
            return new ModelOfferingAvailability(offering.OfferingId, reason == "available", reason);
        }).ToArray();
        async Task<bool> Allowed(string permission, AccessScope scope) =>
            (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(permission), scope, ct)).Allowed;
        var manageConnections = new List<Guid>(); var testConnections = new List<Guid>(); var manageOfferings = new List<Guid>();
        foreach (var connection in connections.Items)
        {
            var scope = TenantModelRules.Scope(tenant, connection.ConnectionId, true);
            if (await Allowed("model-connections.manage", scope)) manageConnections.Add(connection.ConnectionId);
            if (await Allowed("model-connections.test", scope)) testConnections.Add(connection.ConnectionId);
        }
        foreach (var offering in offerings.Items)
            if (await Allowed("models.manage", TenantModelRules.Scope(tenant, offering.OfferingId))) manageOfferings.Add(offering.OfferingId);
        var tenantScope = AccessScope.ForTenant(tenant);
        var access = new ModelAdministrationAccess(await Allowed("model-connections.manage", tenantScope),
            await Allowed("models.manage", tenantScope), await Allowed("models.manage", tenantScope), manageConnections, testConnections, manageOfferings);
        await Complete(human, tenant, state.Tenant.Revision, ct);
        var settings = offerings.All || modelState.Settings.DefaultOfferingId is { } selected && offerings.Items.Any(o => o.OfferingId == selected)
            ? modelState.Settings : modelState.Settings with { DefaultOfferingId = null, DefaultReasoningEffort = null };
        return new(connections.Items, offerings.Items, settings, presets.Where(p => p.Enabled && !p.Archived).ToArray(), state.Tenant.Revision, availability, access);
    }
    public async ValueTask<ProviderConnection> SaveConnectionAsync(HumanSessionReference human, Guid tenant, Guid? id,
        ProviderConnectionChange change, long expectedSecurityRevision, long expectedRevision, CancellationToken ct = default)
    {
        var state = await WriteState(human, tenant, id is { } key ? TenantModelRules.Scope(tenant, key, true) : AccessScope.ForTenant(tenant),
            "model-connections.manage", expectedSecurityRevision, ct);
        var models = await store.ReadTenantModelsAsync(tenant, ct);
        var old = id is { } existing ? models.Connections.SingleOrDefault(c => c.ConnectionId == existing) ?? throw new AccessDeniedException("resource_unavailable") : null;
        RequireRevision(old?.Revision ?? 0, expectedRevision);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds()); var connectionId = old?.ConnectionId ?? ids.NewId();
        var value = Validate(() => ModelManagementRules.Freeze(new ProviderConnection(connectionId, tenant, change.Kind,
            change.Label, endpoints.Validate(change.Kind, change.Endpoint, change.NoCredential), change.CredentialId, change.NoCredential,
            change.Enabled, checked(expectedRevision + 1), old?.CreatedAt ?? now, now)));
        TenantModelRules.ConnectionChange(value, old, expectedRevision);
        if (value.CredentialId is { } credentialId)
        {
            // Referencing a credential requires independently current human read/manage authority.
            await Require(state, human, "credentials.read", TenantCredentialRules.Scope(tenant, credentialId), ct);
            await Require(state, human, "credentials.manage", TenantCredentialRules.Scope(tenant, credentialId), ct);
            TenantModelRules.RequireCredential(value, await credentials.FindTenantCredentialAsync(tenant, credentialId, ct), requireActive: value.Enabled);
        }
        if (!await store.TrySaveProviderConnectionAsync(Advance(state), expectedSecurityRevision, value, expectedRevision, human,
            Audit(human, tenant, "model-connections.manage", connectionId), ct)) throw new SecurityConflictException("stale_revision");
        return value;
    }
    public async ValueTask<ModelOffering> SaveOfferingAsync(HumanSessionReference human, Guid tenant, Guid? id,
        ModelOfferingChange change, long expectedSecurityRevision, long expectedRevision, CancellationToken ct = default)
    {
        var state = await WriteState(human, tenant, id is { } key ? TenantModelRules.Scope(tenant, key) : AccessScope.ForTenant(tenant),
            "models.manage", expectedSecurityRevision, ct);
        var models = await store.ReadTenantModelsAsync(tenant, ct);
        var old = id is { } existing ? models.Offerings.SingleOrDefault(o => o.OfferingId == existing) ?? throw new AccessDeniedException("resource_unavailable") : null;
        RequireRevision(old?.Revision ?? 0, expectedRevision);
        var connection = models.Connections.SingleOrDefault(c => c.ConnectionId == change.ConnectionId) ?? throw new AccessDeniedException("resource_unavailable");
        await Require(state, human, "model-connections.read", TenantModelRules.Scope(tenant, connection.ConnectionId, true), ct);
        var preset = change.PresetId is { } presetId ? (await store.ReadModelPresetsAsync(ct)).SingleOrDefault(p => p.PresetId == presetId) : null;
        if (old is null && change.PresetId is not null && preset is not { Enabled: true, Archived: false }) throw AgentCoreErrors.Validation("Select an enabled model preset.");
        if (change.RestoreAvailability && (old?.Observation != ModelObservation.NotReturned || !change.Enabled || change.Retired))
            throw AgentCoreErrors.Validation("Availability recovery requires an existing enabled, non-retired offering not returned by discovery.");
        var capabilities = old is null && preset is not null ? preset.Capabilities : change.Capabilities with { Source = ModelMetadataSource.Manual, VerifiedAt = time.GetUtcNow() };
        var value = Validate(() => ModelManagementRules.Freeze(new ModelOffering(old?.OfferingId ?? ids.NewId(), tenant, connection.ConnectionId,
            connection.Kind, change.PresetId, change.ExternalModelId, change.Label, change.Transport, capabilities,
            change.Enabled, change.Retired, checked(expectedRevision + 1), change.RestoreAvailability ? ModelObservation.Unknown : old?.Observation ?? ModelObservation.Unknown, old?.LastSeenAt)));
        TenantModelRules.OfferingChange(value, old, expectedRevision, connection, preset);
        if (!await store.TrySaveModelOfferingAsync(Advance(state), expectedSecurityRevision, value, expectedRevision, human,
            Audit(human, tenant, "models.manage", value.OfferingId) with { ReasonCode = change.RestoreAvailability ? "model_availability_restored" : "model_configuration_changed" }, ct)) throw new SecurityConflictException("stale_revision");
        return value;
    }
    public async ValueTask<TenantModelSettings> SaveDefaultAsync(HumanSessionReference human, Guid tenant, Guid? offering,
        string? effort, long expectedSecurityRevision, long expectedRevision, CancellationToken ct = default)
    {
        var state = await WriteState(human, tenant, AccessScope.ForTenant(tenant), "models.manage", expectedSecurityRevision, ct);
        var value = Validate(() => ModelManagementRules.Freeze(new TenantModelSettings(tenant, offering, effort, checked(expectedRevision + 1))));
        if (!await store.TrySaveTenantModelSettingsAsync(Advance(state), expectedSecurityRevision, value, expectedRevision, human,
            Audit(human, tenant, "models.manage", tenant), ct)) throw new SecurityConflictException("stale_revision");
        return value;
    }
    private ValueTask<TenantSecurityState> State(HumanSessionReference human, Guid tenant, CancellationToken ct) =>
        TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct);
    private async ValueTask<TenantSecurityState> WriteState(HumanSessionReference human, Guid tenant, AccessScope scope, string permission, long expected, CancellationToken ct)
    {
        var state = await State(human, tenant, ct); await Require(state, human, permission, scope, ct);
        if (state.Tenant.Revision != expected) throw new SecurityConflictException("stale_revision");
        return state;
    }
    private async ValueTask Require(TenantSecurityState state, HumanSessionReference human, string permission, AccessScope scope, CancellationToken ct)
    {
        var decision = await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(permission), scope, ct);
        if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
    }
    private async ValueTask Complete(HumanSessionReference human, Guid tenant, long expected, CancellationToken ct)
    { if ((await State(human, tenant, ct)).Tenant.Revision != expected) throw new SecurityConflictException("stale_revision"); }
    private static void RequireRevision(long current, long expected) { if (expected < 0 || current != expected) throw new SecurityConflictException("stale_revision"); }
    private static TenantSecurityState Advance(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
    private AuthorizationAudit Audit(HumanSessionReference human, Guid tenant, string action, Guid id) => new(ids.NewId(), tenant, human.PrincipalId, action, id.ToString("D"), "allowed", "model_configuration_changed", time.GetUtcNow());
    private static T Validate<T>(Func<T> action) { try { return action(); } catch (ArgumentException e) { throw AgentCoreErrors.Validation(e.Message); } }
}
