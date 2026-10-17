using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantRuntimeStore
{
    private void RequireRuntimeOwner(AgentRunOwner owner)
    {
        TenantRuntimeRules.RequireOwner(owner);
        if (!_tenants.TryGetValue(owner.TenantId!.Value, out var state) || state.Tenant.Status != IdentityStatus.Active)
            throw new AccessDeniedException("tenant_unavailable");
        var agent = FindTenantAgentAsync(state.Tenant.TenantId, owner.AgentInstanceId).Result;
        if (agent is null || agent.PrincipalId != owner.ExecutorPrincipalId || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("executor_unavailable");
        TenantSessionRules.RequireExecutor(state, agent);
    }
    public ValueTask<SessionSnapshot?> LoadTenantRuntimeAsync(TenantRuntimeFrame frame, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) { RequireRuntimeOwner(frame.Owner); return ValueTask.FromResult(sessionBacking.LoadTenantRuntime(frame, (clock ?? TimeProvider.System).GetUtcNow())); } }
    public ValueTask SaveTenantRuntimeAsync(TenantRuntimeFrame frame, SessionSnapshot snapshot, long expectedRevision, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) { RequireRuntimeOwner(frame.Owner); sessionBacking.SaveTenantRuntime(frame, snapshot, expectedRevision, (clock ?? TimeProvider.System).GetUtcNow()); return ValueTask.CompletedTask; } }
    public ValueTask<ConversationHistoryPage?> ReadTenantRuntimeHistoryAsync(TenantRuntimeFrame frame, long? after, long? before, int limit, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) { RequireRuntimeOwner(frame.Owner); return ValueTask.FromResult(sessionBacking.ReadTenantRuntimeHistory(frame, after, before, Math.Clamp(limit, 1, 1000), (clock ?? TimeProvider.System).GetUtcNow())); } }
    public ValueTask<IReadOnlyList<AgentRun>> ListRunnableTenantRunsAsync(DateTimeOffset at, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            var result = sessionBacking.AgentRuns.Runs.Values.Where(r => r.ApprovalRequestId is null && r.Admission.Activation.Kind == ActivationKind.UserTurn
                && r.Owner.TenantId is { } tenant && _tenants.GetValueOrDefault(tenant)?.Tenant.Status == IdentityStatus.Active
                && sessionBacking.OwnsTenantSession(tenant, r.SessionId) && (r.Status == AgentRunStatus.Queued || r.Status == AgentRunStatus.WaitingToRetry && r.NextRetryAtUtc <= at
                    || r.Status == AgentRunStatus.Running && r.Claim!.LeaseExpiresAtUtc <= at))
                .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Admission.Activation.SourceEntryIds.Select(id => sessionBacking.FindEntry(r.SessionId, id)?.Sequence ?? long.MaxValue).DefaultIfEmpty(long.MaxValue).Min())
                .ThenBy(r => r.AgentRunId).Take(Math.Clamp(limit, 1, 100)).ToArray();
            return ValueTask.FromResult<IReadOnlyList<AgentRun>>(result);
        }
    }
    public ValueTask<AgentRun> ApplyTenantRuntimeCommandAsync(AgentRunOwner owner, Guid runId, AgentRunCommand command, Guid diagnosticId, CancellationToken ct = default, Guid? runtimeGeneration = null)
    {
        ct.ThrowIfCancellationRequested(); TenantRuntimeRules.RequireOwner(owner);
        lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            var run = sessionBacking.ReadTenantRun(owner.TenantId!.Value, runId);
            if (run is null || run.Owner != owner) throw AgentCoreErrors.NotFound("Run was not found.");
            if (runtimeGeneration is { } generation && run.Claim?.Generation != generation) throw new AccessDeniedException("tenant_runtime_unavailable");
            if (command is AgentRunCommand.Claim)
            {
                if (run.ApprovalRequestId is not null || run.Admission.Activation.Kind != ActivationKind.UserTurn) throw AgentCoreErrors.Conflict("Canonical tenant continuation service required.");
                try { RequireRuntimeOwner(owner); } catch (AccessDeniedException) { throw AgentCoreErrors.Conflict("Executor is unavailable."); }
                if (sessionBacking.AgentRuns.Runs.Values.Any(r => r.SessionId == run.SessionId && r.AgentRunId != runId && r.Status is AgentRunStatus.Running or AgentRunStatus.WaitingForSignal))
                    throw AgentCoreErrors.Conflict("Session already owns an active execution.");
            }
            if (command is AgentRunCommand.Complete { OutcomeEntryId: { } entryId })
            {
                var entry = sessionBacking.FindEntry(run.SessionId, entryId);
                if (entry is not { Role: ConversationRole.Assistant, Status: EntryStatus.Completed } || entry.ResponseId != run.ResponseId) throw AgentCoreErrors.Conflict("Outcome must reference the saved response.");
            }
            if (command is AgentRunCommand.SuspendWait or AgentRunCommand.ResumeWait or AgentRunCommand.BeginApproval or AgentRunCommand.DecideApproval or AgentRunCommand.ExpireApproval)
                throw AgentCoreErrors.Conflict("This tenant continuation requires its canonical service.");
            if (run.Claim is { } claim && claim.LeaseExpiresAtUtc <= (clock ?? TimeProvider.System).GetUtcNow()
                && command is not (AgentRunCommand.Recover or AgentRunCommand.RequestCancellation or AgentRunCommand.DeadlineExceeded)) throw AgentCoreErrors.Conflict("Execution claim expired.");
            try
            {
                var next = command.Apply(run, () => diagnosticId); sessionBacking.SaveTenantOperationRun(next, run.Revision); return ValueTask.FromResult(next);
            }
            catch (Exception ex) when (ex is AgentRunTransitionException or ArgumentException) { throw AgentCore.Infrastructure.Persistence.AgentRunStoreMapping.Map(ex); }
        }
    }
}
