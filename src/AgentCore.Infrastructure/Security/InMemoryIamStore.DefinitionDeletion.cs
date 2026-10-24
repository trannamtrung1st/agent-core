using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TenantDefinitionDeleted?> TryDeleteDefinitionDraftAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid draftId, long expectedDraftRevision, bool retireDefinition, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () =>
        {
            var draft = definitions.TenantDraft(next.Tenant.TenantId, draftId);
            if (draft is null || draft.Revision != expectedDraftRevision) return null;
            var id = Guid.Parse(draft.DefinitionId);
            var actualRetire = definitions.TenantDrafts(next.Tenant.TenantId, id).Count == 1
                && definitions.TenantPublications(next.Tenant.TenantId, id).Count == 0;
            if (retireDefinition != actualRetire) return null;
            TenantDefinitionResourceScopes.RequireNoDraftGrants(next, draftId);
            if (retireDefinition) TenantDefinitionDeletionRules.RequireNoResourceGrants(next, id);
            return WithDefinitionReferences(id, retireDefinition,
                () => definitions.DeleteTenantDraft(next.Tenant.TenantId, draftId, expectedDraftRevision, retireDefinition, at));
        }, ct, initiator));

    public ValueTask<TenantDefinitionDeleted?> TryDeleteDefinitionAsync(TenantSecurityState next, long expectedSecurityRevision,
        Guid definitionId, AdminDefinitionDeleteWitness witness, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        TenantDefinitionDeletionRules.Validate(witness);
        return ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () =>
        {
            var current = definitions.TenantDeletionWitness(next.Tenant.TenantId, definitionId);
            if (current is null || !TenantDefinitionDeletionRules.Matches(witness, current)) return null;
            TenantDefinitionDeletionRules.RequireNoResourceGrants(next, definitionId);
            return WithDefinitionReferences(definitionId, true,
                () => definitions.DeleteTenantDefinition(next.Tenant.TenantId, definitionId, at));
        }, ct, initiator));
    }
    private TenantDefinitionDeleted? WithDefinitionReferences(Guid id, bool retiring, Func<TenantDefinitionDeleted?> change)
    {
        if (!retiring) return change();
        lock (definitionReferences.AdmissionGate)
        {
            var key = id.ToString("D");
            var retired = _tenants.Values.SelectMany(t => t.SystemIdentities).Where(i => i.Status == IdentityStatus.Removed).Select(i => i.AgentInstanceId!.Value).ToHashSet();
            if (instances.CountByDefinition(key, retired) > 0 || definitionReferences.HasDefinitionHistory(key))
                throw new SecurityConflictException("definition_in_use");
            return change();
        }
    }
}
