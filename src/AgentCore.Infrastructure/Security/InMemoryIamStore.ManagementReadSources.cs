using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    public ValueTask RequireTenantManagementReadSourcesAsync(TenantRunReference reference, IReadOnlyList<TenantManagementReadReference> sources, Guid? generation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); WithTenantEvents(() => {
            var run = sessionBacking.ReadRetainedTenantOperationRun(reference.TenantId, reference.RunId) ?? throw new AccessDeniedException("management_source_unavailable");
            if (generation is { } claim) _ = RequireMemoryFrame(new(run.Owner, run.SessionId, run.AgentRunId, claim));
            RequireManagementReadSources(run, sources, ct); return true;
        }); return ValueTask.CompletedTask;
    }
    private void RequireManagementReadSources(AgentRun run, IReadOnlyList<TenantManagementReadReference> sources, CancellationToken ct)
    {
        if (sources.Count == 0) return;
        if (sources.Count > 64 || sources.Any(s => s.TenantId != run.Owner.TenantId || !TenantMemoryOperationRules.ValidHash(s.ObservationHash))) throw new AccessDeniedException("management_source_unavailable");
        var reader = ManagementReader(run, null, ct); var iam = new IamAuthorizer(this);
        foreach (var source in sources)
        {
            var shape = TenantManagementReadReferences.Shape(source, run.SessionId, reader.Root.SessionId);
            var observed = ObserveManagementRead(run, source.ToolName, source.ArgumentsJson, shape, null, ct);
            var review = TenantSandboxReadOperationRules.NeedsReviewAsync(reader.Security, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId,
                "agents.read", TenantManagementReadRules.Scope(reader), iam, ct).AsTask().GetAwaiter().GetResult();
            if (review && !ManagementReadConsented(reader, source, ct)) throw new AccessDeniedException("management_read_review_required");
        }
    }
    private bool ManagementReadConsented(TenantManagementReader reader, TenantManagementReadReference source, CancellationToken ct)
    {
        foreach (var receipt in _operationReceipts.Values.Where(r => r.TenantId == reader.Security.Tenant.TenantId && r.SessionId == reader.Run.SessionId
            && r.RootActivationId == reader.Run.Admission.Activation.RootActivationId && r.Outcome == TenantOperationOutcome.Completed && r.RequestId is not null && r.ReasonCode == TenantManagementReadRules.Completed))
        {
            var request = _approvals.GetValueOrDefault(receipt.RequestId!.Value);
            if (request?.Operation.ManagementRead is not { } value || !TenantManagementReadReferences.Matches(value, source)
                || _approvalDecisions.GetValueOrDefault(request.RequestId) is not { Status: TenantApprovalStatus.Approved } decision) continue;
            try {
                var observed = ObserveManagementRead(reader.Run, value.ToolName, value.ArgumentsJson, value, null, ct); ValidateAgentActor(reader.Security, decision.HumanPrincipalId);
                TenantManagementReadService.ReviewerAsync(request, observed, decision.HumanPrincipalId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult(); return true;
            } catch (Exception error) when (error is AccessDeniedException or SecurityConflictException) { }
        }
        return false;
    }
    private void RequireManagementReadCheckpoint(AgentRun run, AgentRunCheckpoint checkpoint, CancellationToken ct)
    {
        var before = TenantManagementReadReferences.Read(run.Checkpoint); var after = TenantManagementReadReferences.Read(checkpoint);
        if (before.Except(after).Any()) throw new AccessDeniedException("management_source_unavailable"); RequireManagementReadSources(run, after, ct);
    }
}
