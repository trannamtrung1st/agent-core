using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantDefinitionService
{
    public async ValueTask DeleteDraftAsync(Guid actor, Guid tenant, Guid draftId, long expectedRevision,
        long expectedDraftRevision, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var draft = await OwnedDraft(tenant, draftId, ct).ConfigureAwait(false);
        var id = Guid.Parse(draft.DefinitionId);
        var state = await ForChange(actor, tenant, "delete", Scope(tenant, id), expectedRevision, ct, initiator).ConfigureAwait(false);
        if (draft.Revision != expectedDraftRevision) throw new SecurityConflictException("stale_revision");
        var retire = (await definitions.ListDefinitionDraftsAsync(tenant, id, ct).ConfigureAwait(false)).Count == 1
            && (await definitions.ListDefinitionPublicationsAsync(tenant, id, ct).ConfigureAwait(false)).Count == 0;
        var next = TenantDefinitionResourceScopes.WithoutDraftGrants(Advance(state), draftId);
        if (retire) next = TenantDefinitionDeletionRules.WithoutResourceGrants(next, id);
        var now = time.GetUtcNow();
        if (await definitions.TryDeleteDefinitionDraftAsync(next, expectedRevision, draftId, expectedDraftRevision, retire, now,
            Audit(tenant, actor, "delete-draft", draftId, now), ct, initiator).ConfigureAwait(false) is null)
            throw new SecurityConflictException("stale_revision");
    }

    public async ValueTask DeleteAsync(Guid actor, Guid tenant, Guid id, AdminDefinitionDeleteWitness witness,
        long expectedRevision, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        TenantDefinitionDeletionRules.Validate(witness);
        var state = await ForChange(actor, tenant, "delete", Scope(tenant, id), expectedRevision, ct, initiator).ConfigureAwait(false);
        var now = time.GetUtcNow();
        if (await definitions.TryDeleteDefinitionAsync(TenantDefinitionDeletionRules.WithoutResourceGrants(Advance(state), id),
            expectedRevision, id, witness, now, Audit(tenant, actor, "delete", id, now), ct, initiator).ConfigureAwait(false) is null)
            throw new SecurityConflictException("stale_revision");
    }
}

public static class TenantDefinitionDeletionRules
{
    public static void Validate(AdminDefinitionDeleteWitness witness)
    {
        ArgumentNullException.ThrowIfNull(witness);
        if (witness.Drafts is null || witness.Publications is null || witness.Drafts.Count + witness.Publications.Count == 0
            || witness.Drafts.Any(d => d is null || d.DraftId == Guid.Empty || d.Revision < 1)
            || witness.Publications.Any(p => p is null || p.Version < 1 || p.MetadataRevision < 1)
            || witness.Drafts.Select(d => d.DraftId).Distinct().Count() != witness.Drafts.Count
            || witness.Publications.Select(p => p.Version).Distinct().Count() != witness.Publications.Count)
            throw new ArgumentException("Supply the current draft and publication revisions.");
    }
    public static bool Matches(AdminDefinitionDeleteWitness expected, AdminDefinitionDeleteWitness actual) =>
        expected.Drafts.OrderBy(d => d.DraftId).SequenceEqual(actual.Drafts.OrderBy(d => d.DraftId))
        && expected.Publications.OrderBy(p => p.Version).SequenceEqual(actual.Publications.OrderBy(p => p.Version));
    public static TenantSecurityState WithoutResourceGrants(TenantSecurityState state, Guid id) => state with
    {
        Assignments = state.Assignments.Where(a => !Targets(a, id)).ToArray()
    };
    public static void RequireNoResourceGrants(TenantSecurityState state, Guid id)
    {
        if (state.Assignments.Any(a => Targets(a, id))) throw new ArgumentException("Retired definitions cannot retain resource grants.");
    }
    private static bool Targets(RoleAssignment assignment, Guid id) => assignment.Scope.Kind == AccessScopeKind.Resource
        && (assignment.Scope.ResourceType == "definition" && assignment.Scope.ResourceId == id.ToString("D")
            || TenantDefinitionResourceScopes.BelongsTo(assignment.Scope, id));
}
