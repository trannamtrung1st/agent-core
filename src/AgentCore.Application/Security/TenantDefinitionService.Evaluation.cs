using AgentCore.Application.Admin;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Security;

public sealed partial class TenantDefinitionService
{
    public async ValueTask<TenantDefinitionScenarioSaved> SaveScenarioAsync(Guid actor, Guid tenant, Guid draftId,
        long expectedRevision, long expectedDraftRevision, DefinitionEvaluationScenarioUpsert upsert, CancellationToken ct = default)
    {
        if (upsert.DraftId != draftId) throw new ArgumentException("Scenario draft does not match.");
        TenantDefinitionChildRules.ValidateScenario(upsert);
        var draft = await OwnedDraft(tenant, draftId, ct).ConfigureAwait(false); var id = Guid.Parse(draft.DefinitionId);
        var state = await ForChange(actor, tenant, "update", Scope(tenant, id), expectedRevision, ct).ConfigureAwait(false);
        upsert = upsert with { UpdatedAt = time.GetUtcNow() };
        return await definitions.TrySaveDefinitionScenarioAsync(Advance(state), expectedRevision, expectedDraftRevision, upsert,
            Audit(tenant, actor, "evaluations.scenario.save", id, upsert.UpdatedAt), ct).ConfigureAwait(false) ?? throw new SecurityConflictException("stale_revision");
    }
    public async ValueTask<TenantDefinitionDraftRevision> RemoveScenarioAsync(Guid actor, Guid tenant, Guid draftId, string scenarioId,
        long expectedRevision, long expectedDraftRevision, CancellationToken ct = default)
    {
        var draft = await OwnedDraft(tenant, draftId, ct).ConfigureAwait(false); var id = Guid.Parse(draft.DefinitionId);
        var state = await ForChange(actor, tenant, "update", Scope(tenant, id), expectedRevision, ct).ConfigureAwait(false);
        return await definitions.TryRemoveDefinitionScenarioAsync(Advance(state), expectedRevision, draftId, expectedDraftRevision, scenarioId,
            Audit(tenant, actor, "evaluations.scenario.remove", id, time.GetUtcNow()), ct).ConfigureAwait(false) ?? throw new SecurityConflictException("stale_revision");
    }
    public async ValueTask<DefinitionEvaluationResult> RunScenarioAsync(Guid actor, Guid tenant, Guid draftId, string scenarioId,
        long expectedRevision, long expectedDraftRevision, CancellationToken ct = default)
    {
        var snapshot = await AuthorizedSnapshot(actor, tenant, draftId, "update", ct).ConfigureAwait(false); var id = Guid.Parse(snapshot.Draft.DefinitionId);
        var state = await ForChange(actor, tenant, "update", Scope(tenant, id), expectedRevision, ct).ConfigureAwait(false);
        if (snapshot.Draft.Revision != expectedDraftRevision) throw new SecurityConflictException("stale_revision");
        var scenario = snapshot.Scenarios.SingleOrDefault(s => s.ScenarioId == scenarioId) ?? throw new AccessDeniedException("resource_unavailable");
        var resourceSnapshots = new List<DefinitionDraftSyntheticResourceSnapshot>();
        foreach (var r in snapshot.Resources)
        {
            var bytes = await DefinitionResourceStoredContent.RequireForBindingAsync(content, r.ContentSha256, r.ByteLength, r.MediaType, ct).ConfigureAwait(false);
            resourceSnapshots.Add(new(r.LogicalPath, r.Kind, r.ContentSha256, bytes));
        }
        // The existing offline evaluator executes outside all persistence locks. Only the exact
        // still-current draft/scenario/authority witness may accept its completion.
        var (passed, findings) = await evaluation.RunAsync(snapshot.Draft.Candidate, resourceSnapshots, scenario, ct).ConfigureAwait(false);
        var result = new DefinitionEvaluationResult(draftId, expectedDraftRevision, TenantDefinitionChildRules.Fingerprint(snapshot),
            scenario.ScenarioId, scenario.ScenarioVersion, "synthetic-offline", passed, findings, time.GetUtcNow());
        return await definitions.TrySaveDefinitionEvaluationAsync(Advance(state), expectedRevision, result,
            Audit(tenant, actor, "evaluations.run", id, result.RecordedAt), ct).ConfigureAwait(false) ?? throw new SecurityConflictException("stale_revision");
    }
}
