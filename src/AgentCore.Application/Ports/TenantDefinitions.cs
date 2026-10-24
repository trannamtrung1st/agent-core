using AgentCore.Domain.Definitions;
using AgentCore.Application.Admin;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantDefinition(Guid TenantId, Guid DefinitionId, DateTimeOffset CreatedAt, DateTimeOffset? DeletedAt = null);
public sealed record TenantDefinitionSummary(TenantDefinition Definition, int DraftCount, int? LatestVersion, string DisplayName);
public sealed record TenantDefinitionPage(IReadOnlyList<TenantDefinitionSummary> Items, int Total, int Page, int PageSize);

public sealed record TenantDefinitionDraftSnapshot(AgentDefinitionDraft Draft, IReadOnlyList<AgentDefinitionDraftResource> Resources,
    IReadOnlyList<DefinitionEvaluationScenario> Scenarios, IReadOnlyList<DefinitionEvaluationResult> Results);
public sealed record TenantDefinitionResourceUpload(Guid? ResourceId, string LogicalPath, AgentDefinitionResourceKind Kind, string MediaType, byte[]? Content);
public sealed record TenantDefinitionResourceContent(string LogicalPath, string MediaType, byte[] Content);
public sealed record TenantDefinitionScenarioSaved(DefinitionEvaluationScenario Scenario, long Revision);
public sealed record TenantDefinitionDraftRevision(long Revision);
public sealed record TenantDefinitionDeleted(bool DefinitionRetired);
public sealed record BuiltInDefinitionResource(string LogicalPath, AgentDefinitionResourceKind Kind, string MediaType, byte[] Content);
public sealed record BuiltInDefinitionTemplate(AgentDefinition Definition, IReadOnlyList<BuiltInDefinitionResource> Resources);

// Shipped immutable definitions plus explicitly approved bytes only. Never resolve the tenant
// or global durable publication store as a fallback for a missing shipped source.
public interface IBuiltInDefinitionTemplateSource
{
    ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default);
    ValueTask<BuiltInDefinitionTemplate?> ReadAsync(string templateId, int version, CancellationToken ct = default);
}

// Ownership belongs to the definition aggregate. Drafts, publications and resource/evaluation
// children remain in their existing stores; canonical mutations commit with security CAS and audit.
public interface ITenantDefinitionStore
{
    ValueTask<TenantDefinitionDeleted?> TryDeleteDefinitionDraftAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, bool retireDefinition, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
    ValueTask<TenantDefinitionDeleted?> TryDeleteDefinitionAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid definitionId, AdminDefinitionDeleteWitness witness, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
    ValueTask<TenantDefinitionDraftSnapshot?> ReadDefinitionDraftSnapshotAsync(Guid tenantId, Guid draftId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> ListOwnedPublicationResourcesAsync(Guid tenantId, Guid definitionId, int version, CancellationToken ct = default);
    ValueTask<AgentDefinitionDraftResourceBatchBound?> TryBindDefinitionResourcesAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, IReadOnlyList<AgentDefinitionDraftResource> resources, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
    ValueTask<AgentDefinitionDraftResourceBatchBound?> TryRemoveDefinitionResourceAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, Guid resourceId, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
    ValueTask<TenantDefinitionScenarioSaved?> TrySaveDefinitionScenarioAsync(TenantSecurityState next, long expectedSecurityRevision,
        long expectedDraftRevision, DefinitionEvaluationScenarioUpsert scenario, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
    ValueTask<TenantDefinitionDraftRevision?> TryRemoveDefinitionScenarioAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, string scenarioId, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
    ValueTask<DefinitionEvaluationResult?> TrySaveDefinitionEvaluationAsync(TenantSecurityState next, long expectedSecurityRevision,
        DefinitionEvaluationResult result, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
    ValueTask<TenantDefinition?> FindDefinitionAsync(Guid tenantId, Guid definitionId, CancellationToken ct = default);
    ValueTask<TenantDefinitionPage> ListDefinitionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AgentDefinitionDraft>> ListDefinitionDraftsAsync(Guid tenantId, Guid definitionId, CancellationToken ct = default);
    ValueTask<AgentDefinitionDraft?> FindDefinitionDraftAsync(Guid tenantId, Guid draftId, CancellationToken ct = default);
    ValueTask<AgentDefinitionPublication?> FindDefinitionPublicationAsync(Guid tenantId, Guid definitionId, int version, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AgentDefinitionPublicationSummary>> ListDefinitionPublicationsAsync(Guid tenantId, Guid definitionId, CancellationToken ct = default);
    ValueTask<bool> TryCreateDefinitionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantDefinition definition,
        AgentDefinitionDraft draft, IReadOnlyList<AgentDefinitionDraftResource> initialResources, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
    ValueTask<bool> TrySaveDefinitionDraftAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentDefinitionDraft draft, long? expectedDraftRevision, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
    ValueTask<AgentDefinitionPublication?> TryPublishDefinitionDraftAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
    ValueTask<AgentDefinitionPublication?> TryDeprecateDefinitionPublicationAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid definitionId, int version, long expectedMetadataRevision, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null);
}
