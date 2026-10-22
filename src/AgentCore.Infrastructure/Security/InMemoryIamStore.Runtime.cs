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
    { ct.ThrowIfCancellationRequested(); lock (_gate) { RequireRuntimeOwner(frame.Owner); SaveTenantRuntimeState(frame, snapshot, expectedRevision, (clock ?? TimeProvider.System).GetUtcNow()); return ValueTask.CompletedTask; } }
    public ValueTask<ConversationHistoryPage?> ReadTenantRuntimeHistoryAsync(TenantRuntimeFrame frame, long? after, long? before, int limit, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) { RequireRuntimeOwner(frame.Owner); return ValueTask.FromResult(sessionBacking.ReadTenantRuntimeHistory(frame, after, before, Math.Clamp(limit, 1, 1000), (clock ?? TimeProvider.System).GetUtcNow())); } }
    public ValueTask<IReadOnlyList<AgentRun>> ListRunnableTenantRunsAsync(DateTimeOffset at, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            var result = sessionBacking.AgentRuns.Runs.Values.Where(r => !TenantInputBlocked(r) && (r.ApprovalRequestId is null || _approvals.GetValueOrDefault(r.ApprovalRequestId.Value) is { Status: not TenantApprovalStatus.Pending }) && (r.Admission.Activation.Kind is ActivationKind.UserTurn or ActivationKind.ApprovalContinuation or ActivationKind.ConfigurationContinuation or ActivationKind.ImmediateBackground or ActivationKind.BackgroundCompleted or ActivationKind.ScheduledWork or ActivationKind.ManualBackground or ActivationKind.CoreEvent or ActivationKind.ApplicationEvent)
                && r.Owner.TenantId is { } tenant && _tenants.GetValueOrDefault(tenant)?.Tenant.Status == IdentityStatus.Active
                && sessionBacking.OwnsTenantSession(tenant, r.SessionId) && (r.Status == AgentRunStatus.Queued || r.Status == AgentRunStatus.WaitingToRetry && r.NextRetryAtUtc <= at
                    || r.Status == AgentRunStatus.Running && r.Claim!.LeaseExpiresAtUtc <= at
                    || r.Status == AgentRunStatus.WaitingForSignal && AgentRunWaitExecution.IsReady(r, at, TenantWaitChildren(r, r.Wait!))))
                .OrderBy(r => r.CreatedAtUtc.ToUnixTimeMilliseconds()).ThenBy(InputOrdinal)
                .ThenBy(r => r.AgentRunId).Take(Math.Clamp(limit, 1, 100)).ToArray();
            return ValueTask.FromResult<IReadOnlyList<AgentRun>>(result);
        }
    }
    public ValueTask<AgentRun> ApplyTenantRuntimeCommandAsync(AgentRunOwner owner, Guid runId, AgentRunCommand command, Guid diagnosticId, CancellationToken ct = default, Guid? runtimeGeneration = null)
    {
        ct.ThrowIfCancellationRequested(); TenantRuntimeRules.RequireOwner(owner);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var run = sessionBacking.ReadTenantRun(owner.TenantId!.Value, runId);
            if (run is null || run.Owner != owner) throw AgentCoreErrors.NotFound("Run was not found.");
            if (runtimeGeneration is { } generation && run.Claim?.Generation != generation) throw new AccessDeniedException("tenant_runtime_unavailable");
            if (command is AgentRunCommand.Claim)
            {
                if (run.Admission.Activation.Kind is not (ActivationKind.UserTurn or ActivationKind.ApprovalContinuation or ActivationKind.ConfigurationContinuation or ActivationKind.ImmediateBackground or ActivationKind.BackgroundCompleted or ActivationKind.ScheduledWork or ActivationKind.ManualBackground or ActivationKind.CoreEvent or ActivationKind.ApplicationEvent)) throw AgentCoreErrors.Conflict("Canonical tenant continuation service required.");
                _ = TenantExecutionLineage.RootAsync(run, this, ct).AsTask().GetAwaiter().GetResult();
                if (run.ApprovalRequestId is { } approvalId) TenantRunContinuationRules.Require(run, _approvals.GetValueOrDefault(approvalId), run.Admission.ApprovalResume is { } resume ? sessionBacking.ReadTenantRun(owner.TenantId!.Value, resume.SourceRunId) : null);
                try { RequireRuntimeOwner(owner); } catch (AccessDeniedException) { throw AgentCoreErrors.Conflict("Executor is unavailable."); }
                if (run.Admission.OutputContract == AgentRunOutputContract.CompletionReport && sessionBacking.AgentRuns.Runs.Values.Any(r => r.SessionId == run.SessionId && r.AgentRunId != runId && !r.IsTerminal))
                    throw AgentCoreErrors.Conflict("Completion waits for pending user work.");
                if (TenantInputBlocked(run))
                    throw AgentCoreErrors.Conflict("Session already owns an active execution.");
            }
            if (command is AgentRunCommand.Checkpoint profileCheckpoint)
            {
                RequireMemoryCheckpoint(run, profileCheckpoint.Value, ct);
                var proposedProfile = AgentRunToolCallCheckpoint.ReadExecutionState(profileCheckpoint.Value).TenantProfileSource;
                if (AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint).TenantProfileSource is not null && proposedProfile is null)
                    throw new AccessDeniedException("profile_source_unavailable");
                if (proposedProfile is not null) RequireProfileSource(run, proposedProfile, ct);
            }
            if (command is AgentRunCommand.Complete) RequireTenantEventRead(run, ct);
            if (command is AgentRunCommand.Complete { Learning: not null }) throw AgentCoreErrors.Conflict("Learning requires the atomic response outcome transaction.");
            if (command is AgentRunCommand.Complete { OutcomeEntryId: { } entryId })
            {
                RequireTenantOutputSources(run, ct);
                if (sessionBacking.AgentRuns.CompletionReceipts.Values.Select(AgentCore.Infrastructure.Persistence.CompletionInboxMapping.Read)
                    .Any(i => i.Owner == owner && i.Status == CompletionInboxStatus.Claimed && i.ClaimRunId == runId && i.Acknowledgment is not null))
                    throw AgentCoreErrors.Conflict("Acknowledgment requires the atomic outcome transaction.");
                var entry = sessionBacking.FindEntry(run.SessionId, entryId);
                if (entry is not { Role: ConversationRole.Assistant, Status: EntryStatus.Completed } || entry.ResponseId != run.ResponseId) throw AgentCoreErrors.Conflict("Outcome must reference the saved response.");
            }
            if (command is AgentRunCommand.BeginApproval or AgentRunCommand.DecideApproval or AgentRunCommand.ExpireApproval)
                throw AgentCoreErrors.Conflict("This tenant continuation requires its canonical service.");
            if (run.Claim is { } claim && claim.LeaseExpiresAtUtc <= (clock ?? TimeProvider.System).GetUtcNow()
                && command is not (AgentRunCommand.Recover or AgentRunCommand.RequestCancellation or AgentRunCommand.DeadlineExceeded)) throw AgentCoreErrors.Conflict("Execution claim expired.");
            try
            {
                AgentRun next;
                if (command is AgentRunCommand.SuspendWait suspend)
                {
                    RequireTenantWait(run, suspend, ct);
                    next = AgentRunWaitExecution.Apply(run, command, TenantWaitChildren(run, suspend.Wait, true));
                }
                else if (command is AgentRunCommand.ResumeWait)
                    next = AgentRunWaitExecution.Apply(run, command, TenantWaitChildren(run, run.Wait ?? throw AgentCoreErrors.Conflict("Run is not waiting.")));
                else next = command.Apply(run, () => diagnosticId);
                if (command is AgentRunCommand.Fail { Code: "protected-output-unavailable" }) sessionBacking.SettleRefusedTenantOutput(run, next);
                var coverage = command is AgentRunCommand.Checkpoint coverageCheckpoint && TenantExperienceReferences.Sources(coverageCheckpoint.Value).Count > 0 ? PrepareTenantExperienceCoverage(TenantCoreRoot(run), coverageCheckpoint.Value) : null;
                sessionBacking.SaveTenantOperationRun(next, run.Revision);
                if (coverage is { } acknowledged) lock (sessionBacking.CoreEvents.TenantGate) sessionBacking.CoreEvents.AcknowledgeTenant(acknowledged.Bucket, acknowledged.Sources);
                AppendTenantCoreRun(run, next); SettleTenantCompletion(next, run.Claim?.Generation); return ValueTask.FromResult(next);
            }
            catch (Exception ex) when (ex is AgentRunTransitionException or ArgumentException) { throw AgentCore.Infrastructure.Persistence.AgentRunStoreMapping.Map(ex); }
        }
    }
}
