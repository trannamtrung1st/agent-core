using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantRunProfileService(ITenantRunProfileStore storage, TenantOperationAuthorizer operations)
{
    public async ValueTask<TenantRunProfileObservation?> ReadAsync(AgentRun run, CancellationToken ct = default)
    {
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId,
            run.Claim?.Generation ?? throw new AccessDeniedException("profile_context_unavailable"));
        var observation = await storage.ReadTenantRunProfileAsync(frame, ct).ConfigureAwait(false);
        if (observation is null) return null;
        var (current, state) = await operations.ResolveAsync(new(run.Owner.TenantId!.Value, run.AgentRunId), ct).ConfigureAwait(false);
        if (current.Owner != run.Owner || current.Claim?.Generation != frame.Generation) throw new AccessDeniedException("profile_context_unavailable");
        var decision = await operations.Check(state, current, "memory.read", AccessScope.ForResource(state.Tenant.TenantId, "agent", run.AgentInstanceId.ToString("D")), ct,
            purpose: ExecutionAuthorizationPurpose.Inspection).ConfigureAwait(false);
        if (decision.Decision != OperationDecisionKind.Allowed) return null;
        await storage.RequireTenantProfileProjectionAsync(frame, observation, ct).ConfigureAwait(false);
        return observation;
    }
    public ValueTask RequireProjectionAsync(AgentRun run, TenantRunProfileObservation? observation, CancellationToken ct = default) => observation is null ? ValueTask.CompletedTask
        : storage.RequireTenantProfileProjectionAsync(new(run.Owner, run.SessionId, run.AgentRunId,
            run.Claim?.Generation ?? throw new AccessDeniedException("profile_context_unavailable")), observation, ct);
    public ValueTask RequireAsync(AgentRun run, TenantProfileSource? source, CancellationToken ct = default) => source is null ? ValueTask.CompletedTask
        : storage.RequireTenantProfileSourceAsync(new(run.Owner.TenantId ?? throw new AccessDeniedException("profile_context_unavailable"), run.AgentRunId), source, run.Claim?.Generation, ct);
}
