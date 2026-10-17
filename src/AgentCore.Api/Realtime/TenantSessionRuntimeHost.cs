using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Realtime;

// Canonical runtimes never enter SessionHost's legacy connection/control lookup or SignalR output path.
public sealed class TenantSessionRuntimeHost(SessionRuntimeFactory factory, ITenantRuntimeStore storage, ITenantRunStore runs,
    TenantRunExecutionAuthority authority, TenantToolAuthorization tools, SessionHost capacity, IIdGenerator ids, TimeProvider time,
    ILogger<TenantSessionRuntimeHost> logger) : ITenantRunDispatcher, IAsyncDisposable
{
    private sealed class Live(AgentRun run, SessionRuntime runtime)
    {
        public AgentRun Run { get; } = run;
        public SessionRuntime Runtime { get; } = runtime;
        public CancellationTokenSource Lifetime { get; } = new();
        public TaskCompletionSource<bool> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Live> _live = [];
    private readonly Dictionary<(Guid Run, Guid Generation), TaskCompletionSource<bool>> _dispatching = [];
    private bool _stopping;
    private sealed class PrivateOutput : ISessionOutput
    { public ValueTask PublishAsync(SessionOutput output, CancellationToken cancellationToken = default) => ValueTask.CompletedTask; }

    public ValueTask<bool> AdmitCompletionAsync(BackgroundCompletionCandidate source, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public ValueTask<bool> RepairPendingInputsAsync(Guid sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public async ValueTask<bool> DispatchAsync(AgentRun run, CancellationToken cancellationToken = default)
    {
        if (run.Owner.TenantId is null || run.Status != AgentRunStatus.Running || run.Claim is null) return false;
        var key = (run.AgentRunId, run.Claim.Generation); TaskCompletionSource<bool> attempt; var owner = false;
        lock (_gate)
        {
            if (_stopping) return false;
            if (!_dispatching.TryGetValue(key, out attempt!))
            { attempt = new(TaskCreationOptions.RunContinuationsAsynchronously); _dispatching.Add(key, attempt); owner = true; }
        }
        if (!owner) return await attempt.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var accepted = await DispatchClaimAsync(run, cancellationToken).ConfigureAwait(false);
            attempt.TrySetResult(accepted); return accepted;
        }
        catch { attempt.TrySetResult(false); throw; }
        finally { lock (_gate) { if (_dispatching.GetValueOrDefault(key) == attempt) _dispatching.Remove(key); } }
    }
    private async ValueTask<bool> DispatchClaimAsync(AgentRun proposed, CancellationToken cancellationToken)
    {
        var run = await runs.FindTenantRunAsync(proposed.Owner.TenantId!.Value, proposed.AgentRunId, cancellationToken).ConfigureAwait(false);
        if (run is not { Status: AgentRunStatus.Running, Claim: { } claim } || claim.Generation != proposed.Claim!.Generation) return false;
        Live? existing = null; Live? collision = null;
        lock (_gate)
        {
            if (_stopping) return false;
            if (_live.TryGetValue(run.SessionId, out var old))
            {
                if (old.Run.AgentRunId == run.AgentRunId && old.Run.Claim!.Generation == claim.Generation) existing = old;
                else collision = old;
            }
        }
        if (existing is not null) return await existing.Accepted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (collision is not null)
        {
            await CancelStaleLiveAsync(collision, cancellationToken).ConfigureAwait(false);
            await DeferUnacceptedAsync(run, cancellationToken).ConfigureAwait(false); return false;
        }
        if (await authority.CurrentDefinitionAsync(run, cancellationToken).ConfigureAwait(false) is null)
        {
            await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Fail(run.Revision, time.GetUtcNow(), claim.Generation,
                "authority-unavailable", "Current tenant execution authority is unavailable.", false, null, ids.NewId()), ids.NewId(), cancellationToken).ConfigureAwait(false);
            return true;
        }
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, claim.Generation);
        var snapshot = await storage.LoadTenantRuntimeAsync(frame, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return false;
        Live? admitted = null; var refused = false;
        lock (_gate)
        {
            if (_stopping) return false;
            if (_live.TryGetValue(run.SessionId, out var raced))
            {
                if (raced.Run.AgentRunId == run.AgentRunId && raced.Run.Claim!.Generation == claim.Generation) existing = raced;
                else { collision = raced; refused = true; }
            }
            else if (!capacity.TryReserveTenantRuntime()) refused = true;
            else
            {
                try { admitted = new(run, factory.CreateTenant(snapshot, new PrivateOutput(), frame, storage, runs, authority, tools)); _live.Add(run.SessionId, admitted); }
                catch { capacity.ReleaseTenantRuntime(); throw; }
            }
        }
        if (existing is not null) return await existing.Accepted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (refused)
        {
            if (collision is not null) await CancelStaleLiveAsync(collision, cancellationToken).ConfigureAwait(false);
            await DeferUnacceptedAsync(run, cancellationToken).ConfigureAwait(false); return false;
        }
        var live = admitted ?? throw new InvalidOperationException("Tenant Runtime admission was not resolved.");
        try
        {
            using var dispatch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, live.Lifetime.Token);
            var accepted = await live.Runtime.DispatchAgentRunAsync(run.AgentRunId, headless: true, dispatch.Token).ConfigureAwait(false);
            live.Accepted.TrySetResult(accepted); return accepted;
        }
        catch { live.Accepted.TrySetResult(false); throw; }
        finally { _ = SuperviseAsync(run.SessionId, live); }
    }
    private async ValueTask CancelStaleLiveAsync(Live candidate, CancellationToken ct)
    {
        var current = await runs.FindTenantRunAsync(candidate.Run.Owner.TenantId!.Value, candidate.Run.AgentRunId, ct).ConfigureAwait(false);
        if (current is { Status: AgentRunStatus.Running, Claim: { } claim } && claim.Generation == candidate.Run.Claim!.Generation) return;
        lock (_gate) { if (_live.GetValueOrDefault(candidate.Run.SessionId) == candidate) candidate.Lifetime.Cancel(); }
    }
    private async ValueTask DeferUnacceptedAsync(AgentRun run, CancellationToken ct)
    {
        if (run.Admission.ApprovalResume is null) return;
        var at = time.GetUtcNow();
        try
        {
            await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
                new AgentRunCommand.DeferApprovalDispatch(run.Revision, at, run.Claim!.Generation, at.AddSeconds(5)), ids.NewId(), ct, run.Claim.Generation).ConfigureAwait(false);
        }
        catch (AgentCoreException ex) when (ex.Code is "Conflict" or "NotFound") { }
        catch (AccessDeniedException) { }
    }
    private async Task SuperviseAsync(Guid sessionId, Live live)
    {
        try
        {
            var seconds = (live.Run.Admission.ExecutionBudget?.Profile.DurationSeconds ?? 300) + 60;
            await live.Runtime.WaitUntilAcceptedConversationWorkSettledAsync(live.Lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(seconds), time, live.Lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (live.Lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            DiagnosticLog.Warning(logger, ex, ids.NewId(), "Tenant Runtime stopped before settling.", new(AgentRunId: live.Run.AgentRunId));
            try
            {
                var current = await runs.FindTenantRunAsync(live.Run.Owner.TenantId!.Value, live.Run.AgentRunId).ConfigureAwait(false);
                if (current is { Status: AgentRunStatus.Running, Claim: { } claim } && claim.Generation == live.Run.Claim!.Generation)
                    await storage.ApplyTenantRuntimeCommandAsync(current.Owner, current.AgentRunId,
                        new AgentRunCommand.DeadlineExceeded(current.Revision, time.GetUtcNow(), claim.Generation), ids.NewId()).ConfigureAwait(false);
            }
            catch (Exception failure) { DiagnosticLog.Warning(logger, failure, ids.NewId(), "Tenant Runtime cleanup could not settle.", new(AgentRunId: live.Run.AgentRunId)); }
        }
        finally
        {
            try { await live.Runtime.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { DiagnosticLog.Warning(logger, ex, ids.NewId(), "Tenant Runtime disposal failed.", new(AgentRunId: live.Run.AgentRunId)); }
            finally
            {
                lock (_gate) { if (_live.GetValueOrDefault(sessionId) == live) _live.Remove(sessionId); }
                capacity.ReleaseTenantRuntime(); live.Lifetime.Dispose();
                live.Stopped.TrySetResult();
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Live[] pending; lock (_gate) { _stopping = true; pending = _live.Values.ToArray(); foreach (var live in pending) live.Lifetime.Cancel(); }
        await Task.WhenAll(pending.Select(l => l.Stopped.Task)).ConfigureAwait(false);
    }
}
