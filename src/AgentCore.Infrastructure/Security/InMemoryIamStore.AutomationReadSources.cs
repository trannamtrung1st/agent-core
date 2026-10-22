using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    public ValueTask RequireTenantAutomationReadSourcesAsync(TenantRunReference reference, IReadOnlyList<TenantAutomationReadReference> sources, Guid? generation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); WithTenantEvents(() => {
            var run = sessionBacking.ReadRetainedTenantOperationRun(reference.TenantId, reference.RunId) ?? throw new AccessDeniedException("automation_source_unavailable");
            if (generation is { } claim) _ = RequireMemoryFrame(new(run.Owner, run.SessionId, run.AgentRunId, claim));
            RequireAutomationReadSources(run, sources, ct); return true;
        }); return ValueTask.CompletedTask;
    }
    private void RequireAutomationReadSources(AgentRun run, IReadOnlyList<TenantAutomationReadReference> sources, CancellationToken ct)
    {
        if (sources.Count == 0) return;
        if (sources.Count > 64 || sources.Any(s => s.TenantId != run.Owner.TenantId || !TenantMemoryOperationRules.ValidHash(s.ObservationHash))) throw new AccessDeniedException("automation_source_unavailable");
        var reader = MemoryReader(run, ct); var iam = new IamAuthorizer(this);
        foreach (var source in sources)
        {
            var shape = TenantAutomationReadReferences.Shape(source, run.SessionId, reader.Root.SessionId);
            var observed = ObserveAutomationRead(run, source.ToolName, source.ArgumentsJson, shape, null, ct);
            var review = TenantAutomationReadRules.Targets(reader, observed.Value).Any(t => TenantSandboxReadOperationRules.NeedsReviewAsync(reader.Security,
                run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, t.Permission, t.Scope, iam, ct).AsTask().GetAwaiter().GetResult());
            if (review && !AutomationReadConsented(reader, source, ct)) throw new AccessDeniedException("automation_read_review_required");
        }
    }
    private bool AutomationReadConsented(TenantMemoryReader reader, TenantAutomationReadReference source, CancellationToken ct)
    {
        foreach (var receipt in _operationReceipts.Values.Where(r => r.TenantId == reader.Security.Tenant.TenantId && r.SessionId == reader.Run.SessionId
            && r.RootActivationId == reader.Run.Admission.Activation.RootActivationId && r.Outcome == TenantOperationOutcome.Completed && r.RequestId is not null && r.ReasonCode == TenantAutomationReadRules.Completed))
        {
            var request = _approvals.GetValueOrDefault(receipt.RequestId!.Value);
            if (request?.Operation.AutomationRead is not { } value || !TenantAutomationReadReferences.Matches(value, source)
                || _approvalDecisions.GetValueOrDefault(request.RequestId) is not { Status: TenantApprovalStatus.Approved } decision) continue;
            try {
                var observed = ObserveAutomationRead(reader.Run, value.ToolName, value.ArgumentsJson, value, null, ct); ValidateAgentActor(reader.Security, decision.HumanPrincipalId);
                TenantAutomationReadService.ReviewerAsync(request, observed, decision.HumanPrincipalId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult(); return true;
            } catch (Exception error) when (error is AccessDeniedException or SecurityConflictException) { }
        }
        return false;
    }
    private void RequireAutomationReadCheckpoint(AgentRun run, AgentRunCheckpoint checkpoint, CancellationToken ct)
    {
        var before = TenantAutomationReadReferences.Read(run.Checkpoint); var after = TenantAutomationReadReferences.Read(checkpoint);
        if (before.Except(after).Any()) throw new AccessDeniedException("automation_source_unavailable"); RequireAutomationReadSources(run, after, ct);
    }
}
