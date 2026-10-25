using AgentCore.Domain.Models;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantModelState(Guid TenantId, IReadOnlyList<ProviderConnection> Connections,
    IReadOnlyList<ModelOffering> Offerings, TenantModelSettings Settings);
public sealed record ModelPresetSummary(ModelPreset Preset, int OfferingReferences);

public sealed record ModelDiscoveryEntry(string ExternalModelId, string? Label = null);
public sealed record ModelMetadataResult(bool Success, string Result, IReadOnlyList<ModelDiscoveryEntry> Models);
public sealed record ModelCredentialWitness(Guid? CredentialId, long? Revision, long? SecretRevision);
public sealed record ModelRouteSnapshot(long SecurityRevision, ProviderConnection Connection, ModelOffering Offering, ModelCredentialWitness Credential);
public sealed record ModelCatalogSourceSnapshot(Guid TenantId, long SecurityRevision, TenantModelSettings Settings,
    IReadOnlyList<ModelRouteSnapshot> Routes);

public interface IModelMetadataTransport
{
    ValueTask<ModelMetadataResult> ReadAsync(ProviderConnection connection, ModelCredentialWitness credential,
        bool discover, Func<CancellationToken, ValueTask> authorizeDispatch, CancellationToken ct = default);
}

// Model mutations participate in the incumbent IAM/security-revision/audit transaction.
public interface ITenantModelStore
{
    ValueTask<ModelReferencePage> ReadModelReferencesAsync(ModelReferenceQuery query, CancellationToken ct = default);
    ValueTask<TenantModelState> ReadTenantModelsAsync(Guid tenant, CancellationToken ct = default);
    ValueTask<ModelRouteSnapshot?> FindModelRouteAsync(Guid tenant, Guid offeringId, CancellationToken ct = default);
    ValueTask<ModelCatalogSourceSnapshot> ReadModelCatalogSourceAsync(Guid tenant, CancellationToken ct = default);
    ValueTask<IReadOnlyList<ModelPreset>> ReadModelPresetsAsync(CancellationToken ct = default);
    ValueTask<IReadOnlyList<ModelPresetSummary>> ReadModelPresetSummariesAsync(CancellationToken ct = default);
    ValueTask<bool> TrySaveModelPresetAsync(ModelPreset value, long expectedRevision, HumanSessionReference human,
        AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<bool> TrySaveProviderConnectionAsync(TenantSecurityState next, long expectedSecurityRevision,
        ProviderConnection value, long expectedRevision, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<bool> TrySaveModelOfferingAsync(TenantSecurityState next, long expectedSecurityRevision,
        ModelOffering value, long expectedRevision, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<bool> TrySaveTenantModelSettingsAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantModelSettings value, long expectedRevision, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<bool> TryApplyModelMetadataAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid connectionId, long expectedConnectionRevision, ModelCredentialWitness credential, DateTimeOffset at,
        ModelMetadataResult result, bool discover, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default);
}
