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
    private bool _stopping;
    private sealed class PrivateOutput : ISessionOutput
    { public ValueTask PublishAsync(SessionOutput output, CancellationToken cancellationToken = default) => ValueTask.CompletedTask; }

    public ValueTask<bool> AdmitCompletionAsync(BackgroundCompletionCandidate source, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public ValueTask<bool> RepairPendingInputsAsync(Guid sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public async ValueTask<bool> DispatchAsync(AgentRun run, CancellationToken cancellationToken = default)
    {
        if (run.Owner.TenantId is null || run.Status != AgentRunStatus.Running || run.Claim is null) return false;
        Live? existing = null;
        lock (_gate)
        {
            if (_stopping) return false;
            if (_live.TryGetValue(run.SessionId, out var old))
            {
                if (old.Run.AgentRunId == run.AgentRunId && old.Run.Claim!.Generation == run.Claim.Generation) existing = old;
                else { old.Lifetime.Cancel(); return false; }
            }
        }
        if (existing is not null) return await existing.Accepted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (await authority.CurrentDefinitionAsync(run, cancellationToken).ConfigureAwait(false) is null)
        {
            await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Fail(run.Revision, time.GetUtcNow(), run.Claim.Generation,
                "authority-unavailable", "Current tenant execution authority is unavailable.", false, null, ids.NewId()), ids.NewId(), cancellationToken).ConfigureAwait(false);
            return true;
        }
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim.Generation);
        var snapshot = await storage.LoadTenantRuntimeAsync(frame, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return false;
        Live live;
        lock (_gate)
        {
            if (_stopping || _live.ContainsKey(run.SessionId) || !capacity.TryReserveTenantRuntime()) return false;
            try { live = new(run, factory.CreateTenant(snapshot, new PrivateOutput(), frame, storage, runs, authority, tools)); _live.Add(run.SessionId, live); }
            catch { capacity.ReleaseTenantRuntime(); throw; }
        }
        try
        {
            using var dispatch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, live.Lifetime.Token);
            var accepted = await live.Runtime.DispatchAgentRunAsync(run.AgentRunId, headless: true, dispatch.Token).ConfigureAwait(false);
            live.Accepted.TrySetResult(accepted); return accepted;
        }
        catch { live.Accepted.TrySetResult(false); throw; }
        finally { _ = SuperviseAsync(run.SessionId, live); }
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
