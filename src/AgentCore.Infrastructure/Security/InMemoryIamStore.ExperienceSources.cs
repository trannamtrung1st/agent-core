using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    public ValueTask RequireTenantExperienceSourcesAsync(TenantRunReference reference, IReadOnlyList<TenantExperienceSourceReference> sources,
        IReadOnlyList<TenantExperienceFactSource> facts, Guid? generation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (isolationBacking.IsolationGate)
        {
            var run = sessionBacking.ReadRetainedTenantOperationRun(reference.TenantId, reference.RunId) ?? throw new AccessDeniedException("experience_unavailable");
            if (generation is { } claim) _ = RequireMemoryFrame(new(run.Owner, run.SessionId, run.AgentRunId, claim));
            RequireExperienceSources(run, sources, facts, ct); return ValueTask.CompletedTask;
        }
    }
    private void RequireExperienceSources(AgentRun run, IReadOnlyList<TenantExperienceSourceReference> sources, IReadOnlyList<TenantExperienceFactSource> facts, CancellationToken ct)
    {
        TenantExperienceCommitRules.SourcesShape(run, sources, facts); if (sources.Count == 0 && facts.Count == 0) return;
        var reader = MemoryReader(run, ct); var settings = Isolation(run.AgentInstanceId); var iam = new IamAuthorizer(this);
        TenantExperienceRules.Eligible(reader, settings, TenantExperienceToolContract.Source);
        foreach (var source in sources)
        {
            var observed = ObserveExperienceSource(run, source.Witness.Kind, source.Witness.SourceId, source.Witness, null, ct);
            if (!TenantExperienceRules.OrdinaryAsync(reader, observed.Witness, false, false, iam, ct).AsTask().GetAwaiter().GetResult()
                && !ExperienceSourceConsented(observed, ct)) throw new AccessDeniedException("experience_read_review_required");
        }
        if (facts.Count == 0) return;
        TenantExperienceRules.RawAsync(reader, null, true, iam, ct).AsTask().GetAwaiter().GetResult();
        var ordinary = TenantExperienceRules.OrdinaryAsync(reader, null, true, false, iam, ct).AsTask().GetAwaiter().GetResult();
        foreach (var source in facts)
        {
            var item = tenantExperiences.GetValueOrDefault((source.TenantId, source.ExperienceId));
            if (item is null || !TenantExperienceRules.Owns(reader, item.Reference())) throw new AccessDeniedException("experience_source_changed");
            var ownConsolidation = !TenantExperienceCommitRules.FactMatches(source, item.Reference()) && OwnExperienceConsolidation(reader, settings, source, item.Reference(), [], ct);
            if (!TenantExperienceCommitRules.FactMatches(source, item.Reference()) && !ownConsolidation
                || !ordinary && !ownConsolidation && !ExperienceFactConsented(reader, settings, source, ct)) throw new AccessDeniedException("experience_source_changed");
        }
    }
    private bool ExperienceSourceConsented(TenantExperienceSourceObservation observed, CancellationToken ct)
    {
        var reader = observed.Reader;
        foreach (var receipt in ExperienceReadReceipts(reader, TenantExperienceRules.ReadCompleted))
        {
            var request = _approvals.GetValueOrDefault(receipt.RequestId!.Value);
            if (request?.Operation.ExperienceOperation?.Source != observed.Witness || _approvalDecisions.GetValueOrDefault(request.RequestId) is not { Status: TenantApprovalStatus.Approved } decision) continue;
            try { ValidateAgentActor(reader.Security, decision.HumanPrincipalId); TenantExperienceConsentRules.SourceAsync(request, observed, decision.HumanPrincipalId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult(); return true; }
            catch (AccessDeniedException) { }
        }
        return false;
    }
    private IEnumerable<TenantOperationReceipt> ExperienceReadReceipts(TenantMemoryReader reader, string reason) => _operationReceipts.Values.Where(r => r.TenantId == reader.Security.Tenant.TenantId
        && r.SessionId == reader.Run.SessionId && r.RootActivationId == reader.Run.Admission.Activation.RootActivationId && r.Outcome == TenantOperationOutcome.Completed && r.RequestId is not null && r.ReasonCode == reason);
    private bool ExperienceFactConsented(TenantMemoryReader reader, TenantIsolationMetadata settings, TenantExperienceFactSource source, CancellationToken ct)
    {
        foreach (var receipt in ExperienceReadReceipts(reader, TenantContinuityRules.Completed))
        {
            var request = _approvals.GetValueOrDefault(receipt.RequestId!.Value);
            if (request?.Operation.ContinuityRead?.Experience is not { } value || !TenantExperienceFactRules.Sources(value).Any(i => TenantExperienceCommitRules.FactMatches(source, i))
                || _approvalDecisions.GetValueOrDefault(request.RequestId) is not { Status: TenantApprovalStatus.Approved } decision) continue;
            try
            {
                var observed = RequireContinuityOperation(request, reader.Run, ct);
                ValidateAgentActor(reader.Security, decision.HumanPrincipalId);
                TenantContinuityReadRules.ReviewerAsync(request, observed, decision.HumanPrincipalId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
                var items = TenantExperienceFactRules.Sources(value).Select(i => tenantExperiences.GetValueOrDefault((i.TenantId, i.ExperienceId))?.Reference()).ToArray();
                if (items.Any(i => i is null)) continue;
                TenantExperienceConsentRules.FactsAsync(request, reader, settings, items.Select(i => i!).ToArray(), decision.HumanPrincipalId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult(); return true;
            }
            catch (AccessDeniedException) { }
        }
        return false;
    }
    private bool OwnExperienceConsolidation(TenantMemoryReader reader, TenantIsolationMetadata settings,
        TenantExperienceFactSource source, TenantExperienceReference item, HashSet<Guid> visited, CancellationToken ct)
    {
        if (item.Visibility != ExperienceVisibility.Superseded || item.Revision != source.Revision + 1 || item.ContentHash != source.ContentHash || visited.Count >= 100 || !visited.Add(item.ExperienceId)) return false;
        foreach (var receipt in ExperienceReadReceipts(reader, TenantExperienceRules.Consolidated))
        {
            var request = _approvals.GetValueOrDefault(receipt.RequestId!.Value);
            if (request?.Operation.ExperienceOperation is not { ToolName: TenantExperienceToolContract.Consolidate, ResultExperienceId: { } resultId } value
                || !TenantExperienceRules.Sources(value).Any(i => i.ExperienceId == source.ExperienceId && i.ContentHash == source.ContentHash && i.Revision == source.Revision)
                || _approvalDecisions.GetValueOrDefault(request.RequestId) is not { Status: TenantApprovalStatus.Approved } decision
                || tenantExperiences.GetValueOrDefault((reader.Security.Tenant.TenantId, resultId)) is not { } result) continue;
            try
            {
                ValidateAgentActor(reader.Security, decision.HumanPrincipalId);
                TenantExperienceConsentRules.OwnConsolidationAsync(request, reader, settings, source, item, result, decision.HumanPrincipalId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
                if (result.Visibility == ExperienceVisibility.Eligible && result.Revision == 1 || OwnExperienceConsolidation(reader, settings,
                    new(result.TenantId, result.ExperienceId, result.ContentHash, 1), result.Reference(), new(visited), ct)) return true;
            }
            catch (Exception error) when (error is AccessDeniedException or SecurityConflictException) { }
        }
        return false;
    }
    private void RequireExperienceCheckpoint(AgentRun run, AgentRunCheckpoint checkpoint, CancellationToken ct)
    {
        var sources = TenantExperienceReferences.Sources(checkpoint); var facts = TenantExperienceReferences.Facts(checkpoint);
        if (TenantExperienceReferences.Sources(run.Checkpoint).Except(sources).Any() || TenantExperienceReferences.Facts(run.Checkpoint).Except(facts).Any()) throw new AccessDeniedException("experience_source_unavailable");
        RequireExperienceSources(run, sources, facts, ct);
    }
}
