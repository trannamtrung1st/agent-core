using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed class AgentRunAdmissionStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly AgentRunOwner Owner = new(Guid.Parse("019944af-0009-7000-8000-0000000000a1"), Guid.Parse("019944af-0009-7000-8000-0000000000b1"));
    private static readonly AgentDefinition Definition = new(1, "examiner", 1,
        new AgentIdentity("Alex", "Examiner", "Practice", "Calm"), ["Practice"], "Instructions",
        new BehaviorPolicy("acknowledgeThenContinue", true, true), new ConversationPolicy("concise", true, "en", 256),
        new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]), new VoiceConfiguration(true, "default", 1),
        new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"), new Dictionary<string, string>());
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_live_receipt_and_run_admit_atomically_and_replay_reuses_the_committed_identity(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var snapshot = Snapshot(Guid.NewGuid(), []);
        await f.Memory.SaveAsync(snapshot, 0);
        var occurrence = await PrepareLive(f, snapshot.SessionId);
        var activation = new Activation(Guid.NewGuid(), snapshot.SessionId, ActivationKind.ApplicationEvent, [],
            occurrence.OccurrenceId, occurrence.OccurrenceId, null, null, $"signal:{occurrence.OccurrenceId:D}", Now, "{}");
        var run = NewRun(activation);
        var stale = snapshot with { Revision = 3 };
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitAsync(stale, 2, run).AsTask())).Code);
        Assert.Empty(await f.Runs.ListForSessionAsync(Owner, snapshot.SessionId));
        Assert.Null((await f.Triggers.GetOccurrenceAsync(occurrence.Owner, occurrence.OccurrenceId))!.AcceptedAgentRunId);
        var committed = snapshot with { Revision = 2 };
        var result = await f.Runs.AdmitAsync(committed, 1, run);
        Assert.True(result.Created);
        await f.ReopenAsync();
        var receipt = (await f.Triggers.GetOccurrenceAsync(occurrence.Owner, occurrence.OccurrenceId))!;
        Assert.Equal(OccurrenceRoutingDisposition.AcceptedLive, receipt.Disposition);
        Assert.Equal(snapshot.SessionId, receipt.LiveSessionId);
        Assert.Equal(run.AgentRunId, receipt.AcceptedAgentRunId);
        Assert.Equal(Now, receipt.LiveEvaluationCompletedAtUtc);
        Assert.Empty(await f.Triggers.ListUnsettledLiveAsync(8));
        var replay = NewRun(new Activation(Guid.NewGuid(), snapshot.SessionId, ActivationKind.ApplicationEvent, [],
            occurrence.OccurrenceId, occurrence.OccurrenceId, null, null, activation.DedupeKey, Now, "{}"));
        var duplicate = await f.Runs.AdmitAsync(committed, 1, replay);
        Assert.False(duplicate.Created);
        Assert.Equal(run.AgentRunId, duplicate.Run.AgentRunId);
        Assert.Single(await f.Runs.ListForSessionAsync(Owner, snapshot.SessionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Quiet_native_receipt_survives_reopen_without_run_and_completed_receipts_do_not_starve_repair(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var snapshot = Snapshot(Guid.NewGuid(), []);
        await f.Memory.SaveAsync(snapshot, 0);
        var quiet = await PrepareLive(f, snapshot.SessionId);
        await f.Triggers.CompleteLiveEvaluationAsync(quiet.OccurrenceId, snapshot.SessionId, Now);
        var pending = await PrepareLive(f, snapshot.SessionId);
        await f.Triggers.ConfirmLiveBeginAsync(pending.OccurrenceId, pending.RoutingRevision, Now);
        await f.ReopenAsync();
        Assert.Equal(pending.OccurrenceId, Assert.Single(await f.Triggers.ListUnsettledLiveAsync(1)).OccurrenceId);
        Assert.Empty(await f.Runs.ListForSessionAsync(Owner, snapshot.SessionId));
        var receipt = (await f.Triggers.GetOccurrenceAsync(quiet.Owner, quiet.OccurrenceId))!;
        Assert.Equal(Now, receipt.LiveEvaluationCompletedAtUtc);
        Assert.Null(receipt.AcceptedAgentRunId);
        Assert.Null(await f.Triggers.CompleteLiveEvaluationAsync(quiet.OccurrenceId, snapshot.SessionId, Now));
        var second = await PrepareLive(f, snapshot.SessionId);
        await f.Triggers.ConfirmLiveBeginAsync(second.OccurrenceId, second.RoutingRevision, Now);
        var firstPage = await f.Triggers.ListUnsettledLiveAsync(1);
        var nextPage = await f.Triggers.ListUnsettledLiveAsync(1, firstPage[0].OccurrenceId);
        Assert.Single(nextPage);
        Assert.NotEqual(firstPage[0].OccurrenceId, nextPage[0].OccurrenceId);
        Assert.Empty(await f.Triggers.ListUnsettledLiveAsync(1, nextPage[0].OccurrenceId));
    }

    private static async Task<TriggerOccurrence> PrepareLive(Fixture f, Guid sessionId)
    {
        var occurrence = new TriggerOccurrence(Guid.NewGuid(), $"native:{Guid.NewGuid():D}", null,
            new(Owner.AgentInstanceId, Owner.ProfileId), TriggerSourceKind.ApplicationEvent, null, Now, Now, "{}",
            Guid.NewGuid(), null, OccurrenceRoutingDisposition.Pending, null, 0, null, null, null,
            modelPin: new("synthetic", "synthetic", "synthetic", null, ExecutionModelSource.ConversationDefault));
        await f.Triggers.AdmitOccurrenceAsync(occurrence);
        var claimId = Guid.NewGuid();
        await f.Triggers.TryClaimOccurrenceAsync(occurrence.OccurrenceId, claimId, Now.AddSeconds(30), Now);
        var prepared = (await f.Triggers.TryAcceptLiveAsync(occurrence.OccurrenceId, claimId, Now))!;
        return (await f.Triggers.BindLiveSessionAsync(occurrence.OccurrenceId, prepared.RoutingRevision, sessionId, Now))!;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Run_outcome_commits_session_and_terminal_state_together_and_quiet_work_removes_only_its_draft(bool sqlite, bool quiet)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var (snapshot, run) = UserTurn();
        await f.Runs.AdmitAsync(snapshot, 0, run);
        run = await Claim(f, run);
        var draft = new ConversationEntry(Guid.NewGuid(), 3, null, ConversationRole.Assistant, "", run.ResponseId,
            EntryStatus.Streaming, SessionMode.Text, 0, 0, Now);
        snapshot = snapshot with { Revision = 2, Entries = snapshot.Entries.Append(draft).ToArray(), LastEntrySequence = 3 };
        await f.Memory.SaveAsync(snapshot, 1);
        var outcome = snapshot with { Revision = 3, Entries = quiet ? snapshot.Entries.Where(entry => entry.EntryId != draft.EntryId).ToArray()
            : snapshot.Entries.Select(entry => entry.EntryId == draft.EntryId ? draft with { Status = EntryStatus.Completed, Text = "Finished" } : entry).ToArray() };
        var command = new AgentRunCommand.Complete(run.Revision, Now, run.Claim!.Generation, quiet ? "" : "Finished",
            quiet ? AgentRunOutcomeKind.NoAction : AgentRunOutcomeKind.Response, quiet ? null : draft.EntryId);
        var completed = await f.Runs.CommitOutcomeAsync(outcome, 2, Owner, run.AgentRunId, command, quiet ? draft.EntryId : null);
        await f.ReopenAsync();
        Assert.Equal(AgentRunStatus.Completed, (await f.Runs.GetAsync(Owner, run.AgentRunId))!.Status);
        Assert.Equal(quiet ? AgentRunOutcomeKind.NoAction : AgentRunOutcomeKind.Response, completed.Result!.OutcomeKind);
        Assert.Equal(3, (await f.Memory.LoadAsync(snapshot.SessionId))!.Revision);
        if (quiet) Assert.DoesNotContain((await f.Memory.LoadAsync(snapshot.SessionId))!.Entries, entry => entry.Role == ConversationRole.Assistant);
        else Assert.Equal(draft.EntryId, Assert.Single((await f.Memory.LoadAsync(snapshot.SessionId))!.Entries, entry => entry.Role == ConversationRole.Assistant).EntryId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_outcome_commit_rolls_back_session_and_preserves_cancellation(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var (snapshot, run) = UserTurn();
        await f.Runs.AdmitAsync(snapshot, 0, run);
        run = await Claim(f, run);
        await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.RequestCancellation(run.Revision, Now, null));
        var proposed = snapshot with { Revision = 2, Title = "Unauthorized stale completion" };
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.CommitOutcomeAsync(proposed, 1,
            Owner, run.AgentRunId, new AgentRunCommand.Complete(run.Revision, Now, run.Claim!.Generation, "", AgentRunOutcomeKind.NoAction, null), null).AsTask())).Code);
        Assert.Equal(snapshot.Title, (await f.Memory.LoadAsync(snapshot.SessionId))!.Title);
        Assert.Equal(1, (await f.Memory.LoadAsync(snapshot.SessionId))!.Revision);
        Assert.True((await f.Runs.GetAsync(Owner, run.AgentRunId))!.CancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_input_intent_survives_reopen_and_is_consumed_atomically_by_one_batch(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var (snapshot, run) = UserTurn();
        snapshot = snapshot with { PendingAgentInputIds = snapshot.Entries.Select(entry => entry.EntryId).ToArray() };
        await f.Memory.SaveAsync(snapshot, 0);
        await f.ReopenAsync();
        Assert.Equal(snapshot.PendingAgentInputIds, (await f.Memory.LoadAsync(snapshot.SessionId))!.PendingAgentInputIds);
        Assert.Equal([snapshot.SessionId], await f.Runs.ListPendingInputSessionsAsync(8));
        var committed = snapshot with { Revision = 2 };
        await f.Runs.AdmitAsync(committed, 1, run);
        await f.ReopenAsync();
        Assert.Empty((await f.Memory.LoadAsync(snapshot.SessionId))!.PendingAgentInputIds);
        Assert.Empty(await f.Runs.ListPendingInputSessionsAsync(8));
        var stored = Assert.Single(await f.Runs.ListForSessionAsync(Owner, snapshot.SessionId));
        Assert.Equal(snapshot.PendingAgentInputIds, stored.Admission.Activation.SourceEntryIds);
        Assert.Equal(run.ResponseId, stored.ResponseId);
        Assert.False((await f.Runs.AdmitAsync(committed, 1, run)).Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_input_repair_is_bounded_and_excludes_semantic_pause_archive_and_active_execution(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var (snapshot, run) = UserTurn();
        snapshot = snapshot with { PendingAgentInputIds = snapshot.Entries.Select(entry => entry.EntryId).ToArray(),
            Status = SessionStatus.Paused, PauseReason = "manual", LifecycleStatus = SessionLifecycleStatus.Paused };
        await f.Memory.SaveAsync(snapshot, 0);
        Assert.Empty(await f.Runs.ListPendingInputSessionsAsync(1));
        snapshot = snapshot with { Revision = 2, PauseReason = "recovered" };
        await f.Memory.SaveAsync(snapshot, 1);
        Assert.Equal([snapshot.SessionId], await f.Runs.ListPendingInputSessionsAsync(1));
        snapshot = snapshot with { Revision = 3, ArchivedAt = Now };
        await f.Memory.SaveAsync(snapshot, 2);
        Assert.Empty(await f.Runs.ListPendingInputSessionsAsync(1));
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.ListPendingInputSessionsAsync(0).AsTask())).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Background_intake_preserves_occurrence_input_and_pins_and_each_recurrence_has_its_own_session(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var (intake, instances, definition) = await Intake(f);
        var automation = await f.Triggers.CreateAsync(new Automation(Guid.NewGuid(),
            new TriggerOwner(Owner.AgentInstanceId, Owner.ProfileId), AutomationStatus.Active,
            "Review the inventory", new FixedIntervalSchedule(60, Now), Now.AddMinutes(1), null, 0, 1, 1,
            new TriggerProvenance(TriggerAuthorizationOrigin.AdminOwner, null, null, Now, Now), null,
            name: "Inventory review"));
        var first = await AwaitingOccurrence(f, automation);
        Assert.Equal(1, (await intake.AcceptAwaitingAsync()).Accepted);
        Assert.Equal(0, (await intake.AcceptAwaitingAsync()).Accepted);
        var receipt = (await f.Triggers.GetOccurrenceAsync(automation.Owner, first.OccurrenceId))!;
        var session = (await f.Memory.LoadAsync(receipt.BackgroundSessionId!.Value))!;
        var run = (await f.Runs.GetAsync(Owner, receipt.AcceptedAgentRunId!.Value))!;
        Assert.Equal(SessionSurface.BackgroundWork, session.Surfaces);
        Assert.Equal(SessionOriginKind.AutomationOccurrence, session.Origin.Kind);
        Assert.Equal(automation.AutomationId, session.Origin.AutomationId);
        Assert.Equal("Inventory review", session.Title);
        Assert.True(session.WorkspaceOwned);
        Assert.Contains("Review the inventory", Assert.Single(session.Entries).Text);
        Assert.Contains(first.EvidenceJson, session.Entries[0].Text);
        Assert.Equal(session.Entries[0].EntryId, Assert.Single(run.Admission.Activation.SourceEntryIds));
        Assert.Equal(ActivationKind.ScheduledWork, run.Admission.Activation.Kind);
        Assert.Equal("synthetic", run.PinnedModel.CatalogKey);
        Assert.Equal(definition.Identity, run.PinnedPersona);
        Assert.Equal("definition:review", Assert.Single(run.ActiveSkillKeys));
        Assert.Equal("Review carefully", Assert.Single(run.PinnedSkillCatalog).Procedure);
        var second = await AwaitingOccurrence(f, automation);
        Assert.Equal(1, (await intake.AcceptAwaitingAsync()).Accepted);
        var secondReceipt = (await f.Triggers.GetOccurrenceAsync(automation.Owner, second.OccurrenceId))!;
        Assert.NotEqual(receipt.BackgroundSessionId, secondReceipt.BackgroundSessionId);
        await f.ReopenAsync();
        Assert.Equal(first.EvidenceJson, (await f.Triggers.GetOccurrenceAsync(automation.Owner, first.OccurrenceId))!.EvidenceJson);
        Assert.Equal(run.AgentRunId, (await f.Runs.GetAsync(Owner, run.AgentRunId))!.AgentRunId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Background_intake_rechecks_current_instance_policy_and_cancelled_automation(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var (intake, instances, _) = await Intake(f);
        var automation = await f.Triggers.CreateAsync(new Automation(Guid.NewGuid(),
            new TriggerOwner(Owner.AgentInstanceId, Owner.ProfileId), AutomationStatus.Active,
            "Review the inventory", new FixedIntervalSchedule(60, Now), Now.AddMinutes(1), null, 0, 1, 1,
            new TriggerProvenance(TriggerAuthorizationOrigin.AdminOwner, null, null, Now, Now), null));
        var awaiting = await AwaitingOccurrence(f, automation);
        await f.Triggers.CancelAsync(automation.Owner, automation.AutomationId, automation.Revision, Now);
        Assert.Equal(1, (await intake.AcceptAwaitingAsync()).Skipped);
        Assert.Null((await f.Triggers.GetOccurrenceAsync(automation.Owner, awaiting.OccurrenceId))!.AcceptedAgentRunId);
        var native = await AwaitingOccurrence(f, automation: null);
        var current = (await instances.FindAsync(Owner.AgentInstanceId))!;
        await instances.UpdateWithExpectedRevisionAsync(new AgentInstanceRevisionUpdate(current.InstanceId,
            current.Revision, Lifecycle: AgentInstanceLifecycle.Archived), Now);
        Assert.Equal(2, (await intake.AcceptAwaitingAsync()).Skipped);
        Assert.Empty(await f.Runs.ListRunnableAsync(Now, 10));
        Assert.Null((await f.Triggers.GetOccurrenceAsync(native.Owner, native.OccurrenceId))!.BackgroundSessionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_detached_occurrence_has_atomic_receipt_without_an_automation(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var (intake, _, _) = await Intake(f);
        var occurrence = await AwaitingOccurrence(f, automation: null);
        Assert.Equal(1, (await intake.AcceptAwaitingAsync()).Accepted);
        var receipt = (await f.Triggers.GetOccurrenceAsync(occurrence.Owner, occurrence.OccurrenceId))!;
        var session = (await f.Memory.LoadAsync(receipt.BackgroundSessionId!.Value))!;
        Assert.Equal(SessionOriginKind.SourceOccurrence, session.Origin.Kind);
        Assert.Equal(occurrence.OccurrenceId, session.Origin.TriggerOccurrenceId);
        Assert.Null(session.Origin.AutomationId);
        var run = (await f.Runs.GetAsync(Owner, receipt.AcceptedAgentRunId!.Value))!;
        Assert.Equal(occurrence.OccurrenceId, run.Admission.Activation.TriggerOccurrenceId);
        Assert.Equal(ActivationKind.ApplicationEvent, run.Admission.Activation.Kind);
    }

    private static async Task<(BackgroundOccurrenceIntake Intake, InMemoryAgentInstanceStore Instances,
        AgentDefinition Definition)> Intake(Fixture f)
    {
        var definition = Definition with
        {
            TriggerPolicy = new TriggerPolicy(true, true, true, true, true, true, 10, 30, 1,
                ["schedule", "applicationEvent"], true, 60),
            Skills = [new SkillSpec("review", "Review", "Review the inventory", "Review carefully",
                SkillProjection.Always, true, ["chat.respond"], [])]
        };
        var instances = new InMemoryAgentInstanceStore();
        await instances.InsertAsync(new AgentInstance(Owner.AgentInstanceId, definition.Id, definition.Version,
            definition.Identity, AgentInstanceLifecycle.Active, Now, Now), initialSkills: definition.SkillList);
        await f.Memory.SaveProfileAsync(new UserProfile(Owner.ProfileId, 1,
            new Dictionary<string, UserProfileValue>(), Now), 0);
        var models = new ConfigurationModelCatalog("synthetic",
            [new ModelDescriptor("synthetic", "Synthetic", "synthetic", "synthetic", true, false, false,
                false, [], null)]);
        return (new BackgroundOccurrenceIntake(f.Triggers, f.Runs, instances, new Definitions(definition),
            f.Memory, models, new SystemIdGenerator(TimeProvider.System), new FakeTimeProvider(Now)), instances, definition);
    }

    private sealed class Definitions(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);
        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken ct = default) =>
            ValueTask.FromResult(id == definition.Id && (version is null || version == definition.Version) ? definition : null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Occurrence_replay_commits_one_child_and_receipt_and_reopen_returns_the_original(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var occurrence = await AwaitingOccurrence(f);
        var original = OccurrenceTurn(occurrence);
        var first = await f.Runs.AdmitOccurrenceAsync(original.Snapshot, original.Run, occurrence.RoutingRevision);
        Assert.True(first.Created);
        var candidates = Enumerable.Range(0, 8).Select(_ => OccurrenceTurn(occurrence)).ToArray();
        var replays = await Task.WhenAll(candidates.Select(item => f.Runs.AdmitOccurrenceAsync(item.Snapshot,
            item.Run, occurrence.RoutingRevision).AsTask()));
        Assert.All(replays, result => { Assert.False(result.Created); Assert.Equal(first.Run.AgentRunId, result.Run.AgentRunId); });
        foreach (var candidate in candidates) Assert.Null(await f.Memory.LoadAsync(candidate.Snapshot.SessionId));
        var accepted = (await f.Triggers.GetOccurrenceAsync(occurrence.Owner, occurrence.OccurrenceId))!;
        Assert.Equal(OccurrenceRoutingDisposition.AcceptedDurable, accepted.Disposition);
        Assert.Equal(first.Run.SessionId, accepted.BackgroundSessionId);
        Assert.Equal(first.Run.AgentRunId, accepted.AcceptedAgentRunId);
        Assert.Null(accepted.ClaimId);
        await f.ReopenAsync();
        var replay = OccurrenceTurn(occurrence);
        var restored = await f.Runs.AdmitOccurrenceAsync(replay.Snapshot, replay.Run, occurrence.RoutingRevision);
        Assert.Equal(first.Run.AgentRunId, restored.Run.AgentRunId);
        Assert.Equal(first.Run.SessionId, restored.Run.SessionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Occurrence_collision_or_stale_revision_rolls_back_child_input_activation_and_receipt(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var existing = UserTurn();
        await f.Runs.AdmitAsync(existing.Snapshot, 0, existing.Run);
        var occurrence = await AwaitingOccurrence(f);
        var proposed = OccurrenceTurn(occurrence);
        var collided = AgentRun.Create(existing.Run.AgentRunId, Owner, proposed.Run.Admission,
            proposed.Run.PinnedModel, 3, Now);
        var collidedSnapshot = proposed.Snapshot with { Origin = new SessionOrigin(SessionOriginKind.AutomationOccurrence,
            initialBackgroundAgentRunId: collided.AgentRunId, automationId: occurrence.AutomationId,
            triggerOccurrenceId: occurrence.OccurrenceId) };
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitOccurrenceAsync(collidedSnapshot,
            collided, occurrence.RoutingRevision).AsTask());
        Assert.Null(await f.Memory.LoadAsync(proposed.Snapshot.SessionId));
        Assert.Null(await f.Runs.GetActivationAsync(Owner, proposed.Run.ActivationId));
        var untouched = (await f.Triggers.GetOccurrenceAsync(occurrence.Owner, occurrence.OccurrenceId))!;
        Assert.Equal(occurrence.Disposition, untouched.Disposition);
        Assert.Equal(occurrence.RoutingRevision, untouched.RoutingRevision);
        Assert.Null(untouched.AcceptedAgentRunId);
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitOccurrenceAsync(
            proposed.Snapshot, proposed.Run, occurrence.RoutingRevision - 1).AsTask())).Code);
        Assert.Null(await f.Memory.LoadAsync(proposed.Snapshot.SessionId));
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitAsync(
            proposed.Snapshot, 0, proposed.Run).AsTask())).Code);
        Assert.True((await f.Runs.AdmitOccurrenceAsync(proposed.Snapshot, proposed.Run, occurrence.RoutingRevision)).Created);
    }

    private static Task<TriggerOccurrence> AwaitingOccurrence(Fixture f) => AwaitingOccurrence(f, null, true);

    private static async Task<TriggerOccurrence> AwaitingOccurrence(Fixture f, Automation? automation, bool rawAutomation = false)
    {
        var occurrence = new TriggerOccurrence(Guid.NewGuid(), $"scheduled:{Guid.NewGuid():D}",
            automation?.AutomationId ?? (rawAutomation ? Guid.NewGuid() : null),
            new TriggerOwner(Owner.AgentInstanceId, Owner.ProfileId), automation is not null || rawAutomation
                ? TriggerSourceKind.Schedule : TriggerSourceKind.ApplicationEvent,
            Now, Now, Now, automation is null ? "{}" : AgentCore.Application.Triggers.AutomationRules.Evidence(automation),
            null, 1, OccurrenceRoutingDisposition.Pending, null, 0, null,
            null, null, modelPin: new ExecutionModelPin("synthetic", "synthetic", "synthetic", null,
                ExecutionModelSource.ConversationDefault));
        await f.Triggers.AdmitOccurrenceAsync(occurrence);
        var claim = Guid.NewGuid();
        await f.Triggers.TryClaimOccurrenceAsync(occurrence.OccurrenceId, claim, Now.AddMinutes(1), Now);
        return await f.Triggers.MarkAwaitingDurableWorkAsync(occurrence.OccurrenceId, claim, "Background admission", Now)
            ?? throw new InvalidOperationException("Occurrence routing failed.");
    }

    private static (SessionSnapshot Snapshot, AgentRun Run) OccurrenceTurn(TriggerOccurrence occurrence)
    {
        var entry = new ConversationEntry(Guid.NewGuid(), 1, occurrence.SourceEventId, ConversationRole.User,
            "Check this occurrence", null, EntryStatus.Completed, SessionMode.Text, 0, 21, Now);
        var session = Snapshot(Guid.NewGuid(), [entry]);
        var activation = new Activation(Guid.NewGuid(), session.SessionId, ActivationKind.ScheduledWork,
            [entry.EntryId], occurrence.SourceEventId, occurrence.OccurrenceId, null, null, occurrence.DedupeKey, Now);
        var run = NewRun(activation);
        var origin = new SessionOrigin(SessionOriginKind.AutomationOccurrence, initialBackgroundAgentRunId: run.AgentRunId,
            automationId: occurrence.AutomationId, triggerOccurrenceId: occurrence.OccurrenceId);
        return (session with { Origin = origin, Surfaces = origin.InitialSurface }, run);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batched_inputs_commit_with_exactly_one_activation_and_run_and_replay_is_safe(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var (snapshot, run) = UserTurn();
        var admitted = await f.Runs.AdmitAsync(snapshot, 0, run);
        Assert.True(admitted.Created);
        var replay = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.Runs.AdmitAsync(snapshot, 0, run).AsTask()));
        Assert.All(replay, item => { Assert.False(item.Created); Assert.Equal(run.AgentRunId, item.Run.AgentRunId); });
        var loaded = await f.Memory.LoadAsync(snapshot.SessionId);
        Assert.Equal(snapshot.Entries.Select(entry => entry.Text), loaded!.Entries.Select(entry => entry.Text));
        var activation = await f.Runs.GetActivationAsync(Owner, run.ActivationId);
        Assert.Equal(snapshot.Entries.Select(entry => entry.EntryId), activation!.SourceEntryIds);
        Assert.Single(await f.Runs.ListForSessionAsync(Owner, snapshot.SessionId));
        var changed = snapshot with { Entries = [snapshot.Entries[0] with { Text = "Different task" }, snapshot.Entries[1]] };
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitAsync(changed, 0, run).AsTask())).Code);
        Assert.Equal("Check A", (await f.Memory.LoadAsync(snapshot.SessionId))!.Entries[0].Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Background_receipt_replay_with_fresh_candidate_ids_cannot_create_an_orphan_child(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var parent = await ParentTurn(f);
        var parentSessionId = parent.SessionId;
        var parentRunId = parent.AgentRunId;
        var first = BackgroundTurn(parentSessionId, parentRunId);
        await f.Runs.AdmitImmediateAsync(first.Snapshot, first.Run, parent.Claim!.Generation);
        var candidates = Enumerable.Range(0, 6).Select(_ => BackgroundTurn(parentSessionId, parentRunId)).ToArray();
        var results = await Task.WhenAll(candidates.Select(candidate => f.Runs.AdmitImmediateAsync(candidate.Snapshot, candidate.Run, parent.Claim!.Generation).AsTask()));
        Assert.All(results, result => { Assert.False(result.Created); Assert.Equal(first.Run.AgentRunId, result.Run.AgentRunId); });
        foreach (var candidate in candidates) Assert.Null(await f.Memory.LoadAsync(candidate.Snapshot.SessionId));
        var child = await f.Memory.LoadAsync(first.Snapshot.SessionId);
        Assert.Equal(SessionOriginKind.ImmediateBackground, child!.Origin.Kind);
        Assert.Equal(first.Run.AgentRunId, child.Origin.InitialBackgroundAgentRunId);
        Assert.Equal(SessionSurface.BackgroundWork, child.Surfaces);
        var changed = BackgroundTurn(parentSessionId, parentRunId, "Different objective");
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitImmediateAsync(changed.Snapshot, changed.Run, parent.Claim!.Generation).AsTask())).Code);
        Assert.Null(await f.Memory.LoadAsync(changed.Snapshot.SessionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Admission_failure_rolls_back_input_and_both_execution_records(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var first = UserTurn();
        await f.Runs.AdmitAsync(first.Snapshot, 0, first.Run);
        var other = UserTurn();
        var collision = AgentRun.Create(first.Run.AgentRunId, Owner, other.Run.Admission,
            other.Run.PinnedModel, 3, Now);
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitAsync(other.Snapshot, 0, collision).AsTask())).Code);
        Assert.Null(await f.Memory.LoadAsync(other.Snapshot.SessionId));
        Assert.Null(await f.Runs.GetActivationAsync(Owner, other.Run.ActivationId));
        Assert.Single(await f.Runs.ListForSessionAsync(Owner, first.Snapshot.SessionId));
        var invalid = other.Snapshot with { Entries = [] };
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitAsync(invalid, 0, other.Run).AsTask())).Code);
        Assert.Null(await f.Memory.LoadAsync(other.Snapshot.SessionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_snapshot_rejects_a_new_admission_without_advancing_session_state(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var first = UserTurn();
        await f.Runs.AdmitAsync(first.Snapshot, 0, first.Run);
        var newer = first.Snapshot with { Revision = 2, Title = "New title", UpdatedAt = Now.AddSeconds(1) };
        await f.Memory.SaveAsync(newer, 1);
        var next = UserTurn(first.Snapshot.SessionId);
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitAsync(next.Snapshot, 0, next.Run).AsTask())).Code);
        Assert.Null(await f.Runs.GetActivationAsync(Owner, next.Run.ActivationId));
        Assert.Equal(2, (await f.Memory.LoadAsync(first.Snapshot.SessionId))!.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_isolation_covers_activation_run_queries_and_commands(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var turn = UserTurn();
        await f.Runs.AdmitAsync(turn.Snapshot, 0, turn.Run);
        foreach (var foreign in new[] { new AgentRunOwner(Guid.NewGuid(), Owner.ProfileId), new AgentRunOwner(Owner.AgentInstanceId, Guid.NewGuid()) })
        {
            Assert.Null(await f.Runs.GetAsync(foreign, turn.Run.AgentRunId));
            Assert.Null(await f.Runs.GetActivationAsync(foreign, turn.Run.ActivationId));
            Assert.Empty(await f.Runs.ListForSessionAsync(foreign, turn.Run.SessionId));
            Assert.Equal("NotFound", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.ApplyAsync(foreign, turn.Run.AgentRunId,
                new AgentRunCommand.Claim(1, Now, Guid.NewGuid(), Now.AddMinutes(1))).AsTask())).Code);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_claims_have_one_winner_and_expired_worker_cannot_commit_an_effect(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var turn = UserTurn();
        await f.Runs.AdmitAsync(turn.Snapshot, 0, turn.Run);
        async Task<AgentRun?> TryClaim()
        {
            try { return await f.Runs.ApplyAsync(Owner, turn.Run.AgentRunId, new AgentRunCommand.Claim(1, Now, Guid.NewGuid(), Now.AddMinutes(1))); }
            catch (AgentCoreException e) when (e.Code == "Conflict") { return null; }
        }
        var claims = await Task.WhenAll(TryClaim(), TryClaim());
        var claimed = Assert.Single(claims, run => run is not null)!;
        Assert.Equal(1, claimed.AttemptCount);
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.ApplyAsync(Owner, claimed.AgentRunId,
            new AgentRunCommand.MarkSideEffect(claimed.Revision, Now.AddMinutes(1), claimed.Claim!.Generation,
                AgentRunSideEffectDisposition.Prepared, "email-1", Hash)).AsTask())).Code);
        var recovered = await f.Runs.ApplyAsync(Owner, claimed.AgentRunId, new AgentRunCommand.Recover(claimed.Revision, Now.AddMinutes(1)));
        var resumed = await f.Runs.ApplyAsync(Owner, recovered.AgentRunId,
            new AgentRunCommand.Claim(recovered.Revision, Now.AddMinutes(1), Guid.NewGuid(), Now.AddMinutes(2)));
        Assert.Equal(claimed.AgentRunId, resumed.AgentRunId);
        Assert.Equal(claimed.ActivationId, resumed.ActivationId);
        Assert.Equal(claimed.ResponseId, resumed.ResponseId);
        Assert.Equal(2, resumed.AttemptCount);
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.ApplyAsync(Owner, resumed.AgentRunId,
            new AgentRunCommand.Checkpoint(resumed.Revision, Now.AddMinutes(1), claimed.Claim!.Generation,
                new AgentRunCheckpoint("{}", 0, 0, 1000), null)).AsTask())).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_requires_persisted_matching_response_then_reopen_preserves_the_outcome(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var turn = UserTurn();
        await f.Runs.AdmitAsync(turn.Snapshot, 0, turn.Run);
        var run = await Claim(f, turn.Run);
        var response = new ConversationEntry(Guid.NewGuid(), 3, null, ConversationRole.Assistant,
            "Checked A and B", run.ResponseId, EntryStatus.Completed, SessionMode.Text, 0, 15, Now.AddSeconds(1));
        var command = new AgentRunCommand.Complete(run.Revision, Now.AddSeconds(1), run.Claim!.Generation,
            "Finished", AgentRunOutcomeKind.Response, response.EntryId);
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.ApplyAsync(Owner, run.AgentRunId, command).AsTask())).Code);
        await f.Memory.SaveAsync(turn.Snapshot with { Revision = 2, Entries = [.. turn.Snapshot.Entries, response], UpdatedAt = Now.AddSeconds(1) }, 1);
        var completed = await f.Runs.ApplyAsync(Owner, run.AgentRunId, command);
        Assert.Equal(AgentRunStatus.Completed, completed.Status);
        Assert.Equal(response.EntryId, completed.OutcomeEntryId);
        await f.ReopenAsync();
        var restored = await f.Runs.GetAsync(Owner, completed.AgentRunId);
        Assert.Equal(completed.OutcomeEntryId, restored!.OutcomeEntryId);
        Assert.Equal(completed.ActivationId, restored.ActivationId);
        Assert.Equal("Checked A and B", (await f.Memory.LoadAsync(completed.SessionId))!.Entries.Last().Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Quiet_background_completion_and_foregrounding_preserve_same_session_for_a_new_turn(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var parent = await ParentTurn(f);
        var task = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        await f.Runs.AdmitImmediateAsync(task.Snapshot, task.Run, parent.Claim!.Generation);
        var run = await Claim(f, task.Run);
        await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Complete(run.Revision,
            Now.AddSeconds(1), run.Claim!.Generation, "", AgentRunOutcomeKind.NoAction, null));
        var child = (await f.Memory.LoadAsync(task.Snapshot.SessionId))!;
        Assert.Equal(SessionLifecycleStatus.Active, child.LifecycleStatus);
        Assert.Single(child.Entries);
        var foreground = child with { Revision = 2, Surfaces = SessionOrigin.ContinueInChat(child.Surfaces), UpdatedAt = Now.AddSeconds(2) };
        await f.Memory.SaveAsync(foreground, 1);
        await f.ReopenAsync();
        var reopened = (await f.Memory.LoadAsync(child.SessionId))!;
        Assert.Equal(child.Origin, reopened.Origin);
        Assert.Equal(SessionSurface.BackgroundWork | SessionSurface.ChatList, reopened.Surfaces);
        var user = new ConversationEntry(Guid.NewGuid(), 2, null, ConversationRole.User, "Check B too", null,
            EntryStatus.Completed, SessionMode.Text, 0, 11, Now.AddSeconds(3));
        var nextActivation = new Activation(Guid.NewGuid(), child.SessionId, ActivationKind.UserTurn, [user.EntryId],
            null, null, null, null, "user:next", Now.AddSeconds(3));
        var nextRun = AgentRun.Create(Guid.NewGuid(), Owner,
            new AgentRunAdmission(nextActivation, Definition.Id, Definition.Version, Definition.Identity, Guid.NewGuid()),
            run.PinnedModel, 3, Now.AddSeconds(3));
        await f.Runs.AdmitAsync(reopened with { Revision = 3, Entries = [.. reopened.Entries, user], UpdatedAt = Now.AddSeconds(3) }, 2, nextRun);
        Assert.Equal(2, (await f.Runs.ListForSessionAsync(Owner, child.SessionId)).Count);
        Assert.Equal(child.SessionId, nextRun.SessionId);
        Assert.NotEqual(run.ActivationId, nextRun.ActivationId);
        Assert.False(child.Origin.MayReportCompletion(nextRun.AgentRunId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_approval_decision_resumes_same_attempt_and_uncertain_effect_survives_restart(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var turn = UserTurn();
        await f.Runs.AdmitAsync(turn.Snapshot, 0, turn.Run);
        var run = await Claim(f, turn.Run);
        run = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.MarkSideEffect(run.Revision,
            Now.AddSeconds(1), run.Claim!.Generation, AgentRunSideEffectDisposition.Prepared, "email-1", Hash));
        run = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.BeginApproval(run.Revision,
            Now.AddSeconds(2), run.Claim!.Generation, Guid.NewGuid(), "email.send", "{}", Hash, "Send note", Now.AddMinutes(10)));
        await f.ReopenAsync();
        var waiting = (await f.Runs.GetAsync(Owner, run.AgentRunId))!;
        Assert.Null(waiting.Claim);
        Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.ApplyAsync(Owner, waiting.AgentRunId,
            new AgentRunCommand.DecideApproval(waiting.Revision, Now.AddSeconds(3), waiting.Approval!.ApprovalId,
                waiting.Approval.Revision, new string('f', 64), AgentRunApprovalDecision.Approved)).AsTask())).Code);
        run = await f.Runs.ApplyAsync(Owner, waiting.AgentRunId, new AgentRunCommand.DecideApproval(waiting.Revision,
            Now.AddSeconds(3), waiting.Approval!.ApprovalId, waiting.Approval.Revision, Hash, AgentRunApprovalDecision.Approved));
        run = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, Now.AddSeconds(3), Guid.NewGuid(), Now.AddMinutes(1)));
        Assert.Equal(1, run.AttemptCount);
        run = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.MarkSideEffect(run.Revision,
            Now.AddSeconds(4), run.Claim!.Generation, AgentRunSideEffectDisposition.InFlight, "email-1", Hash));
        await f.ReopenAsync();
        run = (await f.Runs.GetAsync(Owner, run.AgentRunId))!;
        run = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Recover(run.Revision, Now.AddMinutes(1)));
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Equal(AgentRunSideEffectDisposition.Indeterminate, run.SideEffect.Disposition);
        Assert.Equal("side-effect-indeterminate", run.Failure!.Code);
        Assert.Empty(await f.Runs.ListRunnableAsync(Now.AddMinutes(2), 10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_origin_cannot_be_rewritten_as_user_chat(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var parent = await ParentTurn(f);
        var task = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        await f.Runs.AdmitImmediateAsync(task.Snapshot, task.Run, parent.Claim!.Generation);
        var changed = task.Snapshot with { Revision = 2, Origin = SessionOrigin.UserChat, UpdatedAt = Now.AddSeconds(1) };
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Memory.SaveAsync(changed, 1).AsTask())).Code);
        Assert.Equal(task.Snapshot.Origin, (await f.Memory.LoadAsync(task.Snapshot.SessionId))!.Origin);
    }

    private static async Task<AgentRun> ParentTurn(Fixture f)
    {
        var turn = UserTurn();
        await f.Runs.AdmitAsync(turn.Snapshot, 0, turn.Run);
        return await Claim(f, turn.Run);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Submillisecond_admission_timestamps_survive_the_indexed_storage_precision(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var turn = UserTurn();
        var precise = Now.AddTicks(1234);
        var a = new Activation(Guid.NewGuid(), turn.Snapshot.SessionId, ActivationKind.UserTurn,
            turn.Run.Admission.Activation.SourceEntryIds, null, null, null, null, "precise:batch", precise);
        var run = AgentRun.Create(Guid.NewGuid(), Owner,
            new AgentRunAdmission(a, Definition.Id, Definition.Version, Definition.Identity, Guid.NewGuid()),
            turn.Run.PinnedModel, 3, precise);
        await f.Runs.AdmitAsync(turn.Snapshot with { CreatedAt = precise, UpdatedAt = precise }, 0, run);
        await f.ReopenAsync();
        var restored = await f.Runs.GetAsync(Owner, run.AgentRunId);
        Assert.NotNull(restored);
        Assert.Equal(a.ActivationId, restored!.ActivationId);
        Assert.Equal(precise.ToUnixTimeMilliseconds(), restored.CreatedAtUtc.ToUnixTimeMilliseconds());
        Assert.Equal(precise, restored.Admission.Activation.AdmittedAtUtc);
    }

    [Fact]
    public async Task Complete_current_ensurecreated_schema_is_stamped_and_reopened()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-run-current-schema-{Guid.NewGuid():N}.db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
            var memory = new SqliteMemoryStore(factory, new FakeTimeProvider(Now));
            await memory.EnsureCreatedAsync();
            var turn = UserTurn();
            var runs = new SqliteAgentRunStore(factory, memory, new Diagnostics());
            await runs.AdmitAsync(turn.Snapshot, 0, turn.Run);
            await memory.EnsureCreatedAsync();
            Assert.NotNull(await runs.GetAsync(Owner, turn.Run.AgentRunId));
            await using var verify = factory.CreateDbContext();
            Assert.Contains("20261008013446_ActivationAgentRunFoundation", await verify.Database.GetAppliedMigrationsAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public async Task Incomplete_occurrence_receipt_schema_cannot_be_stamped_as_current_admission()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-run-incomplete-receipt-{Guid.NewGuid():N}.db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await db.Database.ExecuteSqlRawAsync("DROP INDEX IX_TriggerOccurrences_AcceptedAgentRunId;");
            }
            var memory = new SqliteMemoryStore(factory, new FakeTimeProvider(Now));
            var error = await Assert.ThrowsAsync<AgentCoreException>(() => memory.EnsureCreatedAsync().AsTask());
            Assert.Contains("Canonical index on TriggerOccurrences is missing or incompatible", error.Message, StringComparison.Ordinal);
            await using var verify = factory.CreateDbContext();
            var applied = await verify.Database.GetAppliedMigrationsAsync();
            Assert.DoesNotContain("20261008021141_BackgroundOccurrenceAdmission", applied);
            Assert.DoesNotContain("20261008021317_BackgroundOccurrenceIntegrity", applied);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Fact]
    public async Task Incomplete_current_ensurecreated_schema_cannot_be_stamped_as_complete()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-run-incomplete-schema-{Guid.NewGuid():N}.db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await db.Database.ExecuteSqlRawAsync("DROP INDEX IX_AgentRuns_ActivationId;");
            }
            var memory = new SqliteMemoryStore(factory, new FakeTimeProvider(Now));
            var error = await Assert.ThrowsAsync<AgentCoreException>(() => memory.EnsureCreatedAsync().AsTask());
            Assert.Contains("Canonical index on AgentRuns is missing or incompatible", error.Message, StringComparison.Ordinal);
            await using var verify = factory.CreateDbContext();
            Assert.DoesNotContain("20261008013446_ActivationAgentRunFoundation", await verify.Database.GetAppliedMigrationsAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Same_accepted_entry_cannot_be_admitted_under_a_second_key(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var turn = UserTurn();
        await f.Runs.AdmitAsync(turn.Snapshot, 0, turn.Run);
        var a = new Activation(Guid.NewGuid(), turn.Snapshot.SessionId, ActivationKind.UserTurn,
            turn.Run.Admission.Activation.SourceEntryIds, null, null, null, null, "different:key", Now);
        var duplicate = NewRun(a);
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitAsync(turn.Snapshot, 0, duplicate).AsTask())).Code);
        Assert.Null(await f.Runs.GetActivationAsync(Owner, duplicate.ActivationId));
        Assert.Single(await f.Runs.ListForSessionAsync(Owner, turn.Snapshot.SessionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_foreign_or_cancelled_parent_cannot_admit_a_background_child(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var missing = BackgroundTurn(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitImmediateAsync(missing.Snapshot, missing.Run, Guid.NewGuid()).AsTask())).Code);
        Assert.Null(await f.Memory.LoadAsync(missing.Snapshot.SessionId));
        var parent = await ParentTurn(f);
        var child = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        var foreign = new AgentRunOwner(Guid.NewGuid(), Owner.ProfileId);
        var foreignRun = AgentRun.Create(child.Run.AgentRunId, foreign, child.Run.Admission, child.Run.PinnedModel, 3, Now);
        var foreignSnapshot = child.Snapshot with { AgentInstanceId = foreign.AgentInstanceId };
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitImmediateAsync(foreignSnapshot, foreignRun, parent.Claim!.Generation).AsTask())).Code);
        Assert.Null(await f.Memory.LoadAsync(child.Snapshot.SessionId));
        await f.Runs.ApplyAsync(Owner, parent.AgentRunId, new AgentRunCommand.RequestCancellation(parent.Revision, Now, null));
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, parent.Claim!.Generation).AsTask())).Code);
        Assert.Null(await f.Memory.LoadAsync(child.Snapshot.SessionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_receipt_commits_one_parent_activation_and_replays_after_reopen(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var parent = await ParentTurn(f);
        var child = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        await f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, parent.Claim!.Generation);
        var completed = await CompleteChild(f, child, quiet: false);
        var source = Assert.Single(await f.Runs.ListUnreportedCompletionsAsync(8));
        Assert.Equal(child.Run.AgentRunId, source.Run.AgentRunId);
        var parentSnapshot = (await f.Memory.LoadAsync(parent.SessionId))!;
        AgentRun Report() => NewRun(new Activation(Guid.NewGuid(), parent.SessionId, ActivationKind.BackgroundCompleted, [],
            child.Run.AgentRunId, null, child.Snapshot.SessionId, child.Run.AgentRunId, "completion:one", Now, "{\"summary\":\"Done\"}"));
        var proposed = Report();
        var first = await f.Runs.AdmitCompletionReportAsync(parentSnapshot with { Revision = parentSnapshot.Revision + 1 }, parentSnapshot.Revision, proposed, completed.AgentRunId);
        Assert.True(first.Created);
        await f.ReopenAsync();
        var replay = await f.Runs.AdmitCompletionReportAsync(parentSnapshot with { Revision = parentSnapshot.Revision + 1 }, parentSnapshot.Revision, Report(), completed.AgentRunId);
        Assert.False(replay.Created);
        Assert.Equal(first.Run.AgentRunId, replay.Run.AgentRunId);
        Assert.Equal(2, (await f.Runs.ListForSessionAsync(Owner, parent.SessionId)).Count);
        Assert.True(await f.Runs.HasCompletionReceiptAsync(Owner, child.Run.AgentRunId));
        Assert.False(await f.Runs.HasCompletionReceiptAsync(new(Owner.AgentInstanceId, Guid.NewGuid()), child.Run.AgentRunId));
        Assert.Empty(await f.Runs.ListUnreportedCompletionsAsync(8));
        Assert.DoesNotContain(await f.Runs.ListRunnableAsync(Now, 8), run => run.AgentRunId == first.Run.AgentRunId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Quiet_completion_cannot_admit_parent_prose_and_skip_receipt_survives_reopen(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var parent = await ParentTurn(f);
        var child = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        await f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, parent.Claim!.Generation);
        var completed = await CompleteChild(f, child, quiet: true);
        var parentSnapshot = (await f.Memory.LoadAsync(parent.SessionId))!;
        var report = NewRun(new Activation(Guid.NewGuid(), parent.SessionId, ActivationKind.BackgroundCompleted, [],
            child.Run.AgentRunId, null, child.Snapshot.SessionId, child.Run.AgentRunId, "completion:quiet", Now, "{}"));
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitCompletionReportAsync(
            parentSnapshot with { Revision = parentSnapshot.Revision + 1 }, parentSnapshot.Revision, report, completed.AgentRunId).AsTask())).Code);
        Assert.Equal(parentSnapshot.Revision, (await f.Memory.LoadAsync(parent.SessionId))!.Revision);
        await f.Runs.SkipCompletionReportAsync(Owner, child.Run.AgentRunId, "quiet-outcome", Now);
        await f.ReopenAsync();
        Assert.True(await f.Runs.HasCompletionReceiptAsync(Owner, child.Run.AgentRunId));
        Assert.Empty(await f.Runs.ListUnreportedCompletionsAsync(8));
        Assert.Single(await f.Runs.ListForSessionAsync(Owner, parent.SessionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generic_admission_cannot_bypass_parent_generation_or_completion_receipts(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var parent = await ParentTurn(f);
        var child = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() =>
            f.Runs.AdmitAsync(child.Snapshot, 0, child.Run).AsTask())).Code);
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() =>
            f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, Guid.Empty).AsTask())).Code);
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() =>
            f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, Guid.NewGuid()).AsTask())).Code);
        Assert.Null(await f.Memory.LoadAsync(child.Snapshot.SessionId));
        await f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, parent.Claim!.Generation);
        await CompleteChild(f, child, quiet: false);
        var snapshot = (await f.Memory.LoadAsync(parent.SessionId))!;
        var report = NewRun(new Activation(Guid.NewGuid(), parent.SessionId, ActivationKind.BackgroundCompleted, [],
            child.Run.AgentRunId, null, child.Snapshot.SessionId, child.Run.AgentRunId, "completion:bypass", Now, "{}"));
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() =>
            f.Runs.AdmitAsync(snapshot with { Revision = snapshot.Revision + 1 }, snapshot.Revision, report).AsTask())).Code);
        Assert.False(await f.Runs.HasCompletionReceiptAsync(Owner, child.Run.AgentRunId));
        Assert.Single(await f.Runs.ListForSessionAsync(Owner, parent.SessionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_pagination_is_bounded_stable_after_reopen_and_rejects_foreign_cursors(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var admitted = new List<AgentRun>();
        for (var i = 0; i < 7; i++)
        {
            var turn = UserTurn();
            await f.Runs.AdmitAsync(turn.Snapshot, 0, turn.Run);
            admitted.Add(turn.Run);
        }
        var expected = admitted.OrderByDescending(run => run.AgentRunId.ToString("D"), StringComparer.Ordinal).Select(run => run.AgentRunId).ToArray();
        var found = new List<Guid>(); Guid? cursor = null;
        do
        {
            await f.ReopenAsync();
            var page = await f.Runs.ListPageAsync(Owner, null, cursor, 2);
            Assert.InRange(page.Items.Count, 1, 2);
            found.AddRange(page.Items.Select(run => run.AgentRunId));
            cursor = page.NextCursor;
            Assert.Equal(cursor is not null, page.HasMore);
        } while (cursor is not null);
        Assert.Equal(expected, found);
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.ListPageAsync(
            new(Owner.AgentInstanceId, Guid.NewGuid()), null, admitted[0].AgentRunId, 2).AsTask())).Code);
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.ListPageAsync(
            Owner, admitted[1].SessionId, admitted[0].AgentRunId, 2).AsTask())).Code);
    }

    private static async Task<AgentRun> CompleteChild(Fixture f, (SessionSnapshot Snapshot, AgentRun Run) child, bool quiet)
    {
        var running = await Claim(f, child.Run);
        var result = new ConversationEntry(Guid.NewGuid(), 2, null, ConversationRole.Assistant, "Completed task", running.ResponseId,
            EntryStatus.Completed, SessionMode.Text, 0, 14, Now);
        var snapshot = child.Snapshot with { Revision = 2, Entries = quiet ? child.Snapshot.Entries : child.Snapshot.Entries.Append(result).ToArray() };
        return await f.Runs.CommitOutcomeAsync(snapshot, 1, Owner, running.AgentRunId,
            new AgentRunCommand.Complete(running.Revision, Now, running.Claim!.Generation, "Completed task",
                quiet ? AgentRunOutcomeKind.NoAction : AgentRunOutcomeKind.Response, quiet ? null : result.EntryId), null);
    }

    private static ValueTask<AgentRun> Claim(Fixture f, AgentRun run) => f.Runs.ApplyAsync(Owner, run.AgentRunId,
        new AgentRunCommand.Claim(run.Revision, Now, Guid.NewGuid(), Now.AddMinutes(1)));

    private static (SessionSnapshot Snapshot, AgentRun Run) UserTurn(Guid? sessionId = null)
    {
        var entries = new[] { "Check A", "Check B" }.Select((text, index) => new ConversationEntry(Guid.NewGuid(), index + 1,
            null, ConversationRole.User, text, null, EntryStatus.Completed, SessionMode.Text, 0, text.Length, Now)).ToArray();
        var snapshot = Snapshot(sessionId ?? Guid.NewGuid(), entries);
        var activation = new Activation(Guid.NewGuid(), snapshot.SessionId, ActivationKind.UserTurn,
            entries.Select(entry => entry.EntryId).ToArray(), null, null, null, null, "user:batch", Now);
        return (snapshot, NewRun(activation));
    }

    private static (SessionSnapshot Snapshot, AgentRun Run) BackgroundTurn(Guid parentSessionId, Guid parentRunId, string objective = "Check the site")
    {
        var entry = new ConversationEntry(Guid.NewGuid(), 1, null, ConversationRole.User, objective, null,
            EntryStatus.Completed, SessionMode.Text, 0, objective.Length, Now);
        var sessionId = Guid.NewGuid();
        var activation = new Activation(Guid.NewGuid(), sessionId, ActivationKind.ImmediateBackground, [entry.EntryId],
            null, null, parentSessionId, parentRunId, "tool:background-1", Now);
        var run = NewRun(activation);
        var origin = new SessionOrigin(SessionOriginKind.ImmediateBackground, parentSessionId, parentRunId, run.AgentRunId,
            reportCompletionToOrigin: true);
        return (Snapshot(sessionId, [entry]) with { Origin = origin, Surfaces = origin.InitialSurface }, run);
    }

    private static SessionSnapshot Snapshot(Guid sessionId, IReadOnlyList<ConversationEntry> entries) =>
        new(1, sessionId, 1, Definition, SessionMode.Text, null, SessionStatus.Created, entries, "", 0, null,
            Owner.ProfileId, Now, Now, Owner.AgentInstanceId, PinnedPersona: Definition.Identity);

    private static AgentRun NewRun(Activation activation) => AgentRun.Create(Guid.NewGuid(), Owner,
        new AgentRunAdmission(activation, Definition.Id, Definition.Version, Definition.Identity, Guid.NewGuid()),
        new AgentRunModelPin("synthetic", "synthetic", "synthetic", null), 3, Now);

    private sealed class Diagnostics : IDiagnosticIdSource { public Guid NewId() => Guid.NewGuid(); }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);
        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class Fixture : IAsyncDisposable
    {
        private string? _path;
        private Factory? _factory;
        public IMemoryStore Memory { get; private set; } = null!;
        public IAgentRunStore Runs { get; private set; } = null!;
        public ITriggerStore Triggers { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync(bool sqlite)
        {
            var f = new Fixture();
            if (sqlite)
            {
                f._path = Path.Combine(Path.GetTempPath(), $"agent-run-admission-{Guid.NewGuid():N}.db");
                f._factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={f._path}")
                    .AddInterceptors(new SqlitePragmaInterceptor(5000)).Options);
                await f.ReopenAsync();
            }
            else
            {
                var memory = new InMemoryMemoryStore();
                f.Memory = memory;
                var triggers = new InMemoryTriggerStore();
                f.Triggers = triggers;
                f.Runs = new InMemoryAgentRunStore(memory, new Diagnostics(), triggers);
            }
            return f;
        }
        public async Task ReopenAsync()
        {
            if (_factory is null)
            {
                Runs = new InMemoryAgentRunStore((InMemoryMemoryStore)Memory, new Diagnostics(), (InMemoryTriggerStore)Triggers);
                return;
            }
            var memory = new SqliteMemoryStore(_factory, new FakeTimeProvider(Now));
            await memory.EnsureCreatedAsync();
            Memory = memory;
            Runs = new SqliteAgentRunStore(_factory, memory, new Diagnostics());
            Triggers = new SqliteTriggerStore(_factory);
        }
        public ValueTask DisposeAsync()
        {
            if (_path is not null)
            {
                using var connection = new SqliteConnection($"Data Source={_path}");
                SqliteConnection.ClearPool(connection);
                foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(_path + suffix);
            }
            return ValueTask.CompletedTask;
        }
    }
}
