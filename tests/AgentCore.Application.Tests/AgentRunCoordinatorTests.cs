using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class AgentRunCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly AgentRunOwner Owner = new(Guid.NewGuid(), Guid.NewGuid());
    private static readonly AgentDefinition Definition = new(1, "general-assistant", 1,
        new AgentIdentity("Alex", "Assistant", "Help", "Calm"), ["Help"], "Instructions",
        new BehaviorPolicy("acknowledgeThenContinue", true, true), new ConversationPolicy("concise", true, "en", 256),
        new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]), new VoiceConfiguration(false, "default", 1),
        new ProviderPreferences("synthetic", null, null), new Dictionary<string, string>());

    [Fact]
    public async Task Accepted_user_batch_commits_one_run_and_fresh_identity_replay_returns_the_original()
    {
        var memory = new InMemoryMemoryStore();
        var store = new InMemoryAgentRunStore(memory, new SystemDiagnosticIdSource());
        var snapshot = Snapshot();
        var initial = Batch(snapshot);
        await store.AdmitAsync(snapshot, 0, initial);
        var replay = await store.AdmitAsync(snapshot, 0, Batch(snapshot));

        Assert.False(replay.Created);
        Assert.Equal(initial.AgentRunId, replay.Run.AgentRunId);
        Assert.Equal(initial.ResponseId, replay.Run.ResponseId);
        Assert.Equal(snapshot.Entries.Select(entry => entry.EntryId), initial.Admission.Activation.SourceEntryIds);
        Assert.Equal(snapshot.Entries.Select(entry => entry.Text), (await memory.LoadAsync(snapshot.SessionId))!.Entries.Select(entry => entry.Text));
        Assert.Single(await store.ListForSessionAsync(Owner, snapshot.SessionId));
    }

    [Fact]
    public void Admission_rejects_foreign_reordered_duplicate_and_unaccepted_inputs_before_execution()
    {
        var snapshot = Snapshot();
        foreach (var invalid in new IReadOnlyList<ConversationEntry>[]
                 {
                     [], [snapshot.Entries[1], snapshot.Entries[0]],
                     [snapshot.Entries[0], snapshot.Entries[0]],
                     [snapshot.Entries[0] with { EntryId = Guid.NewGuid() }],
                     [snapshot.Entries[0] with { Status = EntryStatus.Streaming }],
                     [snapshot.Entries[0] with { Role = ConversationRole.Assistant }]
                 })
            Assert.Equal("ValidationError", Assert.Throws<AgentCoreException>(() => Batch(snapshot, invalid)).Code);
        Assert.Throws<AgentCoreException>(() => Batch(snapshot with { PinnedPersona = null }));
        Assert.Throws<AgentCoreException>(() => Batch(snapshot with { ProfileId = null }));
        Assert.Throws<AgentCoreException>(() => Batch(snapshot with { ModelSelection = null }));
    }

    [Fact]
    public async Task Fast_path_and_scheduler_share_one_claim_and_dispatch_once()
    {
        var f = await Fixture.CreateAsync();
        var fast = f.Coordinator.DispatchAsync(Owner, f.Run.AgentRunId).AsTask();
        var scheduled = f.Coordinator.ExecuteRunnableAsync(8).AsTask();
        await Task.WhenAll(fast, scheduled);

        Assert.Equal(1, (await fast ? 1 : 0) + await scheduled);
        var dispatch = Assert.Single(f.Dispatcher.Dispatched);
        Assert.Equal(AgentRunStatus.Running, dispatch.Status);
        Assert.Equal(1, dispatch.AttemptCount);
        Assert.Equal(dispatch.Claim!.Generation, (await f.Store.GetAsync(Owner, f.Run.AgentRunId))!.Claim!.Generation);
        Assert.False(await f.Coordinator.DispatchAsync(new AgentRunOwner(Guid.NewGuid(), Owner.ProfileId), f.Run.AgentRunId));
        Assert.Single(f.Dispatcher.Dispatched);
    }

    [Fact]
    public async Task Expired_safe_claim_recovers_the_same_activation_response_and_run()
    {
        var f = await Fixture.CreateAsync();
        Assert.True(await f.Coordinator.DispatchAsync(Owner, f.Run.AgentRunId));
        var first = Assert.Single(f.Dispatcher.Dispatched);
        f.Time.Advance(AgentRunCoordinator.ClaimDuration);
        Assert.Equal(1, await f.Coordinator.ExecuteRunnableAsync(8));
        var resumed = f.Dispatcher.Dispatched[1];

        Assert.Equal(first.AgentRunId, resumed.AgentRunId);
        Assert.Equal(first.ActivationId, resumed.ActivationId);
        Assert.Equal(first.ResponseId, resumed.ResponseId);
        Assert.Equal(2, resumed.AttemptCount);
        Assert.NotEqual(first.Claim!.Generation, resumed.Claim!.Generation);
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Store.ApplyAsync(Owner,
            resumed.AgentRunId, new AgentRunCommand.Checkpoint(resumed.Revision, f.Time.GetUtcNow(), first.Claim.Generation,
                new AgentRunCheckpoint("{}", 0, 0, 1000), null)).AsTask())).Code);
    }

    [Fact]
    public async Task Uncertain_external_effect_is_terminalized_without_dispatch_or_retry()
    {
        var f = await Fixture.CreateAsync();
        await f.Coordinator.DispatchAsync(Owner, f.Run.AgentRunId);
        var running = Assert.Single(f.Dispatcher.Dispatched);
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var prepared = await f.Store.ApplyAsync(Owner, running.AgentRunId,
            new AgentRunCommand.MarkSideEffect(running.Revision, Now, running.Claim!.Generation,
                AgentRunSideEffectDisposition.Prepared, "email-1", hash));
        await f.Store.ApplyAsync(Owner, prepared.AgentRunId,
            new AgentRunCommand.MarkSideEffect(prepared.Revision, Now, running.Claim.Generation,
                AgentRunSideEffectDisposition.InFlight, "email-1", hash));
        f.Time.Advance(AgentRunCoordinator.ClaimDuration);

        Assert.Equal(0, await f.Coordinator.ExecuteRunnableAsync(8));
        Assert.Single(f.Dispatcher.Dispatched);
        var failed = (await f.Store.GetAsync(Owner, running.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Failed, failed.Status);
        Assert.Equal(AgentRunSideEffectDisposition.Indeterminate, failed.SideEffect.Disposition);
        Assert.Empty(await f.Store.ListRunnableAsync(f.Time.GetUtcNow(), 8));
    }

    [Fact]
    public async Task Approval_expiry_resumes_same_attempt_and_preserves_exact_decision_for_the_mailbox()
    {
        var f = await Fixture.CreateAsync();
        await f.Coordinator.DispatchAsync(Owner, f.Run.AgentRunId);
        var running = Assert.Single(f.Dispatcher.Dispatched);
        var checkpointed = await f.Store.ApplyAsync(Owner, running.AgentRunId,
            new AgentRunCommand.Checkpoint(running.Revision, Now, running.Claim!.Generation,
                new AgentRunCheckpoint("{}", 1, 0, 1000), null));
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var awaiting = await f.Store.ApplyAsync(Owner, running.AgentRunId,
            new AgentRunCommand.BeginApproval(checkpointed.Revision, Now, running.Claim.Generation,
                Guid.NewGuid(), "email.send", "{}", hash, "Send email", Now.AddMinutes(1)));
        Assert.False(await f.Coordinator.DispatchAsync(Owner, awaiting.AgentRunId));
        f.Time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, await f.Coordinator.ExecuteRunnableAsync(8));
        var resumed = f.Dispatcher.Dispatched[1];
        Assert.Equal(running.AgentRunId, resumed.AgentRunId);
        Assert.Equal(1, resumed.AttemptCount);
        Assert.Equal(AgentRunApprovalDecision.Expired, resumed.Approval!.Decision);
        Assert.Equal(hash, resumed.Approval.ActionHash);
    }

    [Fact]
    public async Task Missing_session_fails_only_its_run_and_the_next_session_still_dispatches()
    {
        var f = await Fixture.CreateAsync();
        var other = Snapshot();
        var next = Batch(other);
        await f.Store.AdmitAsync(other, 0, next);
        f.Dispatcher.MissingSession = f.Run.SessionId;

        Assert.Equal(1, await f.Coordinator.ExecuteRunnableAsync(8));
        Assert.Equal(2, f.Dispatcher.Dispatched.Count);
        var failed = (await f.Store.GetAsync(Owner, f.Run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Failed, failed.Status);
        Assert.Equal("session-unavailable", failed.Failure!.Code);
        Assert.Equal(AgentRunStatus.Running, (await f.Store.GetAsync(Owner, next.AgentRunId))!.Status);
    }

    [Fact]
    public async Task Dispatch_exception_keeps_the_claim_until_safe_lease_recovery()
    {
        var f = await Fixture.CreateAsync();
        f.Dispatcher.Error = new InvalidOperationException("Transport failed after mailbox receipt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Coordinator.DispatchAsync(Owner, f.Run.AgentRunId).AsTask());
        var current = (await f.Store.GetAsync(Owner, f.Run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Running, current.Status);
        Assert.Equal(1, current.AttemptCount);
        Assert.False(await f.Coordinator.DispatchAsync(Owner, f.Run.AgentRunId));
        Assert.Single(f.Dispatcher.Dispatched);
        f.Dispatcher.Error = null;
        f.Time.Advance(AgentRunCoordinator.ClaimDuration);
        Assert.Equal(1, await f.Coordinator.ExecuteRunnableAsync(8));
    }

    private static SessionSnapshot Snapshot()
    {
        var entries = new[] { "Check A", "Check B" }.Select((text, index) => new ConversationEntry(Guid.NewGuid(), index + 1,
            null, ConversationRole.User, text, null, EntryStatus.Completed, SessionMode.Text, 0, text.Length, Now)).ToArray();
        return new SessionSnapshot(1, Guid.NewGuid(), 1, Definition, SessionMode.Text, null, SessionStatus.Created,
            entries, "", 0, null, Owner.ProfileId, Now, Now, Owner.AgentInstanceId, PinnedPersona: Definition.Identity,
            ModelSelection: new SessionModelSelection("synthetic", "synthetic", "scripted", ModelSelectionSource.SystemDefault, null));
    }

    private static AgentRun Batch(SessionSnapshot snapshot, IReadOnlyList<ConversationEntry>? users = null) =>
        AgentRunAdmissionFactory.ForAcceptedUserBatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), snapshot,
            users ?? snapshot.Entries, Now, []);

    private sealed class Dispatcher : IAgentRunDispatcher
    {
        public ValueTask<bool> AdmitCompletionAsync(BackgroundCompletionCandidate source, CancellationToken ct = default) => ValueTask.FromResult(false);
        public List<AgentRun> Dispatched { get; } = [];
        public Exception? Error { get; set; }
        public Guid? MissingSession { get; set; }
        public ValueTask<bool> RepairPendingInputsAsync(Guid sessionId, CancellationToken ct = default) => ValueTask.FromResult(false);
        public ValueTask<bool> DispatchAsync(AgentRun run, CancellationToken cancellationToken = default)
        {
            Dispatched.Add(run);
            if (run.SessionId == MissingSession) throw AgentCoreErrors.NotFound("Session was not found.");
            if (Error is { } exception) throw exception;
            return ValueTask.FromResult(true);
        }
    }

    private sealed record Fixture(InMemoryAgentRunStore Store, AgentRun Run, Dispatcher Dispatcher,
        FakeTimeProvider Time, AgentRunCoordinator Coordinator)
    {
        public static async Task<Fixture> CreateAsync()
        {
            var store = new InMemoryAgentRunStore(new InMemoryMemoryStore(), new SystemDiagnosticIdSource());
            var snapshot = Snapshot();
            var run = Batch(snapshot);
            await store.AdmitAsync(snapshot, 0, run);
            var time = new FakeTimeProvider(Now);
            var dispatcher = new Dispatcher();
            return new(store, run, dispatcher, time,
                new AgentRunCoordinator(store, dispatcher, new SystemIdGenerator(time), time));
        }
    }
}
