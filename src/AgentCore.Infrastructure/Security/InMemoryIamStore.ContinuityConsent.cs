using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    private bool ContinuitySourceOwnersAvailable(TenantMemoryReader reader, PreparedContinuityRead read) => TenantContinuityRules.Sessions(read).All(source =>
        sessionBacking.ReadTenantSession(reader.Security.Tenant.TenantId, source.SessionId) is { } owned && owned.Owner.AgentInstanceId == reader.Run.AgentInstanceId && owned.Owner.ExecutorPrincipalId == reader.Agent.PrincipalId);
    private bool HistoricalReadConsented(TenantContinuityObservation observed, TenantHistoricalSource source, CancellationToken ct)
    {
        var reader = observed.Reader;
        foreach (var receipt in _operationReceipts.Values.Where(r => r.TenantId == reader.Run.Owner.TenantId && r.SessionId == reader.Run.SessionId
            && r.RootActivationId == reader.Run.Admission.Activation.RootActivationId && r.Outcome == TenantOperationOutcome.Completed && r.RequestId is not null && r.ReasonCode == TenantContinuityRules.Completed))
        {
            var request = _approvals.GetValueOrDefault(receipt.RequestId!.Value);
            if (request?.Operation.ContinuityRead is not { } read || !TenantContinuityRules.Sessions(read).Any(s => s.SessionId == source.SessionId && s.Revision == source.Revision
                && s.ThroughEntrySequence == source.ThroughEntrySequence && s.AfterEntrySequence == source.AfterEntrySequence && s.Limit == source.Limit && s.ObservationHash == source.ObservationHash)
                || _approvalDecisions.GetValueOrDefault(request.RequestId) is not { Status: TenantApprovalStatus.Approved } decision || !ContinuitySourceOwnersAvailable(reader, read)) continue;
            var memory = read.Memory is null ? [] : TenantMemoryOperationRules.Sources(read.Memory).Select(s => learnedMemories.GetValueOrDefault((request.TenantId, s.MemoryId))?.Reference()).ToArray();
            if (memory.Any(s => s is null)) continue;
            try
            {
                ValidateAgentActor(reader.Security, decision.HumanPrincipalId);
                TenantContinuityReadRules.ConsentAsync(request, reader, memory.Select(s => s!).ToArray(), decision.HumanPrincipalId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult(); return true;
            }
            catch (AccessDeniedException) { }
        }
        return false;
    }
    private TenantContinuityObservation RequireContinuityOperation(TenantApprovalRequest request, AgentRun run, CancellationToken ct)
    {
        var read = request.Operation.ContinuityRead ?? throw new SecurityConflictException("operation_mismatch");
        var observed = ObserveContinuity(run, AgentCore.Application.Tools.TenantContinuityToolContract.Parse(read.ToolName, read.ArgumentsJson), read, null, ct);
        TenantContinuityReadRules.RawAsync(observed, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult(); return observed;
    }
}
