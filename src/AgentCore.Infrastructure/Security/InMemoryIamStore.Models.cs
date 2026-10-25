using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantModelStore
{
    private readonly Dictionary<Guid, ModelPreset> modelPresets = BuiltinModelPresets.All.ToDictionary(p => p.PresetId);
    private readonly Dictionary<Guid, ProviderConnection> modelConnections = [];
    private readonly Dictionary<Guid, ModelOffering> modelOfferings = [];
    private readonly Dictionary<Guid, TenantModelSettings> modelSettings = [];
    private readonly IIdGenerator modelIdSource = new AgentCore.Infrastructure.Identity.SystemIdGenerator(clock ?? TimeProvider.System);

    public ValueTask<ModelCatalogSourceSnapshot> ReadModelCatalogSourceAsync(Guid tenant, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate)
        {
            if (!_tenants.TryGetValue(tenant, out var state) || state.Tenant.Status != IdentityStatus.Active)
                throw new AccessDeniedException("tenant_unavailable");
            var routes = new List<ModelRouteSnapshot>();
            foreach (var offering in modelOfferings.Values.Where(o => o.TenantId == tenant))
            {
                var connection = modelConnections.GetValueOrDefault(offering.ConnectionId);
                if (connection?.TenantId != tenant) continue;
                var credential = connection.CredentialId is { } id ? credentials?.ReadTenantCredential(tenant, id) : null;
                try { TenantModelRules.RequireCredential(connection, credential); }
                catch (AccessDeniedException) { continue; }
                routes.Add(new(state.Tenant.Revision, connection, offering,
                    new(connection.CredentialId, credential?.Value.Revision, credential?.Value.SecretRevision)));
            }
            return ValueTask.FromResult(new ModelCatalogSourceSnapshot(tenant, state.Tenant.Revision,
                modelSettings.GetValueOrDefault(tenant) ?? new(tenant, null, null, 0), Array.AsReadOnly(routes.ToArray())));
        }
    }

    public ValueTask<ModelRouteSnapshot?> FindModelRouteAsync(Guid tenant, Guid offeringId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate)
        {
            var offering = modelOfferings.GetValueOrDefault(offeringId);
            var connection = offering is null ? null : modelConnections.GetValueOrDefault(offering.ConnectionId);
            if (offering?.TenantId != tenant || connection?.TenantId != tenant || !_tenants.TryGetValue(tenant, out var state)) return ValueTask.FromResult<ModelRouteSnapshot?>(null);
            var credential = connection.CredentialId is { } id ? credentials?.ReadTenantCredential(tenant, id) : null;
            TenantModelRules.RequireCredential(connection, credential);
            return ValueTask.FromResult<ModelRouteSnapshot?>(new(state.Tenant.Revision, connection, offering,
                new(connection.CredentialId, credential?.Value.Revision, credential?.Value.SecretRevision)));
        }
    }

    public ValueTask<TenantModelState> ReadTenantModelsAsync(Guid tenant, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (tenant == Guid.Empty) throw new ArgumentException("Tenant is required.");
        lock (_gate) return ValueTask.FromResult(new TenantModelState(tenant,
            Array.AsReadOnly(modelConnections.Values.Where(c => c.TenantId == tenant).OrderBy(c => c.ConnectionId).ToArray()),
            Array.AsReadOnly(modelOfferings.Values.Where(o => o.TenantId == tenant).OrderBy(o => o.OfferingId).ToArray()),
            modelSettings.GetValueOrDefault(tenant) ?? new(tenant, null, null, 0)));
    }
    public ValueTask<IReadOnlyList<ModelPreset>> ReadModelPresetsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
            return ValueTask.FromResult<IReadOnlyList<ModelPreset>>(Array.AsReadOnly(modelPresets.Values.OrderBy(p => p.PresetId).ToArray()));
    }
    public ValueTask<IReadOnlyList<ModelPresetSummary>> ReadModelPresetSummariesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var references = modelOfferings.Values.Where(o => o.PresetId is not null).GroupBy(o => o.PresetId!.Value)
                .ToDictionary(g => g.Key, g => g.Count());
            return ValueTask.FromResult<IReadOnlyList<ModelPresetSummary>>(Array.AsReadOnly(modelPresets.Values.OrderBy(p => p.PresetId)
                .Select(p => new ModelPresetSummary(p, references.GetValueOrDefault(p.PresetId))).ToArray()));
        }
    }
    public ValueTask<bool> TrySaveModelPresetAsync(ModelPreset value, long expectedRevision, HumanSessionReference human,
        AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); value = ModelManagementRules.Freeze(value);
        TenantModelRules.Audit(human, audit, null, "system.model-presets.manage", value.PresetId);
        lock (_gate)
        {
            RequireSystemHuman(human, "system.model-presets.manage", ct);
            var old = modelPresets.GetValueOrDefault(value.PresetId);
            if ((old?.Revision ?? 0) != expectedRevision || _audit.Any(a => a.AuditId == audit.AuditId)) return ValueTask.FromResult(false);
            if (value.Revision != checked(expectedRevision + 1) || old is not null && (old.Kind != value.Kind || old.ExternalModelId != value.ExternalModelId))
                throw new ArgumentException("Preset identity is immutable.");
            if (modelPresets.Values.Any(p => p.PresetId != value.PresetId && p.Kind == value.Kind && p.ExternalModelId == value.ExternalModelId)) return ValueTask.FromResult(false);
            modelPresets[value.PresetId] = value; _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
    public ValueTask<bool> TrySaveProviderConnectionAsync(TenantSecurityState next, long expectedSecurityRevision,
        ProviderConnection value, long expectedRevision, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        value = ModelManagementRules.Freeze(value);
        TenantModelRules.Audit(human, audit, value.TenantId, "model-connections.manage", value.ConnectionId);
        return ValueTask.FromResult(WriteModel(next, expectedSecurityRevision, value.TenantId, human, audit, () =>
        {
            var old = modelConnections.GetValueOrDefault(value.ConnectionId);
            if ((old?.Revision ?? 0) != expectedRevision) return false;
            TenantModelRules.ConnectionChange(value, old, expectedRevision);
            if (!value.Enabled && modelSettings.GetValueOrDefault(value.TenantId)?.DefaultOfferingId is { } defaultId
                && modelOfferings.GetValueOrDefault(defaultId)?.ConnectionId == value.ConnectionId) throw new SecurityConflictException("model_connection_is_default");
            TenantModelRules.RequireCredential(value, value.CredentialId is { } id ? credentials?.ReadTenantCredential(value.TenantId, id) : null, requireActive: value.Enabled);
            modelConnections[value.ConnectionId] = value; return true;
        }, ct));
    }
    public ValueTask<bool> TrySaveModelOfferingAsync(TenantSecurityState next, long expectedSecurityRevision,
        ModelOffering value, long expectedRevision, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        value = ModelManagementRules.Freeze(value);
        TenantModelRules.Audit(human, audit, value.TenantId, "models.manage", value.OfferingId);
        return ValueTask.FromResult(WriteModel(next, expectedSecurityRevision, value.TenantId, human, audit, () =>
        {
            var old = modelOfferings.GetValueOrDefault(value.OfferingId);
            if ((old?.Revision ?? 0) != expectedRevision) return false;
            TenantModelRules.OfferingChange(value, old, expectedRevision, modelConnections.GetValueOrDefault(value.ConnectionId),
                value.PresetId is { } id ? modelPresets.GetValueOrDefault(id) : null);
            if ((!value.Enabled || value.Retired) && modelSettings.GetValueOrDefault(value.TenantId)?.DefaultOfferingId == value.OfferingId)
                throw new SecurityConflictException("model_offering_is_default");
            if (modelSettings.GetValueOrDefault(value.TenantId) is { } settings && settings.DefaultOfferingId == value.OfferingId)
            {
                var connection = modelConnections.GetValueOrDefault(value.ConnectionId);
                TenantModelRules.RequireDefault(settings, value, connection, connection?.CredentialId is { } credential
                    ? credentials?.ReadTenantCredential(value.TenantId, credential) : null);
            }
            if (modelOfferings.Values.Any(o => o.OfferingId != value.OfferingId && o.TenantId == value.TenantId && o.ConnectionId == value.ConnectionId && o.ExternalModelId == value.ExternalModelId)) return false;
            modelOfferings[value.OfferingId] = value; return true;
        }, ct));
    }
    public ValueTask<bool> TrySaveTenantModelSettingsAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantModelSettings value, long expectedRevision, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        value = ModelManagementRules.Freeze(value);
        TenantModelRules.Audit(human, audit, value.TenantId, "models.manage", value.TenantId);
        return ValueTask.FromResult(WriteModel(next, expectedSecurityRevision, value.TenantId, human, audit, () =>
        {
            if ((modelSettings.GetValueOrDefault(value.TenantId)?.Revision ?? 0) != expectedRevision) return false;
            if (value.Revision != checked(expectedRevision + 1)) throw new ArgumentException("Invalid model settings revision.");
            var offering = value.DefaultOfferingId is { } id ? modelOfferings.GetValueOrDefault(id) : null;
            var connection = offering is null ? null : modelConnections.GetValueOrDefault(offering.ConnectionId);
            TenantModelRules.RequireDefault(value, offering, connection, connection?.CredentialId is { } credential ? credentials?.ReadTenantCredential(value.TenantId, credential) : null);
            modelSettings[value.TenantId] = value; return true;
        }, ct));
    }
    private bool WriteModel(TenantSecurityState next, long expected, Guid tenant, HumanSessionReference human,
        AuthorizationAudit audit, Func<bool> change, CancellationToken ct)
    {
        if (next.Tenant.TenantId != tenant || audit.ActorPrincipalId != human.PrincipalId) throw new ArgumentException("Invalid model owner.");
        return WriteTenantResource(next, expected, audit, () =>
        {
            // The incumbent transaction holds the IAM and credential gates together.
            RequireAgentHuman(tenant, human.PrincipalId, human);
            if (_audit.Any(a => a.AuditId == audit.AuditId)) return false;
            return change();
        }, ct, human);
    }
    public ValueTask<bool> TryApplyModelMetadataAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid connectionId, long expectedConnectionRevision, ModelCredentialWitness credential, DateTimeOffset at,
        ModelMetadataResult result, bool discover, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        var observed = ModelDiscoveryReconciliation.Freeze(result);
        TenantModelRules.Audit(human, audit, next.Tenant.TenantId, "model-connections.test", connectionId);
        return ValueTask.FromResult(WriteModel(next, expectedSecurityRevision, next.Tenant.TenantId, human, audit, () =>
        {
            var connection = modelConnections.GetValueOrDefault(connectionId);
            if (connection is null || connection.TenantId != next.Tenant.TenantId || connection.Revision != expectedConnectionRevision || !connection.Enabled) return false;
            var current = connection.CredentialId is { } id ? credentials?.ReadTenantCredential(connection.TenantId, id) : null;
            TenantModelRules.RequireCredential(connection, current);
            if (credential != new ModelCredentialWitness(connection.CredentialId, current?.Value.Revision, current?.Value.SecretRevision)) return false;
            var previous = modelOfferings.Values.Where(o => o.TenantId == connection.TenantId && o.ConnectionId == connectionId).ToArray();
            var updated = result.Success && discover ? ModelDiscoveryReconciliation.Reconcile(connection, previous, observed, at, modelIdSource.NewId, result.Result == "synthetic_metadata_available") : previous;
            // Observational metadata never changes a model/configuration revision or tenant enablement.
            foreach (var offering in updated) modelOfferings[offering.OfferingId] = offering;
            modelConnections[connectionId] = connection with { LastTestAt = at, LastTestResult = result.Result };
            return true;
        }, ct));
    }
    private bool OwnsModelResource(AccessScope scope) => TenantModelRules.TryId(scope, out var id)
        && (scope.ResourceType == "model-connection" ? modelConnections.GetValueOrDefault(id)?.TenantId == scope.TenantId
            : modelOfferings.GetValueOrDefault(id)?.TenantId == scope.TenantId);
}
