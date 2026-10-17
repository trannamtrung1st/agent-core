using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Security;

public sealed class TenantAgentRunStore(ITenantRuntimeStore storage, ITenantRunStore runs, IIdGenerator ids, TenantRuntimeFrame? frame = null) : IAgentRunStore
{
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
    public ValueTask<AgentRunAdmissionResult> AdmitImmediateAsync(SessionSnapshot snapshot, AgentRun run, Guid expectedParentGeneration, CancellationToken cancellationToken = default) => throw new AccessDeniedException("tenant_continuation_unavailable");
    public ValueTask<AgentRunAdmissionResult> AdmitCompletionReportAsync(SessionSnapshot parent, long expectedRevision, AgentRun report, Guid childRunId, CancellationToken cancellationToken = default) => throw new AccessDeniedException("tenant_continuation_unavailable");
    public ValueTask<AgentRunAdmissionResult> AdmitOccurrenceAsync(SessionSnapshot snapshot, AgentRun run, long expectedRoutingRevision, CancellationToken cancellationToken = default) => throw new AccessDeniedException("tenant_trigger_unavailable");
    public ValueTask<AgentRun> CommitOutcomeAsync(SessionSnapshot snapshot, long expectedSessionRevision, AgentRunOwner owner, Guid agentRunId, AgentRunCommand.Complete completion, Guid? draftEntryId, CancellationToken cancellationToken = default) => throw new AccessDeniedException("tenant_background_outcome_unavailable");
}
