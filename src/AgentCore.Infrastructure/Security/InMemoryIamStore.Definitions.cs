using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<bool> OwnsResourceAsync(AccessScope scope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (instances.CredentialGate) lock (definitions.TenantGate)
            return ValueTask.FromResult(TenantApprovalRules.TryApprovalId(scope, out var approval) ? _approvals.GetValueOrDefault(approval)?.TenantId == scope.TenantId
                : TenantRunRules.TryId(scope, out var run) ? sessionBacking.ReadTenantRun(scope.TenantId!.Value, run) is not null
                : TenantSessionRules.TryId(scope, out var session) ? sessionBacking.OwnsTenantSession(scope.TenantId!.Value, session)
                : TenantCredentialRules.TryId(scope, out var credential) ? credentials?.OwnsTenantCredential(scope.TenantId!.Value, credential) == true
                : TenantAgentChildScopes.TryParse(scope, out var instance, out _)
                ? ChildCatalog(scope.TenantId!.Value, instance, null, ct).Contains(scope)
                : TenantDefinitionResourceScopes.TryParse(scope, out var resource) ? OwnsDefinitionResource(scope.TenantId!.Value, resource)
                : scope.Kind == AccessScopeKind.Resource && scope.ResourceType == "definition"
                && scope.TenantId is { } tenant && tenant != Guid.Empty && Guid.TryParseExact(scope.ResourceId, "D", out var id) && scope.ResourceId == id.ToString("D")
                && definitions.OwnsActiveDefinition(tenant, id));
    }
    public ValueTask<TenantDefinition?> FindDefinitionAsync(Guid tenantId, Guid definitionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (definitions.TenantGate)
        { var root = definitions.TenantDefinitions.GetValueOrDefault(definitionId); return ValueTask.FromResult(root is { DeletedAt: null } && root.TenantId == tenantId ? root : null); }
    }
    public ValueTask<TenantDefinitionPage> ListDefinitionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); if (tenantId == Guid.Empty || page < 0 || page > 100_000 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        lock (_gate) lock (definitions.TenantGate)
        {
            var allowed = allowedIds?.ToHashSet(); var all = definitions.TenantDefinitions.Values.Where(d => d.TenantId == tenantId && d.DeletedAt == null && (allowed is null || allowed.Contains(d.DefinitionId))).OrderBy(d => d.DefinitionId.ToString("D"), StringComparer.Ordinal).ToArray();
            var items = all.Skip(page * pageSize).Take(pageSize).Select(root =>
            {
                var drafts = definitions.TenantDrafts(tenantId, root.DefinitionId); var latest = definitions.TenantPublications(tenantId, root.DefinitionId).LastOrDefault();
                return new TenantDefinitionSummary(root, drafts.Count, latest?.Version, latest?.Payload.Identity.Name ?? drafts.OrderByDescending(d => d.UpdatedAt).FirstOrDefault()?.Candidate.Identity.Name ?? root.DefinitionId.ToString("D"));
            }).ToArray();
            return ValueTask.FromResult(new TenantDefinitionPage(items, all.Length, page, pageSize));
        }
    }
    public ValueTask<IReadOnlyList<AgentDefinitionDraft>> ListDefinitionDraftsAsync(Guid tenantId, Guid definitionId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) lock (definitions.TenantGate) return ValueTask.FromResult(definitions.TenantDrafts(tenantId, definitionId)); }
    public ValueTask<AgentDefinitionDraft?> FindDefinitionDraftAsync(Guid tenantId, Guid draftId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) lock (definitions.TenantGate) return ValueTask.FromResult(definitions.TenantDraft(tenantId, draftId)); }
    public ValueTask<AgentDefinitionPublication?> FindDefinitionPublicationAsync(Guid tenantId, Guid definitionId, int version, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) lock (definitions.TenantGate) return ValueTask.FromResult(definitions.TenantPublication(tenantId, definitionId, version)); }
    public ValueTask<IReadOnlyList<AgentDefinitionPublicationSummary>> ListDefinitionPublicationsAsync(Guid tenantId, Guid definitionId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) lock (definitions.TenantGate) return ValueTask.FromResult<IReadOnlyList<AgentDefinitionPublicationSummary>>(definitions.TenantPublications(tenantId, definitionId).Select(p => new AgentDefinitionPublicationSummary(p.DefinitionId, p.Version, p.Status, p.MetadataRevision, p.PublishedAt, p.Payload.SkillList.Select(s => new PublishedSkillReference(s.Id, s.Name, s.RequiredCapabilities)).ToArray())).ToArray()); }
    public ValueTask<bool> TryCreateDefinitionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantDefinition definition, AgentDefinitionDraft draft,
        IReadOnlyList<AgentDefinitionDraftResource> initialResources, AuthorizationAudit audit, CancellationToken ct = default) =>
        ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () => definition.TenantId == next.Tenant.TenantId && definitions.CreateTenantDefinition(definition, draft, initialResources, ct) ? definition : null, ct) is not null);
    public ValueTask<bool> TrySaveDefinitionDraftAsync(TenantSecurityState next, long expectedSecurityRevision, AgentDefinitionDraft draft, long? expectedDraftRevision, AuthorizationAudit audit, CancellationToken ct = default) =>
        ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () => definitions.SaveTenantDraft(next.Tenant.TenantId, draft, expectedDraftRevision) ? draft : null, ct) is not null);
    public ValueTask<AgentDefinitionPublication?> TryPublishDefinitionDraftAsync(TenantSecurityState next, long expectedSecurityRevision, Guid draftId, long expectedDraftRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default) =>
        ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () =>
        {
            TenantDefinitionResourceScopes.RequireNoDraftGrants(next, draftId);
            return definitions.PublishTenantDraft(next.Tenant.TenantId, draftId, expectedDraftRevision, at);
        }, ct));
    public ValueTask<AgentDefinitionPublication?> TryDeprecateDefinitionPublicationAsync(TenantSecurityState next, long expectedSecurityRevision, Guid definitionId, int version, long expectedMetadataRevision, AuthorizationAudit audit, CancellationToken ct = default) =>
        ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () => definitions.DeprecateTenantPublication(next.Tenant.TenantId, definitionId, version, expectedMetadataRevision), ct));
    private T? WriteDefinition<T>(TenantSecurityState next, long expected, AuthorizationAudit audit, Func<T?> change, CancellationToken ct) where T : class
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        if (audit.TenantId != next.Tenant.TenantId || next.Tenant.Revision != checked(expected + 1)) throw new ArgumentException("Invalid definition transaction.");
        lock (_gate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expected) return null;
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, audit.ActorPrincipalId); EnsureReferences(next, audit, ct: ct);
            var result = change(); if (result is null) return null;
            _tenants[next.Tenant.TenantId] = next; _audit.Add(audit); return result;
        }
    }
}
