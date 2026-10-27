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
            return ValueTask.FromResult(AgentCore.Application.Tasks.TaskAuthority.TryScope(scope, out var taskScope)
                ? scope.ResourceType == "task" ? delegatedTasks.GetValueOrDefault((scope.TenantId!.Value, taskScope)) is { } ownedTask
                    && sessionBacking.ReadTenantSession(scope.TenantId.Value, ownedTask.Source.SessionId) is not null
                    : agentThreads.ContainsKey((scope.TenantId!.Value, taskScope))
                : AgentCore.Application.Evaluations.EvaluationStoreRules.TryScope(scope, out _) ? OwnsEvaluation(scope)
                : AgentCore.Application.Models.TenantModelRules.TryId(scope, out _) ? OwnsModelResource(scope)
                : TenantSecretService.TryId(scope, out var secret) ? _secrets.GetValueOrDefault((scope.TenantId!.Value, secret)) is { Status: not AgentCore.Domain.Spaces.SecretStatus.Deleted }
                : TenantSpaceService.TryId(scope, out var space) ? _spaces.GetValueOrDefault((scope.TenantId!.Value, space)) is { DeletedAt: null }
                : TenantEventAdmissionRules.TryId(scope, out var coreEvent) ? sessionBacking.CoreEvents.OwnsTenantEvent(scope.TenantId!.Value, coreEvent)
                : TenantWebhookRules.TryId(scope, out var webhook) ? webhookBacking.OwnsTenantSource(scope.TenantId!.Value, webhook)
                : TenantAutomationRules.TryId(scope, out var automation) ? automationBacking.OwnsTenantAutomation(scope.TenantId!.Value, automation)
                : TenantWorkspaceRules.TryParse(scope, out _, out _) ? OwnsWorkspace(scope, ct)
                : TenantSandboxProfileRules.TryId(scope, out var profile) ? FindTenantSandboxProfileAsync(scope.TenantId!.Value, profile, ct: ct).Result is not null
                : TenantApprovalRules.TryApprovalId(scope, out var approval) ? _approvals.GetValueOrDefault(approval)?.TenantId == scope.TenantId
                : TenantAttachmentRules.TryId(scope, out var attachment) ? attachmentBacking?.OwnsTenantAttachment(scope.TenantId!.Value, attachment) == true
                : TenantArtifactRules.TryId(scope, out var artifact) ? artifactBacking?.OwnsTenantArtifact(scope.TenantId!.Value, artifact) == true
                : TenantRunRules.TryId(scope, out var run) ? sessionBacking.ReadTenantRun(scope.TenantId!.Value, run) is not null
                : TenantSessionRules.TryId(scope, out var session) ? sessionBacking.OwnsTenantSession(scope.TenantId!.Value, session)
                : TenantCredentialRules.TryId(scope, out var credential) ? credentials?.OwnsTenantCredential(scope.TenantId!.Value, credential) == true
                : TenantAgentChildScopes.TryParse(scope, out var instance, out _)
                ? ChildCatalog(scope.TenantId!.Value, instance, null, ct).Contains(scope)
                : TenantResourceScopes.TrySpace(scope, out var sourceSpace, out var sourceResource) ? FindSpaceResourceAsync(scope.TenantId!.Value, sourceSpace, sourceResource, ct).Result is not null
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
        IReadOnlyList<AgentDefinitionDraftResource> initialResources, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () => definition.TenantId == next.Tenant.TenantId && definitions.CreateTenantDefinition(definition, draft, initialResources, ct) ? definition : null, ct, initiator) is not null);
    public ValueTask<bool> TrySaveDefinitionDraftAsync(TenantSecurityState next, long expectedSecurityRevision, AgentDefinitionDraft draft, long? expectedDraftRevision, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () => {
            AgentCore.Domain.Evaluations.EvaluationSuiteVersion? clone = null;
            if (expectedDraftRevision is null)
            {
                foreach (var binding in draft.Candidate.EvaluationBindings ?? [])
                    if (binding.Hook == AgentCore.Domain.Evaluations.EvaluationHook.Publication && Suite(next.Tenant.TenantId, binding.SuiteId, binding.Version) is { DraftId: not null } source)
                    {
                        clone = AgentCore.Domain.Evaluations.EvaluationRules.Freeze(source with { SuiteId = draft.DraftId, DraftId = draft.DraftId, Version = 1, CreatedBy = audit.ActorPrincipalId, CreatedAt = audit.At, Archived = false });
                        draft = draft with { Candidate = draft.Candidate with { EvaluationBindings = (draft.Candidate.EvaluationBindings ?? []).Where(b => b != binding).ToArray() } };
                    }
            }
            foreach (var binding in draft.Candidate.EvaluationBindings ?? [])
            {
                var target = Suite(next.Tenant.TenantId, binding.SuiteId, binding.Version);
                if (target is null || target.Archived || Suite(next.Tenant.TenantId, binding.SuiteId)?.Archived != false
                    || target.DraftId is not null && (binding.Hook != AgentCore.Domain.Evaluations.EvaluationHook.Publication || target.DefinitionId != Guid.Parse(draft.DefinitionId)))
                    throw new AccessDeniedException("evaluation_suite_unavailable");
                AgentCore.Application.Evaluations.EvaluationBindingValidation.Require(binding, target, reference => Script(next.Tenant.TenantId, reference.ScriptId, reference.Version));
            }
            if (!definitions.SaveTenantDraft(next.Tenant.TenantId, draft, expectedDraftRevision)) return null;
            if (clone is not null) evaluationSuites.Add((clone.TenantId, clone.SuiteId, 1), clone);
            return draft;
        }, ct, initiator) is not null);
    public ValueTask<AgentDefinitionPublication?> TryPublishDefinitionDraftAsync(TenantSecurityState next, long expectedSecurityRevision, Guid draftId, long expectedDraftRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () =>
        {
            TenantDefinitionResourceScopes.RequireNoDraftGrants(next, draftId);
            var snapshot = EvaluationSnapshot(next.Tenant.TenantId, draftId);
            if (snapshot is null) return null;
            return definitions.PublishTenantDraft(next.Tenant.TenantId, draftId, expectedDraftRevision, at, new(snapshot.Resources, snapshot.Suites, snapshot.Attempts));
        }, ct, initiator));
    public ValueTask<AgentDefinitionPublication?> TryDeprecateDefinitionPublicationAsync(TenantSecurityState next, long expectedSecurityRevision, Guid definitionId, int version, long expectedMetadataRevision, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null) =>
        ValueTask.FromResult(WriteDefinition(next, expectedSecurityRevision, audit, () => definitions.DeprecateTenantPublication(next.Tenant.TenantId, definitionId, version, expectedMetadataRevision), ct, initiator));
    private T? WriteDefinition<T>(TenantSecurityState next, long expected, AuthorizationAudit audit, Func<T?> change, CancellationToken ct, HumanSessionReference? initiator) where T : class
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        if (audit.TenantId != next.Tenant.TenantId || next.Tenant.Revision != checked(expected + 1)) throw new ArgumentException("Invalid definition transaction.");
        lock (_gate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expected) return null;
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); ValidateAgentActor(before, audit.ActorPrincipalId); EnsureReferences(next, audit, ct: ct);
            RequireAgentHuman(next.Tenant.TenantId, audit.ActorPrincipalId, initiator);
            var result = change(); if (result is null) return null;
            _tenants[next.Tenant.TenantId] = next; _audit.Add(audit); return result;
        }
    }
}
