using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantDefinitionService(IIamStore security, ITenantDefinitionStore definitions, IamAuthorizer authorizer,
    ProviderAliasSet aliases, ITenantModelCatalogReader modelCatalogs, IToolConfigurationGate configuration, IIdGenerator ids, TimeProvider time, IDefinitionResourceContentStore content,
    IBuiltInDefinitionTemplateSource templates, IdentityService identity)
{
    public ValueTask<TenantDefinitionPage> ListAsync(Guid actor, Guid tenant, int page = 0, int pageSize = 30, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        WithHumanRead<TenantDefinitionPage>(actor, tenant, initiator, async () =>
        {
            var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
            var permission = PermissionCatalog.Get("agent-definitions.read");
            var all = await authorizer.CheckTenantAsync(state, actor, permission, AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
            List<Guid>? allowed = null;
            if (!all.Allowed)
            {
                if (all.ReasonCode != "permission_denied") throw new AccessDeniedException(all.ReasonCode);
                allowed = [];
                var candidates = state.Assignments.Where(a => a.PrincipalId == actor && a.Scope.Kind == AccessScopeKind.Resource
                    && a.Scope.ResourceType == "definition").Select(a => Guid.Parse(a.Scope.ResourceId!)).Distinct();
                foreach (var id in candidates)
                    if ((await authorizer.CheckTenantAsync(state, actor, permission, Scope(tenant, id), ct).ConfigureAwait(false)).Allowed) allowed.Add(id);
            }
            return await definitions.ListDefinitionsAsync(tenant, allowed, Math.Clamp(page, 0, 100_000), Math.Clamp(pageSize, 1, 100), ct).ConfigureAwait(false);
        }, ct);

    public async ValueTask<AgentDefinitionDraft> CreateAsync(Guid actor, Guid tenant, string name, long expectedRevision, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var state = await ForChange(actor, tenant, "create", AccessScope.ForTenant(tenant), expectedRevision, ct, initiator).ConfigureAwait(false);
        var id = ids.NewId(); var now = time.GetUtcNow();
        var candidate = AgentDefinitionStarter.Create(id.ToString("D"), aliases);
        candidate = candidate with { Identity = candidate.Identity with { Name = name } };
        candidate = Validate(candidate, false);
        var draft = new AgentDefinitionDraft(ids.NewId(), id.ToString("D"), 1, candidate, DefinitionDraftSourceKind.New, null, now, now);
        if (!await definitions.TryCreateDefinitionAsync(Advance(state), expectedRevision, new(tenant, id, now), draft, [],
            Audit(tenant, actor, "create", id, now), ct, initiator).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        return draft;
    }

    public ValueTask<IReadOnlyList<AgentDefinitionDraft>> DraftsAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        WithHumanRead<IReadOnlyList<AgentDefinitionDraft>>(actor, tenant, initiator, async () =>
        {
            await authorizer.RequireAsync(actor, "agent-definitions.read", Scope(tenant, id), ct).ConfigureAwait(false);
            return await definitions.ListDefinitionDraftsAsync(tenant, id, ct).ConfigureAwait(false);
        }, ct);
    public ValueTask<AgentDefinitionDraft> ReadDraftAsync(Guid actor, Guid tenant, Guid draftId, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        WithHumanRead<AgentDefinitionDraft>(actor, tenant, initiator, async () =>
        {
            var draft = await OwnedDraft(tenant, draftId, ct).ConfigureAwait(false);
            await authorizer.RequireAsync(actor, "agent-definitions.read", Scope(tenant, Guid.Parse(draft.DefinitionId)), ct).ConfigureAwait(false);
            return draft;
        }, ct);
    public async ValueTask<AgentDefinitionDraft> UpdateAsync(Guid actor, Guid tenant, Guid draftId, AgentDefinitionCandidate candidate,
        long expectedRevision, long expectedDraftRevision, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var draft = await OwnedDraft(tenant, draftId, ct).ConfigureAwait(false); var id = Guid.Parse(draft.DefinitionId);
        var state = await ForChange(actor, tenant, "update", Scope(tenant, id), expectedRevision, ct, initiator).ConfigureAwait(false);
        if (candidate.DefinitionId != draft.DefinitionId) throw new ArgumentException("Definition identity cannot change.");
        if (draft.Revision != expectedDraftRevision) throw new SecurityConflictException("stale_revision");
        candidate = Validate(candidate, false); var now = time.GetUtcNow();
        var updated = draft with { Candidate = candidate, Revision = checked(draft.Revision + 1), UpdatedAt = now };
        if (!await definitions.TrySaveDefinitionDraftAsync(Advance(state), expectedRevision, updated, expectedDraftRevision,
            Audit(tenant, actor, "update", id, now), ct, initiator).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        return updated;
    }
    public async ValueTask<AgentDefinitionDraft> ForkAsync(Guid actor, Guid tenant, Guid id, int version, long expectedRevision, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var state = await ForChange(actor, tenant, "update", Scope(tenant, id), expectedRevision, ct, initiator).ConfigureAwait(false);
        // Reading a publication is a separate privilege from writing this definition.
        await authorizer.RequireAsync(actor, "agent-definitions.read", Scope(tenant, id), ct).ConfigureAwait(false);
        var source = await definitions.FindDefinitionPublicationAsync(tenant, id, version, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await RequireDestinationResources(state, actor, (await definitions.ListOwnedPublicationResourcesAsync(tenant, id, version, ct).ConfigureAwait(false)).Count, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        var draft = new AgentDefinitionDraft(ids.NewId(), id.ToString("D"), 1, Validate(AgentDefinitionCandidate.FromDefinition(source.Payload), false),
            DefinitionDraftSourceKind.ForkDurable, version, now, now);
        if (!await definitions.TrySaveDefinitionDraftAsync(Advance(state), expectedRevision, draft, null,
            Audit(tenant, actor, "fork", id, now), ct, initiator).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        _ = await HumanState(actor, tenant, initiator, ct).ConfigureAwait(false);
        return (await definitions.FindDefinitionDraftAsync(tenant, draft.DraftId, ct).ConfigureAwait(false))!;
    }
    public async ValueTask<AgentDefinitionPublication> PublishAsync(Guid actor, Guid tenant, Guid draftId,
        long expectedRevision, long expectedDraftRevision, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var draft = await OwnedDraft(tenant, draftId, ct).ConfigureAwait(false); var id = Guid.Parse(draft.DefinitionId);
        var state = await ForChange(actor, tenant, "publish", Scope(tenant, id), expectedRevision, ct, initiator).ConfigureAwait(false);
        if (draft.Revision != expectedDraftRevision) throw new SecurityConflictException("stale_revision");
        _ = Validate(draft.Candidate, true, await modelCatalogs.ReadAsync(state, ct).ConfigureAwait(false));
        var snapshot = await definitions.ReadDefinitionDraftSnapshotAsync(tenant, draftId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await RequireDestinationResources(state, actor, snapshot.Resources.Count, ct).ConfigureAwait(false);
        var now = time.GetUtcNow();
        return await definitions.TryPublishDefinitionDraftAsync(TenantDefinitionResourceScopes.WithoutDraftGrants(Advance(state), draftId), expectedRevision, draftId, expectedDraftRevision, now,
            Audit(tenant, actor, "publish", id, now), ct, initiator).ConfigureAwait(false) ?? throw new SecurityConflictException("stale_revision");
    }
    public ValueTask<IReadOnlyList<AgentDefinitionPublicationSummary>> PublicationsAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        WithHumanRead<IReadOnlyList<AgentDefinitionPublicationSummary>>(actor, tenant, initiator, async () =>
        {
            await authorizer.RequireAsync(actor, "agent-definitions.read", Scope(tenant, id), ct).ConfigureAwait(false);
            return await definitions.ListDefinitionPublicationsAsync(tenant, id, ct).ConfigureAwait(false);
        }, ct);
    public ValueTask<AgentDefinitionPublication> ReadPublicationAsync(Guid actor, Guid tenant, Guid id, int version, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        WithHumanRead<AgentDefinitionPublication>(actor, tenant, initiator, async () =>
        {
            await authorizer.RequireAsync(actor, "agent-definitions.read", Scope(tenant, id), ct).ConfigureAwait(false);
            return await definitions.FindDefinitionPublicationAsync(tenant, id, version, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        }, ct);
    public async ValueTask<AgentDefinitionPublication> DeprecateAsync(Guid actor, Guid tenant, Guid id, int version,
        long expectedRevision, long expectedMetadataRevision, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var state = await ForChange(actor, tenant, "publish", Scope(tenant, id), expectedRevision, ct, initiator).ConfigureAwait(false);
        return await definitions.TryDeprecateDefinitionPublicationAsync(Advance(state), expectedRevision, id, version, expectedMetadataRevision,
            Audit(tenant, actor, "deprecate", id, time.GetUtcNow()), ct, initiator).ConfigureAwait(false) ?? throw new SecurityConflictException("stale_revision");
    }
    private async ValueTask<TenantSecurityState> HumanState(Guid actor, Guid tenant, HumanSessionReference? initiator, CancellationToken ct)
    {
        if (initiator is null) return await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        if (initiator.PrincipalId != actor) throw new AccessDeniedException("initiator_unavailable");
        return await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, initiator, ct).ConfigureAwait(false);
    }
    private async ValueTask RequireReadWitness(Guid actor, Guid tenant, long expectedRevision, HumanSessionReference? initiator, CancellationToken ct)
    {
        if (initiator is null) return;
        var current = await HumanState(actor, tenant, initiator, ct).ConfigureAwait(false);
        if (current.Tenant.Revision != expectedRevision) throw new SecurityConflictException("stale_revision");
    }
    private async ValueTask<T> WithHumanRead<T>(Guid actor, Guid tenant, HumanSessionReference? initiator, Func<ValueTask<T>> read, CancellationToken ct)
    {
        if (initiator is null) return await read().ConfigureAwait(false);
        var before = await HumanState(actor, tenant, initiator, ct).ConfigureAwait(false);
        var result = await read().ConfigureAwait(false);
        await RequireReadWitness(actor, tenant, before.Tenant.Revision, initiator, ct).ConfigureAwait(false);
        return result;
    }
    public async ValueTask<AdminAuthoringOptions> AuthoringOptionsAsync(Guid actor, Guid tenant, Guid? definitionId, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        var before = await HumanState(actor, tenant, initiator, ct).ConfigureAwait(false);
        if (definitionId is { } id) _ = await AccessAsync(actor, tenant, id, ct).ConfigureAwait(false);
        else await RequireResourcePermission(before, actor, "agent-definitions.create", AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
        var catalog = await modelCatalogs.ReadAsync(before, ct).ConfigureAwait(false);
        var result = new AdminAuthoringOptionsService(aliases).Get(catalog);
        await RequireReadWitness(actor, tenant, before.Tenant.Revision, initiator, ct).ConfigureAwait(false);
        return result;
    }
    private AgentDefinitionCandidate Validate(AgentDefinitionCandidate candidate, bool publication, IModelCatalog? models = null)
    {
        candidate = CapabilityAuthorizationResolver.ResolveCandidate(candidate);
        if (publication) AgentDefinitionCandidateValidator.ValidateForPublication(candidate, aliases, models ?? throw new InvalidOperationException("An authorized tenant catalog is required."), configuration);
        else AgentDefinitionCandidateValidator.ValidateForPersistence(candidate, aliases);
        return candidate;
    }
    private async ValueTask<TenantSecurityState> ForChange(Guid actor, Guid tenant, string action, AccessScope scope, long revision, CancellationToken ct, HumanSessionReference? initiator = null)
    {
        var state = await HumanState(actor, tenant, initiator, ct).ConfigureAwait(false);
        var check = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("agent-definitions." + action), scope, ct).ConfigureAwait(false);
        if (!check.Allowed) throw new AccessDeniedException(check.ReasonCode);
        if (scope.Kind == AccessScopeKind.Resource)
        {
            var read = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get("agent-definitions.read"), scope, ct).ConfigureAwait(false);
            if (!read.Allowed) throw new AccessDeniedException(read.ReasonCode);
        }
        if (state.Tenant.Revision != revision) throw new SecurityConflictException("stale_revision");
        return state;
    }
    private async ValueTask<AgentDefinitionDraft> OwnedDraft(Guid tenant, Guid id, CancellationToken ct) =>
        await definitions.FindDefinitionDraftAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
    private static TenantSecurityState Advance(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
    private static AccessScope Scope(Guid tenant, Guid id) => AccessScope.ForResource(tenant, "definition", id.ToString("D"));
    private AuthorizationAudit Audit(Guid tenant, Guid actor, string action, Guid id, DateTimeOffset at) => new(ids.NewId(), tenant, actor, "agent-definitions." + action, id.ToString("D"), "allowed", "authorized", at);
}
