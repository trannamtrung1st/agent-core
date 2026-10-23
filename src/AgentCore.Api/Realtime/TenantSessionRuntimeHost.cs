using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api.Realtime;

// Tenant Run lifetimes and recipient-authenticated delivery have separate owners.
public sealed partial class TenantSessionRuntimeHost(SessionRuntimeFactory factory, ITenantRuntimeStore storage, ITenantRunStore runs,
    TenantRunExecutionAuthority authority, TenantToolAuthorization tools, TenantRuntimeCapacity capacity, IIdGenerator ids, TimeProvider time,
    ILogger<TenantSessionRuntimeHost> logger, TenantRunContinuationService continuations, TenantRunCancellationService cancellations,
    TenantSessionLiveDelivery delivery, IIamStore inputSecurity, ITenantSessionStore inputSessions,
    TenantVoiceService? voiceService = null, VoiceAvailability? voiceAvailability = null,
    InteractionPolicy? voicePolicy = null, Func<TenantRunService>? voiceCommands = null,
    Func<TenantRequestIsolationService>? requestIsolation = null, ITenantRequestIsolationStore? requestIsolationStore = null) : ITenantRunDispatcher, IAsyncDisposable
{
    private sealed class Live(AgentRun run, SessionRuntime runtime, TenantSessionLiveDelivery.Source output, bool ownsCapacity)
    {
        public AgentRun Run { get; } = run;
        public bool OwnsCapacity = ownsCapacity;
        public SessionRuntime Runtime { get; } = runtime;
        public TenantSessionLiveDelivery.Source Output { get; } = output;
        public CancellationTokenSource Lifetime { get; } = new();
        public CancellationTokenSource OperationStop { get; } = new();
        public TaskCompletionSource<bool> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Live> _live = [];
    private sealed class PendingDispatch(Guid session)
    {
        public Guid Session { get; } = session;
        public bool RequestSettled;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Dictionary<(Guid Run, Guid Generation), PendingDispatch> _dispatching = [];
    private bool _stopping;
    // Deterministic fixture barrier around the existing claim/snapshot registration race.
    internal Func<Guid, Task>? BeforeDispatchRegistration { get; set; }
    internal Func<AgentRun, Task>? BeforeRuntimeRelease { get; set; }
    internal Action<AgentRun, AgentRun>? RuntimeCollisionObserved { get; set; }

    // Deterministic fixture barrier: a durable wait is visible before its supervised
    // generation has finished releasing the provider/runtime and capacity slot.
    // Observe the existing lifetime; this does not dispatch or mutate execution.
    internal Task WaitForGenerationReleaseAsync(Guid sessionId, Guid runId, Guid generation, CancellationToken ct)
    {
        Task stopped;
        lock (_gate)
            stopped = _live.TryGetValue(sessionId, out var live) && live.Run.AgentRunId == runId && live.Run.Claim?.Generation == generation
                ? live.Stopped.Task : Task.CompletedTask;
        return stopped.WaitAsync(ct);
    }

    public ValueTask<bool> RepairPendingInputsAsync(Guid sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public async ValueTask<bool> DispatchAsync(AgentRun run, CancellationToken cancellationToken = default)
    {
        if (run.Owner.TenantId is null || run.Status != AgentRunStatus.Running || run.Claim is null) return false;
        var key = (run.AgentRunId, run.Claim.Generation); PendingDispatch attempt; var owner = false;
        lock (_gate)
        {
            if (_stopping) return false;
            if (!_dispatching.TryGetValue(key, out attempt!))
            { attempt = new(run.SessionId); _dispatching.Add(key, attempt); owner = true; }
        }
        if (!owner) return await attempt.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var accepted = await DispatchClaimAsync(run, attempt, cancellationToken).ConfigureAwait(false);
            attempt.Completion.TrySetResult(accepted); return accepted;
        }
        catch { attempt.Completion.TrySetResult(false); throw; }
        finally { lock (_gate) { if (_dispatching.GetValueOrDefault(key) == attempt) _dispatching.Remove(key); } }
    }
    private async ValueTask<bool> DispatchClaimAsync(AgentRun proposed, PendingDispatch attempt, CancellationToken cancellationToken)
    {
        var run = await runs.FindTenantRunAsync(proposed.Owner.TenantId!.Value, proposed.AgentRunId, cancellationToken).ConfigureAwait(false);
        if (run is not { Status: AgentRunStatus.Running, Claim: { } claim } || claim.Generation != proposed.Claim!.Generation) return false;
        Live? existing = null; Live? collision = null; long sessionWriteEpoch;
        lock (_gate)
        {
            if (_stopping) return false;
            sessionWriteEpoch = _sessionWriteEpoch;
            if (_live.TryGetValue(run.SessionId, out var old))
            {
                if (old.Run.AgentRunId == run.AgentRunId && old.Run.Claim!.Generation == claim.Generation) existing = old;
                else collision = old;
            }
        }
        if (existing is not null) return await existing.Accepted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (collision is not null)
        {
            RuntimeCollisionObserved?.Invoke(run, collision.Run);
            await CancelStaleLiveAsync(collision, cancellationToken).ConfigureAwait(false);
            if (await DrainPreviousRuntimeAsync(run, collision, cancellationToken).ConfigureAwait(false))
                return await DispatchClaimAsync(run, attempt, cancellationToken).ConfigureAwait(false);
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
        if (BeforeDispatchRegistration is { } beforeRegistration) await beforeRegistration(run.SessionId).WaitAsync(cancellationToken).ConfigureAwait(false);
        Live? admitted = null; var refused = false; var ownsCapacity = false;
        lock (_gate)
        {
            if (_stopping) return false;
            if (attempt.RequestSettled || _cancelling.Contains((run.Owner.TenantId!.Value, run.AgentRunId)) || _quiescentWrites.Contains(run.SessionId) || sessionWriteEpoch != _sessionWriteEpoch) refused = true;
            else if (_live.TryGetValue(run.SessionId, out var raced))
            {
                if (raced.Run.AgentRunId == run.AgentRunId && raced.Run.Claim!.Generation == claim.Generation) existing = raced;
                else { collision = raced; refused = true; }
            }
            else if (!TryReserveVoiceOrRunCapacity(run.SessionId, out ownsCapacity)) refused = true;
            else
            {
                try
                {
                    var voiceLease = _voices.GetValueOrDefault(run.SessionId)?.Lease;
                    if (voiceLease?.Active != true) voiceLease = null;
                    var output = delivery.CreateSource(run, voiceLease);
                    admitted = new(run, factory.CreateTenant(snapshot, output, frame, storage, runs, authority, tools,
                        voiceLease is not null && _voices.GetValueOrDefault(run.SessionId) is { } voice
                            ? new(voiceLease, signal => QueueVoice(voice, new VoiceSignal(signal))) : null), output, ownsCapacity);
                    _live.Add(run.SessionId, admitted);
                }
                catch { if (ownsCapacity) capacity.ReleaseTenantRuntime(); throw; }
            }
        }
        if (existing is not null) return await existing.Accepted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (refused)
        {
            if (collision is not null)
            {
                RuntimeCollisionObserved?.Invoke(run, collision.Run);
                await CancelStaleLiveAsync(collision, cancellationToken).ConfigureAwait(false);
                if (await DrainPreviousRuntimeAsync(run, collision, cancellationToken).ConfigureAwait(false))
                    return await DispatchClaimAsync(run, attempt, cancellationToken).ConfigureAwait(false);
            }
            await DeferUnacceptedAsync(run, cancellationToken).ConfigureAwait(false); return false;
        }
        var live = admitted ?? throw new InvalidOperationException("Tenant Runtime admission was not resolved.");
        try
        {
            live.Output.Activate();
            using var dispatch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, live.Lifetime.Token, live.OperationStop.Token);
            try { await continuations.PrepareExternalEffectAsync(run, dispatch.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is AccessDeniedException or SecurityConflictException)
            {
                var current = await runs.FindTenantRunAsync(run.Owner.TenantId!.Value, run.AgentRunId, dispatch.Token).ConfigureAwait(false);
                if (current is { Status: AgentRunStatus.Running, Claim: { } currentClaim } && currentClaim.Generation == claim.Generation)
                {
                    var reason = ex is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)ex).ReasonCode;
                    AgentRunCommand command = current.CancellationRequested
                        ? new AgentRunCommand.CommitCancellation(current.Revision, time.GetUtcNow(), claim.Generation, null)
                        : new AgentRunCommand.Fail(current.Revision, time.GetUtcNow(), claim.Generation,
                            reason == "execution_budget_exhausted" ? "execution-budget-exhausted" : "approval-effect-unavailable",
                            "The saved approval operation could not be consumed safely.", false, null, ids.NewId());
                    await storage.ApplyTenantRuntimeCommandAsync(current.Owner, current.AgentRunId, command, ids.NewId(), dispatch.Token, claim.Generation).ConfigureAwait(false);
                }
                live.Accepted.TrySetResult(true); return true;
            }
            var accepted = await live.Runtime.DispatchAgentRunAsync(run.AgentRunId, headless: live.Output.Voice?.Active != true, dispatch.Token).ConfigureAwait(false);
            live.Accepted.TrySetResult(accepted); return accepted;
        }
        catch { live.Accepted.TrySetResult(false); throw; }
        finally { _ = SuperviseAsync(run.SessionId, live); }
    }
    private async ValueTask<bool> DrainPreviousRuntimeAsync(AgentRun run, Live previous, CancellationToken ct)
    {
        if (previous.Run.Owner != run.Owner || previous.Run.SessionId != run.SessionId) return false;
        if (previous.Run.AgentRunId == run.AgentRunId)
        {
            if (previous.Run.Claim!.Generation == run.Claim!.Generation || run.AttemptCount <= previous.Run.AttemptCount) return false;
        }
        else
        {
            var persisted = await runs.FindTenantRunAsync(run.Owner.TenantId!.Value, previous.Run.AgentRunId, ct).ConfigureAwait(false);
            if (persisted is null || !persisted.IsTerminal || persisted.Owner != run.Owner || persisted.SessionId != run.SessionId) return false;
        }
        // A terminal predecessor or an older retry generation can still own
        // the Session during disposal. Retain the new claim until it releases
        // the registry, then reload current authority/checkpoint for admission.
        await previous.Stopped.Task.WaitAsync(ct).ConfigureAwait(false);
        return true;
    }
    private async ValueTask CancelStaleLiveAsync(Live candidate, CancellationToken ct)
    {
        var current = await runs.FindTenantRunAsync(candidate.Run.Owner.TenantId!.Value, candidate.Run.AgentRunId, ct).ConfigureAwait(false);
        if (current is { Status: AgentRunStatus.Running, Claim: { } claim } && claim.Generation == candidate.Run.Claim!.Generation) return;
        // A durable completion commit precedes its final transport publication. Receipts
        // and new admissions must await that owning attempt's normal drain; cancelling
        // it here can abort publication and retire the attachment for its successor.
        var draining = current is { Status: AgentRunStatus.Completed } && current.Owner == candidate.Run.Owner
            && current.AttemptCount == candidate.Run.AttemptCount;
        bool currentLive; lock (_gate) currentLive = _live.GetValueOrDefault(candidate.Run.SessionId) == candidate;
        if (currentLive && !draining) StopTokens(candidate);
        RuntimeRetirementObserved?.Invoke(candidate.Run);
    }
    internal Action<AgentRun>? RuntimeRetirementObserved { get; set; }
    private async ValueTask DeferUnacceptedAsync(AgentRun run, CancellationToken ct)
    {
        var signal = run.WaitCount > 0 && run.Checkpoint?.AwaitingSignalAdmission == true;
        if (run.Admission.ApprovalResume is null && !signal) return;
        var at = time.GetUtcNow();
        try
        {
            AgentRunCommand command = signal ? new AgentRunCommand.DeferSignalDispatch(run.Revision, at, run.Claim!.Generation, at.AddSeconds(5))
                : new AgentRunCommand.DeferApprovalDispatch(run.Revision, at, run.Claim!.Generation, at.AddSeconds(5));
            await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, command, ids.NewId(), ct, run.Claim!.Generation).ConfigureAwait(false);
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
            DiagnosticLog.Warning(logger, ex, ids.NewId(), "Tenant Runtime stopped before settling.", new(AgentRunId: live.Run.AgentRunId,
                TenantId: live.Run.Owner.TenantId, ActorPrincipalId: live.Run.Owner.ExecutorPrincipalId));
            try
            {
                var current = await runs.FindTenantRunAsync(live.Run.Owner.TenantId!.Value, live.Run.AgentRunId).ConfigureAwait(false);
                if (current is { Status: AgentRunStatus.Running, Claim: { } claim } && claim.Generation == live.Run.Claim!.Generation)
                    await storage.ApplyTenantRuntimeCommandAsync(current.Owner, current.AgentRunId,
                        new AgentRunCommand.DeadlineExceeded(current.Revision, time.GetUtcNow(), claim.Generation), ids.NewId()).ConfigureAwait(false);
            }
            catch (Exception failure) { DiagnosticLog.Warning(logger, failure, ids.NewId(), "Tenant Runtime cleanup could not settle.", new(AgentRunId: live.Run.AgentRunId,
                TenantId: live.Run.Owner.TenantId, ActorPrincipalId: live.Run.Owner.ExecutorPrincipalId)); }
        }
        finally
        {
            try { await live.Runtime.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { DiagnosticLog.Warning(logger, ex, ids.NewId(), "Tenant Runtime disposal failed.", new(AgentRunId: live.Run.AgentRunId,
                TenantId: live.Run.Owner.TenantId, ActorPrincipalId: live.Run.Owner.ExecutorPrincipalId)); }
            finally
            {
                await live.Output.CloseAsync().ConfigureAwait(false);
                if (BeforeRuntimeRelease is { } beforeRelease) await beforeRelease(live.Run).ConfigureAwait(false);
                var release = false;
                lock (_gate)
                {
                    if (_live.GetValueOrDefault(sessionId) == live) _live.Remove(sessionId);
                    if (live.OwnsCapacity && _voices.GetValueOrDefault(sessionId) is { Lease.Retiring: false } voice)
                    { voice.OwnsCapacity = true; live.OwnsCapacity = false; }
                    release = live.OwnsCapacity; live.OwnsCapacity = false;
                }
                if (release) capacity.ReleaseTenantRuntime(); live.OperationStop.Dispose(); live.Lifetime.Dispose();
                await SettleReleasedRequestAsync(live).ConfigureAwait(false);
                live.Stopped.TrySetResult();
            }
        }
    }
    private Task? shutdown;
    public Task StopAsync(CancellationToken ct = default)
    {
        Task pending;
        lock (_gate)
        {
            _stopping = true; capacity.StopAdmissions();
            shutdown ??= DrainAsync(_live.Values.ToArray(), _voices.Values.Select(v => v.Lease).ToArray());
            pending = shutdown;
        }
        return pending.WaitAsync(ct);
    }
    public ValueTask DisposeAsync() => new(StopAsync());
    private async Task DrainAsync(Live[] pending, TenantVoiceLease[] speech)
    {
        var speechCleanup = Task.WhenAll(speech.Select(l => RetireVoiceAsync(l).AsTask()));
        foreach (var live in pending) StopTokens(live);
        await Task.WhenAll(pending.Select(l => l.Stopped.Task)).ConfigureAwait(false);
        await speechCleanup.ConfigureAwait(false);
    }
}
