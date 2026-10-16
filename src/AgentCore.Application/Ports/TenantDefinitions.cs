using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantDefinition(Guid TenantId, Guid DefinitionId, DateTimeOffset CreatedAt);
public sealed record TenantDefinitionSummary(TenantDefinition Definition, int DraftCount, int? LatestVersion, string DisplayName);
public sealed record TenantDefinitionPage(IReadOnlyList<TenantDefinitionSummary> Items, int Total, int Page, int PageSize);

// Ownership belongs to the definition aggregate. Drafts, publications and resource/evaluation
// children remain in their existing stores; canonical mutations commit with security CAS and audit.
public interface ITenantDefinitionStore
{
    ValueTask<TenantDefinition?> FindDefinitionAsync(Guid tenantId, Guid definitionId, CancellationToken ct = default);
    ValueTask<TenantDefinitionPage> ListDefinitionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AgentDefinitionDraft>> ListDefinitionDraftsAsync(Guid tenantId, Guid definitionId, CancellationToken ct = default);
    ValueTask<AgentDefinitionDraft?> FindDefinitionDraftAsync(Guid tenantId, Guid draftId, CancellationToken ct = default);
    ValueTask<AgentDefinitionPublication?> FindDefinitionPublicationAsync(Guid tenantId, Guid definitionId, int version, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AgentDefinitionPublicationSummary>> ListDefinitionPublicationsAsync(Guid tenantId, Guid definitionId, CancellationToken ct = default);
    ValueTask<bool> TryCreateDefinitionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantDefinition definition,
        AgentDefinitionDraft draft, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<bool> TrySaveDefinitionDraftAsync(TenantSecurityState next, long expectedSecurityRevision,
        AgentDefinitionDraft draft, long? expectedDraftRevision, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<AgentDefinitionPublication?> TryPublishDefinitionDraftAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default);
    ValueTask<AgentDefinitionPublication?> TryDeprecateDefinitionPublicationAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid definitionId, int version, long expectedMetadataRevision, AuthorizationAudit audit, CancellationToken ct = default);
}
