using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

public sealed class TenantAgentRunStore(ITenantRuntimeStore storage, ITenantRunStore runs, IIdGenerator ids, TenantRuntimeFrame? frame = null, TenantRunExecutionAuthority? authority = null) : IAgentRunStore, ITenantCompletionRuntimeStore
{
    private TenantRuntimeFrame CompletionFrame() => frame ?? throw new AccessDeniedException("tenant_runtime_scope_mismatch");
    public ValueTask<TenantCompletionPage> ReadTenantCompletionsAsync(TenantRuntimeFrame requested, string tool, Guid? childSessionId, Guid? cursor, bool pendingOnly, int limit, CancellationToken ct = default)
    {
        if (requested != CompletionFrame()) throw new AccessDeniedException("tenant_runtime_scope_mismatch");
        return storage.ReadTenantCompletionsAsync(frame!, tool, childSessionId, cursor, pendingOnly, limit, ct);
    }
    public ValueTask<TenantCompletionView> MutateTenantCompletionAsync(TenantRuntimeFrame requested, Guid childSessionId, long expectedRevision, string toolCallId, Guid token, string? usage, CancellationToken ct = default)
    {
        if (requested != CompletionFrame()) throw new AccessDeniedException("tenant_runtime_scope_mismatch");
        return storage.MutateTenantCompletionAsync(frame!, childSessionId, expectedRevision, toolCallId, token, usage, ct);
    }
    public ValueTask<bool> HasTenantCompletionClaimAsync(TenantRuntimeFrame requested, bool acknowledgedOnly, CancellationToken ct = default)
    {
        if (requested != CompletionFrame()) throw new AccessDeniedException("tenant_runtime_scope_mismatch");
        return storage.HasTenantCompletionClaimAsync(frame!, acknowledgedOnly, ct);
    }
    public ValueTask<bool> HasCompletionClaimAsync(AgentRunOwner owner, Guid parentRunId, Guid generation, DateTimeOffset now, CancellationToken ct = default)
    { Scope(owner, mutation: parentRunId); if (generation != CompletionFrame().Generation) throw new AccessDeniedException("tenant_runtime_scope_mismatch"); return storage.HasTenantCompletionClaimAsync(frame!, false, ct); }
    public ValueTask<bool> HasCompletionAcknowledgmentAsync(AgentRunOwner owner, Guid parentRunId, CancellationToken ct = default)
    { Scope(owner, mutation: parentRunId); return storage.HasTenantCompletionClaimAsync(CompletionFrame(), true, ct); }
    private void Scope(AgentRunOwner owner, Guid? session = null, Guid? mutation = null)
    {
        TenantRuntimeRules.RequireOwner(owner);
        if (frame is not null && (owner != frame.Owner || session is not null && session != frame.SessionId || mutation is not null && mutation != frame.RunId))
            throw new AccessDeniedException("tenant_runtime_scope_mismatch");
    }
    public async ValueTask<AgentRun?> GetAsync(AgentRunOwner owner, Guid agentRunId, CancellationToken cancellationToken = default)
    {
        Scope(owner); var run = await runs.FindTenantRunAsync(owner.TenantId!.Value, agentRunId, cancellationToken).ConfigureAwait(false);
        if (run is null || run.Owner != owner || frame is not null && run.SessionId != frame.SessionId) return null; return run;
    }
    public async ValueTask<IReadOnlyList<AgentRun>> ListForSessionAsync(AgentRunOwner owner, Guid sessionId, CancellationToken cancellationToken = default)
    {
        Scope(owner, sessionId); var result = new List<AgentRun>();
        foreach (var id in await runs.ListTenantRunIdsAsync(owner.TenantId!.Value, sessionId, cancellationToken).ConfigureAwait(false))
            if (await GetAsync(owner, id, cancellationToken).ConfigureAwait(false) is { } run) result.Add(run);
        return result;
    }
    public ValueTask<AgentRun> ApplyAsync(AgentRunOwner owner, Guid agentRunId, AgentRunCommand command, CancellationToken cancellationToken = default)
    { Scope(owner, mutation: agentRunId); return storage.ApplyTenantRuntimeCommandAsync(owner, agentRunId, command, ids.NewId(), cancellationToken, frame?.Generation); }
    public ValueTask<IReadOnlyList<AgentRun>> ListRunnableAsync(DateTimeOffset asOfUtc, int limit, CancellationToken cancellationToken = default)
    { if (frame is not null) throw new AccessDeniedException("tenant_runtime_scope_mismatch"); return storage.ListRunnableTenantRunsAsync(asOfUtc, limit, cancellationToken); }
    public ValueTask<IReadOnlyList<Guid>> ListPendingInputSessionsAsync(int limit, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<Guid>>([]);
    public ValueTask<Activation?> GetActivationAsync(AgentRunOwner owner, Guid activationId, CancellationToken cancellationToken = default) => ValueTask.FromResult<Activation?>(null);
    public ValueTask<AgentRun?> GetLatestForAutomationAsync(AgentRunOwner owner, Guid automationId, CancellationToken cancellationToken = default) => ValueTask.FromResult<AgentRun?>(null);
    public async ValueTask<AgentRunPage> ListPageAsync(AgentRunOwner owner, Guid? sessionId, Guid? before, int limit, CancellationToken cancellationToken = default)
    {
        if (sessionId is null) throw new AccessDeniedException("tenant_session_required");
        var all = await ListForSessionAsync(owner, sessionId.Value, cancellationToken).ConfigureAwait(false);
        var page = all.OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.AgentRunId).ToArray();
        if (before is { } cursor) page = page.SkipWhile(r => r.AgentRunId != cursor).Skip(1).ToArray();
        var take = page.Take(Math.Clamp(limit, 1, 100)).ToArray(); return new(take, take.LastOrDefault()?.AgentRunId, page.Length > take.Length);
    }
    public ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListUnreportedCompletionsAsync(int limit, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<BackgroundCompletionCandidate>>([]);
    public ValueTask<IReadOnlyList<BackgroundCompletionCandidate>> ListBackgroundChildrenAsync(AgentRunOwner owner, Guid parentSessionId, int limit, CancellationToken ct = default)
    { Scope(owner, parentSessionId); return ValueTask.FromResult<IReadOnlyList<BackgroundCompletionCandidate>>([]); }
    public ValueTask<IReadOnlyList<CompletionInboxItem>> ListCompletionInboxAsync(AgentRunOwner owner, Guid parentSessionId, int limit, DateTimeOffset now, CancellationToken ct = default)
    { Scope(owner, parentSessionId); return ValueTask.FromResult<IReadOnlyList<CompletionInboxItem>>([]); }
    public ValueTask<bool> HasCompletionReceiptAsync(AgentRunOwner owner, Guid childRunId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public ValueTask SkipCompletionReportAsync(AgentRunOwner owner, Guid childRunId, string reason, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new AccessDeniedException("tenant_continuation_unavailable");
    public ValueTask<AgentRunAdmissionResult> AdmitAsync(SessionSnapshot snapshot, long expectedSessionRevision, AgentRun run, CancellationToken cancellationToken = default) => throw new AccessDeniedException("tenant_admission_requires_authenticated_ingress");
    public ValueTask<AgentRunAdmissionResult> AdmitImmediateAsync(SessionSnapshot snapshot, AgentRun run, Guid expectedParentGeneration, CancellationToken cancellationToken = default)
    {
        if (frame is null || frame.Generation != expectedParentGeneration || run.Owner != frame.Owner
            || run.Admission.ExecutionContext?.ParentRunId != frame.RunId) throw new AccessDeniedException("tenant_runtime_scope_mismatch");
        return storage.AdmitTenantImmediateAsync(frame, snapshot, run, ids.NewId(), cancellationToken);
    }
    public ValueTask<AgentRunAdmissionResult> AdmitCompletionReportAsync(SessionSnapshot parent, long expectedRevision, AgentRun report, Guid childRunId, CancellationToken cancellationToken = default) => throw new AccessDeniedException("tenant_continuation_unavailable");
    public ValueTask<AgentRunAdmissionResult> AdmitOccurrenceAsync(SessionSnapshot snapshot, AgentRun run, long expectedRoutingRevision, CancellationToken cancellationToken = default) => throw new AccessDeniedException("tenant_trigger_unavailable");
    public async ValueTask<AgentRun> CommitOutcomeAsync(SessionSnapshot snapshot, long expectedSessionRevision, AgentRunOwner owner, Guid agentRunId, AgentRunCommand.Complete completion, Guid? draftEntryId, CancellationToken cancellationToken = default)
    {
        Scope(owner, snapshot.SessionId, agentRunId);
        if (frame is null) throw new AccessDeniedException("tenant_runtime_scope_mismatch");
        try { return await storage.CommitTenantOutcomeAsync(frame, snapshot, expectedSessionRevision, completion, draftEntryId, ids.NewId(), cancellationToken).ConfigureAwait(false); }
        catch (AccessDeniedException e) when (authority is not null && (e.ReasonCode.StartsWith("account_", StringComparison.Ordinal) || e.ReasonCode.StartsWith("profile_", StringComparison.Ordinal) || e.ReasonCode.StartsWith("memory_", StringComparison.Ordinal)))
        {
            if (e.ReasonCode.StartsWith("memory_", StringComparison.Ordinal)) await authority.RecordMemoryOutputFailureAsync(new(owner.TenantId!.Value, agentRunId), e.ReasonCode, cancellationToken).ConfigureAwait(false);
            else if (e.ReasonCode.StartsWith("profile_", StringComparison.Ordinal)) await authority.RecordProfileOutputFailureAsync(new(owner.TenantId!.Value, agentRunId), e.ReasonCode, cancellationToken).ConfigureAwait(false);
            else await authority.RecordAccountOutputFailureAsync(new(owner.TenantId!.Value, agentRunId), e.ReasonCode, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
