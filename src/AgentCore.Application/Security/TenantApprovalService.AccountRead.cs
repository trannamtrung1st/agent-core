using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareAccountReadAsync(TenantRunReference reference, TenantAccountReadIntent intent,
        long expectedRunRevision, Guid generation, CancellationToken ct = default)
    {
        try
        {
            var prepared = await (accountReads ?? throw new AccessDeniedException("tenant_effect_unavailable")).PrepareAsync(
                await AccountFrame(reference, expectedRunRevision, generation, ct).ConfigureAwait(false), intent, ct).ConfigureAwait(false);
            var operation = TenantApprovalRules.PrepareAccountRead(ids.NewId(), prepared.Agent.Instance.InstanceId, prepared.Read, prepared.Agent.Instance.Revision, prepared.Agent.Instance.PersonaRevision);
            return await PersistAsync(reference, prepared.Run, prepared.State, prepared.Agent, operation, prepared.Snapshot, true, expectedRunRevision, generation, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, "credentials.use", e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode,
                ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false); throw;
        }
    }
    private async ValueTask<AgentCore.Application.Ports.TenantRuntimeFrame> AccountFrame(TenantRunReference reference, long revision, Guid generation, CancellationToken ct)
    {
        var (run, _) = await operations.ResolveForDecisionAsync(reference, "credentials.use", ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
        if (run.Revision != revision || run.Claim?.Generation != generation) throw new AccessDeniedException("tenant_runtime_unavailable");
        return new(run.Owner, run.SessionId, run.AgentRunId, generation);
    }
}
