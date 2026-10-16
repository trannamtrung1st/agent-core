using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantDefinitionDraftSnapshot?> ReadDefinitionDraftSnapshotAsync(Guid tenantId, Guid draftId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) lock (definitions.TenantGate) return ValueTask.FromResult(definitions.TenantSnapshot(tenantId, draftId)); }
    public ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> ListOwnedPublicationResourcesAsync(Guid tenantId, Guid definitionId, int version, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) lock (definitions.TenantGate) return ValueTask.FromResult(definitions.TenantPublicationResources(tenantId, definitionId, version)); }
    public ValueTask<AgentDefinitionDraftResourceBatchBound?> TryBindDefinitionResourcesAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, IReadOnlyList<AgentDefinitionDraftResource> resources, AuthorizationAudit audit, CancellationToken ct = default) =>
        ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () => definitions.BindTenantResources(next.Tenant.TenantId, draftId, expectedDraftRevision, resources, audit.At), ct));
    public ValueTask<AgentDefinitionDraftResourceBatchBound?> TryRemoveDefinitionResourceAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, Guid resourceId, AuthorizationAudit audit, CancellationToken ct = default) =>
        ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () =>
        {
            var draft = definitions.TenantDraft(next.Tenant.TenantId, draftId);
            if (draft is null || draft.Revision != expectedDraftRevision) return null;
            TenantDefinitionResourceScopes.RequireNoGrant(next, TenantDefinitionResourceScopes.Draft(next.Tenant.TenantId, Guid.Parse(draft.DefinitionId), draftId, resourceId));
            return definitions.RemoveTenantResource(next.Tenant.TenantId, draftId, expectedDraftRevision, resourceId, audit.At);
        }, ct));
    public ValueTask<TenantDefinitionScenarioSaved?> TrySaveDefinitionScenarioAsync(TenantSecurityState next, long expectedSecurityRevision,
        long expectedDraftRevision, DefinitionEvaluationScenarioUpsert scenario, AuthorizationAudit audit, CancellationToken ct = default) =>
        ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () => definitions.SaveTenantScenario(next.Tenant.TenantId, expectedDraftRevision, scenario, audit.At), ct));
    public ValueTask<TenantDefinitionDraftRevision?> TryRemoveDefinitionScenarioAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, string scenarioId, AuthorizationAudit audit, CancellationToken ct = default) =>
        ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () => definitions.RemoveTenantScenario(next.Tenant.TenantId, draftId, expectedDraftRevision, scenarioId, audit.At), ct));
    public ValueTask<DefinitionEvaluationResult?> TrySaveDefinitionEvaluationAsync(TenantSecurityState next, long expectedSecurityRevision,
        DefinitionEvaluationResult result, AuthorizationAudit audit, CancellationToken ct = default) =>
        ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () => definitions.SaveTenantEvaluation(next.Tenant.TenantId, result, next.Tenant.Revision), ct));
}
