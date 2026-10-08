using AgentCore.Application.Execution;
using AgentCore.Application.Tools;
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
        var session = (await f.Memory.LoadAsync(receipt.ExecutionSessionId!.Value))!;
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
        Assert.NotEqual(receipt.ExecutionSessionId, secondReceipt.ExecutionSessionId);
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
        Assert.Null((await f.Triggers.GetOccurrenceAsync(native.Owner, native.OccurrenceId))!.ExecutionSessionId);
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
        var session = (await f.Memory.LoadAsync(receipt.ExecutionSessionId!.Value))!;
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
        Assert.Equal(first.Run.SessionId, accepted.ExecutionSessionId);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_target_admission_preserves_transcript_replays_receipt_and_serializes_same_session(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var user = UserTurn();
        await f.Runs.AdmitAsync(user.Snapshot, 0, user.Run);
        var busy = await f.Runs.ApplyAsync(Owner, user.Run.AgentRunId, new AgentRunCommand.Claim(user.Run.Revision, Now, Guid.NewGuid(), Now.AddMinutes(1)));
        var automation = await f.Triggers.CreateAsync(new Automation(Guid.NewGuid(), new(Owner.AgentInstanceId, Owner.ProfileId),
            AutomationStatus.Active, "Say hello", new FixedIntervalSchedule(60, Now), Now.AddMinutes(1), null, 0, 1, 1,
            new(TriggerAuthorizationOrigin.AdminOwner, null, null, Now, Now), null,
            executionTarget: AutomationExecutionTarget.Existing(user.Snapshot.SessionId), completionDelivery: AutomationCompletionDelivery.None));
        var occurrence = await AwaitingOccurrence(f, automation);
        var activation = new Activation(Guid.NewGuid(), user.Snapshot.SessionId, ActivationKind.ScheduledWork, [],
            occurrence.SourceEventId, occurrence.OccurrenceId, null, null, $"automation:{occurrence.OccurrenceId:D}", Now, "{}");
        var first = NewRun(activation);
        var metadata = user.Snapshot with { Entries = [] };
        Assert.True((await f.Runs.AdmitOccurrenceAsync(metadata, first, occurrence.RoutingRevision)).Created);
        Assert.Equal(user.Snapshot.Entries, (await f.Memory.LoadAsync(user.Snapshot.SessionId))!.Entries);
        Assert.Equal(user.Snapshot.Revision, (await f.Memory.LoadAsync(user.Snapshot.SessionId))!.Revision);
        Assert.Empty(await f.Runs.ListRunnableAsync(Now, 8));
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.ApplyAsync(Owner, first.AgentRunId,
            new AgentRunCommand.Claim(first.Revision, Now, Guid.NewGuid(), Now.AddMinutes(1))).AsTask())).Code);
        await f.Runs.ApplyAsync(Owner, busy.AgentRunId, new AgentRunCommand.Fail(busy.Revision, Now, busy.Claim!.Generation, "fixture-ended", "Prior response ended", false, null));
        Assert.Equal(first.AgentRunId, Assert.Single(await f.Runs.ListRunnableAsync(Now, 8)).AgentRunId);
        await f.ReopenAsync();
        var replay = await f.Runs.AdmitOccurrenceAsync(metadata, NewRun(activation), occurrence.RoutingRevision);
        Assert.False(replay.Created); Assert.Equal(first.AgentRunId, replay.Run.AgentRunId);
        Assert.Equal(first.SessionId, (await f.Triggers.GetOccurrenceAsync(automation.Owner, occurrence.OccurrenceId))!.ExecutionSessionId);
        Assert.Empty((await f.Memory.ListBackgroundSessionsAsync(Owner, null, 50, false)).Items);
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
                ExecutionModelSource.ConversationDefault), executionTarget: automation?.ExecutionTarget, completionDelivery: automation?.CompletionDelivery);
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
    public async Task An_expired_active_run_is_recoverable_even_when_an_older_run_is_queued(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var first = UserTurn();
        await f.Runs.AdmitAsync(first.Snapshot, 0, first.Run);
        var input = new ConversationEntry(Guid.NewGuid(), 3, null, ConversationRole.User, "A later turn", null,
            EntryStatus.Completed, SessionMode.Text, 0, 12, Now.AddSeconds(1));
        var activation = new Activation(Guid.NewGuid(), first.Snapshot.SessionId, ActivationKind.UserTurn,
            [input.EntryId], null, null, null, null, "later-turn", Now.AddSeconds(1));
        var later = AgentRun.Create(Guid.NewGuid(), Owner,
            new(activation, Definition.Id, Definition.Version, Definition.Identity, Guid.NewGuid(), AgentRunOutputContract.ConversationResponse),
            first.Run.PinnedModel, 3, Now.AddSeconds(1));
        await f.Runs.AdmitAsync(first.Snapshot with { Revision = 2, Entries = first.Snapshot.Entries.Append(input).ToArray() }, 1, later);
        later = await f.Runs.ApplyAsync(Owner, later.AgentRunId, new AgentRunCommand.Claim(later.Revision,
            Now.AddSeconds(1), Guid.NewGuid(), Now.AddMinutes(1)));
        Assert.Empty(await f.Runs.ListRunnableAsync(Now.AddSeconds(2), 1));
        await f.ReopenAsync();
        Assert.Equal(later.AgentRunId, Assert.Single(await f.Runs.ListRunnableAsync(Now.AddMinutes(2), 1)).AgentRunId);
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
            new AgentRunAdmission(nextActivation, Definition.Id, Definition.Version, Definition.Identity, Guid.NewGuid(), AgentRunOutputContract.ConversationResponse),
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
            new AgentRunAdmission(a, Definition.Id, Definition.Version, Definition.Identity, Guid.NewGuid(), AgentRunOutputContract.ConversationResponse),
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
    public async Task Continued_child_runs_cannot_starve_the_bounded_initial_completion_query(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var parent = await ParentTurn(f);
        var first = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        await f.Runs.AdmitImmediateAsync(first.Snapshot, first.Run, parent.Claim!.Generation);
        await CompleteChild(f, first, quiet: false);
        await f.Runs.SkipCompletionReportAsync(Owner, first.Run.AgentRunId, "parent-unavailable", Now);

        var snapshot = (await f.Memory.LoadAsync(first.Snapshot.SessionId))!;
        var input = new ConversationEntry(Guid.NewGuid(), 3, null, ConversationRole.User, "Continue", null,
            EntryStatus.Completed, SessionMode.Text, 0, 8, Now);
        var continuation = NewRun(new Activation(Guid.NewGuid(), snapshot.SessionId, ActivationKind.UserTurn,
            [input.EntryId], null, null, null, null, "continued-child", Now));
        await f.Runs.AdmitAsync(snapshot with { Revision = snapshot.Revision + 1, Entries = snapshot.Entries.Append(input).ToArray() },
            snapshot.Revision, continuation);
        continuation = await Claim(f, continuation);
        await f.Runs.ApplyAsync(Owner, continuation.AgentRunId, new AgentRunCommand.Fail(continuation.Revision,
            Now.AddSeconds(1), continuation.Claim!.Generation, "fixture-failure", "Continuation failed", false, null));

        var secondParent = await ParentTurn(f);
        var second = BackgroundTurn(secondParent.SessionId, secondParent.AgentRunId, "Another independent task");
        await f.Runs.AdmitImmediateAsync(second.Snapshot, second.Run, secondParent.Claim!.Generation);
        var running = await Claim(f, second.Run);
        await f.Runs.ApplyAsync(Owner, running.AgentRunId, new AgentRunCommand.Fail(running.Revision,
            Now.AddSeconds(2), running.Claim!.Generation, "fixture-failure", "Initial task failed", false, null));
        await f.ReopenAsync();
        Assert.Equal(second.Run.AgentRunId, Assert.Single(await f.Runs.ListUnreportedCompletionsAsync(1)).Run.AgentRunId);
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
        await CompleteParent(f, parent);
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
        Assert.Contains(await f.Runs.ListRunnableAsync(Now, 8), run => run.AgentRunId == first.Run.AgentRunId);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_completion_report_waits_for_active_parent_consumption(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var parent = await ParentTurn(f);
        var child = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        await f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, parent.Claim!.Generation);
        var claimed = await Claim(f, child.Run);
        await f.Runs.ApplyAsync(Owner, claimed.AgentRunId, new AgentRunCommand.Fail(claimed.Revision,
            Now, claimed.Claim!.Generation, "task-failed", "Task failed", false, null));
        var snapshot = (await f.Memory.LoadAsync(parent.SessionId))!;
        var report = NewRun(new Activation(Guid.NewGuid(), parent.SessionId, ActivationKind.BackgroundCompleted,
            [], claimed.AgentRunId, null, child.Run.SessionId, child.Run.AgentRunId,
            $"completion:{child.Run.SessionId:D}:{child.Run.AgentRunId:D}", Now, "{}"));
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitCompletionReportAsync(
            snapshot with { Revision = snapshot.Revision + 1 }, snapshot.Revision, report, child.Run.AgentRunId).AsTask());
        Assert.Equal("Conflict", error.Code);
        Assert.False(await f.Runs.HasCompletionReceiptAsync(Owner, child.Run.AgentRunId));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task Taken_result_acknowledgment_is_provisional_until_atomic_parent_terminal(bool sqlite, bool success)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var parent = await ParentTurn(f);
        var child = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        await f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, parent.Claim!.Generation);
        await CompleteChild(f, child, false);
        await f.ReopenAsync();
        var item = Assert.Single(await f.Runs.ListCompletionInboxAsync(Owner, parent.SessionId, 20, Now));
        Assert.Equal(CompletionInboxStatus.Pending, item.Status);
        Assert.False(await f.Runs.HasCompletionReceiptAsync(Owner, child.Run.AgentRunId));
        Assert.Single(await f.Runs.ListUnreportedCompletionsAsync(20));
        var token = Guid.NewGuid();
        item = await f.Runs.TakeCompletionAsync(Owner, parent.AgentRunId, parent.Claim.Generation, child.Run.AgentRunId, item.Revision, "take-1", token, Now);
        Assert.Empty(await f.Runs.ListUnreportedCompletionsAsync(20));
        var duplicateTake = await f.Runs.TakeCompletionAsync(Owner, parent.AgentRunId, parent.Claim.Generation, child.Run.AgentRunId, item.Revision - 1, "take-1", Guid.NewGuid(), Now);
        Assert.Equal(item.ClaimToken, duplicateTake.ClaimToken); Assert.Equal(item.Revision, duplicateTake.Revision);
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.TakeCompletionAsync(Owner, parent.AgentRunId,
            parent.Claim.Generation, child.Run.AgentRunId, item.Revision, "take-2", Guid.NewGuid(), Now).AsTask())).Code);
        item = await f.Runs.AcknowledgeCompletionAsync(Owner, parent.AgentRunId, parent.Claim.Generation, child.Run.AgentRunId, item.Revision, token,
            "Used the background findings to answer the user.", Now);
        await f.ReopenAsync();
        Assert.Equal(CompletionInboxStatus.Claimed, Assert.Single(await f.Runs.ListCompletionInboxAsync(Owner, parent.SessionId, 20, Now)).Status);
        if (success) await CompleteParent(f, parent);
        else await f.Runs.ApplyAsync(Owner, parent.AgentRunId, new AgentRunCommand.Fail(parent.Revision, Now, parent.Claim.Generation, "model-failed", "Failed before final answer", false, null));
        await f.ReopenAsync();
        var settled = Assert.Single(await f.Runs.ListCompletionInboxAsync(Owner, parent.SessionId, 20, Now));
        Assert.Equal(success ? CompletionInboxStatus.Handled : CompletionInboxStatus.Pending, settled.Status);
        Assert.Equal(success ? parent.AgentRunId : (Guid?)null, settled.HandledByRunId);
        Assert.Equal(success, await f.Runs.HasCompletionReceiptAsync(Owner, child.Run.AgentRunId));
        Assert.Null(settled.ClaimToken);
        Assert.Equal(success ? "handled" : "pending", (await f.Runs.GetCompletionDeliveryAsync(Owner, child.Run.AgentRunId)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_claim_expiry_and_wrong_generation_cannot_acknowledge(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var parent = await ParentTurn(f);
        var child = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        await f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, parent.Claim!.Generation);
        await CompleteChild(f, child, false);
        var item = Assert.Single(await f.Runs.ListCompletionInboxAsync(Owner, parent.SessionId, 20, Now));
        item = await f.Runs.TakeCompletionAsync(Owner, parent.AgentRunId, parent.Claim.Generation, child.Run.AgentRunId, item.Revision, "take", Guid.NewGuid(), Now);
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AcknowledgeCompletionAsync(Owner, parent.AgentRunId, Guid.NewGuid(),
            child.Run.AgentRunId, item.Revision, item.ClaimToken!.Value, "Used", Now).AsTask())).Code);
        await f.ReopenAsync();
        Assert.Equal(CompletionInboxStatus.Claimed, (await f.Runs.GetCompletionInboxAsync(Owner, parent.SessionId, child.Run.AgentRunId, Now.AddMinutes(2)))!.Status);
        await f.Runs.ListDeliveryCandidatesAsync(20, Now.AddMinutes(2));
        Assert.Equal(CompletionInboxStatus.Pending, Assert.Single(await f.Runs.ListCompletionInboxAsync(Owner, parent.SessionId, 20, Now.AddMinutes(2))).Status);
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AcknowledgeCompletionAsync(Owner, parent.AgentRunId, parent.Claim.Generation,
            child.Run.AgentRunId, item.Revision, item.ClaimToken!.Value, "Used", Now.AddMinutes(2)).AsTask())).Code);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Typed_wait_survives_reopen_and_resumes_same_run_once_without_an_attempt(bool sqlite, bool background)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var parent = await ParentTurn(f);
        var child = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        await f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, parent.Claim!.Generation);
        var call = new ModelToolCall("wait-1", "execution.wait", "{}");
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new ModelMessage(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 15000);
        var wait = new AgentRunWait(call.Id, background ? AgentRunWaitMode.Background : AgentRunWaitMode.Duration,
            background ? [child.Run.SessionId] : [], AgentRunWaitUntil.All, Now, Now.AddSeconds(10));
        var suspended = await f.Runs.ApplyAsync(Owner, parent.AgentRunId, new AgentRunCommand.SuspendWait(parent.Revision, Now, parent.Claim.Generation, checkpoint, wait));
        Assert.Equal(AgentRunStatus.WaitingForSignal, suspended.Status);
        Assert.Null(suspended.Claim);
        Assert.DoesNotContain(await f.Runs.ListRunnableAsync(Now.AddSeconds(1), 20), r => r.AgentRunId == parent.AgentRunId);
        await f.ReopenAsync();
        if (background) await CompleteChild(f, child, false);
        var wakeAt = background ? Now.AddSeconds(1) : Now.AddSeconds(10);
        Assert.Contains(await f.Runs.ListRunnableAsync(wakeAt, 20), r => r.AgentRunId == parent.AgentRunId);
        var generation = Guid.NewGuid();
        var resumed = await f.Runs.ApplyAsync(Owner, parent.AgentRunId, new AgentRunCommand.ResumeWait(suspended.Revision, wakeAt, generation, wakeAt.AddMinutes(1)));
        Assert.Equal(parent.AgentRunId, resumed.AgentRunId); Assert.Equal(parent.ActivationId, resumed.ActivationId);
        Assert.Equal(parent.ResponseId, resumed.ResponseId); Assert.Equal(parent.AttemptCount, resumed.AttemptCount);
        Assert.Equal(generation, resumed.Claim!.Generation); Assert.Null(resumed.Wait);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Checkpoint, out var messages));
        var result = Assert.Single(messages!, m => m.Role == ModelRole.Tool);
        Assert.Equal(call.Id, result.ToolCallId);
        Assert.Contains(background ? "condition_met" : "elapsed", result.Text);
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.ApplyAsync(Owner, parent.AgentRunId,
            new AgentRunCommand.ResumeWait(suspended.Revision, wakeAt, Guid.NewGuid(), wakeAt.AddMinutes(1))).AsTask())).Code);
        Assert.Equal(CompletionInboxStatus.Pending, background ? Assert.Single(await f.Runs.ListCompletionInboxAsync(Owner, parent.SessionId, 20, wakeAt)).Status : CompletionInboxStatus.Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Busy_parent_does_not_starve_another_ready_delivery_and_batch_reserves_all_sources(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var busy = await ParentTurn(f);
        var busyChild = BackgroundTurn(busy.SessionId, busy.AgentRunId);
        await f.Runs.AdmitImmediateAsync(busyChild.Snapshot, busyChild.Run, busy.Claim!.Generation);
        await CompleteChild(f, busyChild, false);
        var parent = await ParentTurn(f);
        var first = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        var second = BackgroundTurn(parent.SessionId, parent.AgentRunId, "Second result", "background-2");
        await f.Runs.AdmitImmediateAsync(first.Snapshot, first.Run, parent.Claim!.Generation);
        await f.Runs.AdmitImmediateAsync(second.Snapshot, second.Run, parent.Claim.Generation);
        await CompleteChild(f, first, false); await CompleteChild(f, second, false);
        Assert.Empty(await f.Runs.ListDeliveryCandidatesAsync(1, Now));
        await CompleteParent(f, parent);
        Assert.NotEqual(busyChild.Run.AgentRunId, Assert.Single(await f.Runs.ListDeliveryCandidatesAsync(1, Now)).Run.AgentRunId);
        var snapshot = (await f.Memory.LoadAsync(parent.SessionId))!;
        var report = NewRun(new Activation(Guid.NewGuid(), parent.SessionId, ActivationKind.BackgroundCompleted, [], first.Run.AgentRunId,
            null, first.Run.SessionId, first.Run.AgentRunId, "completion:batch", Now, "{}"));
        var admitted = await f.Runs.AdmitCompletionReportBatchAsync(snapshot with { Revision = snapshot.Revision + 1 }, snapshot.Revision, report, [first.Run.AgentRunId, second.Run.AgentRunId]);
        await f.ReopenAsync();
        var inbox = await f.Runs.ListCompletionInboxAsync(Owner, parent.SessionId, 20, Now);
        Assert.Equal(2, inbox.Count); Assert.All(inbox, i => { Assert.Equal(CompletionInboxStatus.DeliveryQueued, i.Status); Assert.Equal(report.ActivationId, i.ReportActivationId); });
        Assert.Empty(await f.Runs.ListDeliveryCandidatesAsync(20, Now));
        var running = await Claim(f, admitted.Run);
        await CompleteParent(f, running);
        await f.ReopenAsync();
        Assert.All(await f.Runs.ListCompletionInboxAsync(Owner, parent.SessionId, 20, Now), i => Assert.Equal(CompletionInboxStatus.Delivered, i.Status));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Wait_timeout_is_normal_and_cancelled_wait_cannot_be_resurrected(bool sqlite, bool cancel)
    {
        await using var f = await Fixture.CreateAsync(sqlite); var parent = await ParentTurn(f);
        var child = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        await f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, parent.Claim!.Generation);
        var call = new ModelToolCall("wait", "execution.wait", "{}");
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new ModelMessage(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 15000);
        var wait = new AgentRunWait(call.Id, AgentRunWaitMode.Background, [child.Run.SessionId], AgentRunWaitUntil.Any, Now, Now.AddSeconds(1));
        var waiting = await f.Runs.ApplyAsync(Owner, parent.AgentRunId, new AgentRunCommand.SuspendWait(parent.Revision, Now, parent.Claim.Generation, checkpoint, wait));
        if (cancel) waiting = await f.Runs.ApplyAsync(Owner, parent.AgentRunId, new AgentRunCommand.RequestCancellation(waiting.Revision, Now, null));
        await f.ReopenAsync();
        var wake = new AgentRunCommand.ResumeWait(waiting.Revision, Now.AddSeconds(2), Guid.NewGuid(), Now.AddMinutes(1));
        if (cancel)
        {
            Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.ApplyAsync(Owner, parent.AgentRunId, wake).AsTask())).Code);
            Assert.Equal(AgentRunStatus.Cancelled, (await f.Runs.GetAsync(Owner, parent.AgentRunId))!.Status);
        }
        else
        {
            var resumed = await f.Runs.ApplyAsync(Owner, parent.AgentRunId, wake);
            Assert.Equal(parent.AttemptCount, resumed.AttemptCount);
            Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Checkpoint, out var messages));
            Assert.Contains("timeout", Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text);
            Assert.Equal(AgentRunStatus.Queued, (await f.Runs.GetAsync(Owner, child.Run.AgentRunId))!.Status);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_claim_without_successful_acknowledged_answer_releases_after_terminal(bool sqlite, bool cancel)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var parent = await ParentTurn(f); var child = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        await f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, parent.Claim!.Generation);
        await CompleteChild(f, child, false);
        var item = (await f.Runs.GetCompletionInboxAsync(Owner, parent.SessionId, child.Run.AgentRunId, Now))!;
        item = await f.Runs.TakeCompletionAsync(Owner, parent.AgentRunId, parent.Claim.Generation, child.Run.AgentRunId, item.Revision, "take", Guid.NewGuid(), Now);
        Assert.True(await f.Runs.HasCompletionClaimAsync(Owner, parent.AgentRunId, parent.Claim.Generation, Now));
        Assert.False(await f.Runs.HasCompletionClaimAsync(Owner, parent.AgentRunId, Guid.NewGuid(), Now));
        if (cancel)
        {
            item = await f.Runs.AcknowledgeCompletionAsync(Owner, parent.AgentRunId, parent.Claim.Generation, child.Run.AgentRunId, item.Revision, item.ClaimToken!.Value, "Used", Now);
            var cancelling = await f.Runs.ApplyAsync(Owner, parent.AgentRunId, new AgentRunCommand.RequestCancellation(parent.Revision, Now, null));
            await f.Runs.ApplyAsync(Owner, parent.AgentRunId, new AgentRunCommand.CommitCancellation(cancelling.Revision, Now, parent.Claim.Generation, null));
        }
        else await CompleteParent(f, parent);
        await f.ReopenAsync();
        item = (await f.Runs.GetCompletionInboxAsync(Owner, parent.SessionId, child.Run.AgentRunId, Now))!;
        Assert.Equal(CompletionInboxStatus.Pending, item.Status); Assert.Null(item.ClaimToken); Assert.Null(item.HandledByRunId);
        Assert.Null(await f.Runs.GetCompletionInboxAsync(new(Owner.AgentInstanceId, Guid.NewGuid()), parent.SessionId, child.Run.AgentRunId, Now));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completion_cursor_pages_are_stable_and_exact_lookup_reaches_beyond_first_hundred(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite); var parent = await ParentTurn(f);
        var admitted = new List<Guid>();
        for (var index = 0; index < 102; index++)
        {
            if (index > 0 && index % AgentRunLimits.MaxImmediateChildren == 0)
            {
                await CompleteParent(f, parent);
                var snapshot = (await f.Memory.LoadAsync(parent.SessionId))!;
                var entry = new ConversationEntry(Guid.NewGuid(), snapshot.LastEntrySequence + 1, null, ConversationRole.User, "Next evidence batch", null, EntryStatus.Completed, SessionMode.Text, 0, 19, Now);
                var next = NewRun(new Activation(Guid.NewGuid(), parent.SessionId, ActivationKind.UserTurn, [entry.EntryId], null, null, null, null, $"page-parent-{index}", Now));
                await f.Runs.AdmitAsync(snapshot with { Revision = snapshot.Revision + 1, Entries = snapshot.Entries.Append(entry).ToArray(), LastEntrySequence = entry.Sequence }, snapshot.Revision, next);
                parent = await Claim(f, next);
            }
            var child = BackgroundTurn(parent.SessionId, parent.AgentRunId, "Evidence", $"background-{index}");
            await f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, parent.Claim!.Generation);
            await CompleteChild(f, child, false); admitted.Add(child.Run.AgentRunId);
        }
        var found = new List<Guid>(); Guid? cursor = null;
        do
        {
            var page = await f.Runs.ListBackgroundPageAsync(Owner, parent.SessionId, cursor, true, 20);
            found.AddRange(page.Select(c => c.Run.AgentRunId)); cursor = page.Count == 20 ? page[^1].Run.AgentRunId : null;
        } while (cursor is not null);
        Assert.Equal(102, found.Count); Assert.Equal(102, found.Distinct().Count());
        var last = found[^1];
        var item = (await f.Runs.GetCompletionInboxAsync(Owner, parent.SessionId, last, Now))!;
        Assert.Equal(CompletionInboxStatus.Pending, item.Status);
        await f.Runs.TakeCompletionAsync(Owner, parent.AgentRunId, parent.Claim!.Generation, last, item.Revision, "take-last", Guid.NewGuid(), Now);
        Assert.DoesNotContain(await f.Runs.ListBackgroundPageAsync(Owner, parent.SessionId, null, true, 100), c => c.Run.AgentRunId == last);
        Assert.Equal("NotFound", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.ListBackgroundPageAsync(Owner, Guid.NewGuid(), last, false, 20).AsTask())).Code);
    }

    [Fact]
    public async Task Forward_migration_preserves_populated_legacy_completion_receipts_and_session_run_identity()
    {
        var path = Path.Combine(Path.GetTempPath(), $"completion-upgrade-{Guid.NewGuid():N}.db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            var (parentSnapshot, parent) = UserTurn(); var child = BackgroundTurn(parent.SessionId, parent.AgentRunId);
            var claimed = new AgentRunCommand.Claim(child.Run.Revision, Now, Guid.NewGuid(), Now.AddMinutes(1)).Apply(child.Run, Guid.NewGuid);
            var completed = new AgentRunCommand.Complete(claimed.Revision, Now, claimed.Claim!.Generation, "Legacy result", AgentRunOutcomeKind.NeedsAttention, Guid.NewGuid()).Apply(claimed, Guid.NewGuid);
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync("20261008115119_AutomationDestinations");
                var memory = new SqliteMemoryStore(factory, new FakeTimeProvider(Now));
                await memory.StageSaveAsync(db, parentSnapshot, 0, default);
                await memory.StageSaveAsync(db, child.Snapshot, 0, default);
                db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(parentSnapshot, parent));
                db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(parent));
                db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(child.Snapshot, completed));
                db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(completed));
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO BackgroundCompletionReceipts (ChildAgentRunId, AgentInstanceId, ProfileId, ParentActivationId, SkipReason, CreatedAtUtc) VALUES ({completed.AgentRunId.ToString("D")}, {Owner.AgentInstanceId.ToString("D")}, {Owner.ProfileId.ToString("D")}, NULL, 'quiet-outcome', {Now.ToUnixTimeMilliseconds()})");
                await db.Database.MigrateAsync();
            }
            var upgradedMemory = new SqliteMemoryStore(factory, new FakeTimeProvider(Now)); await upgradedMemory.EnsureCreatedAsync();
            var runs = new SqliteAgentRunStore(factory, upgradedMemory, new Diagnostics());
            Assert.Equal(parent.SessionId, (await upgradedMemory.LoadAsync(parent.SessionId))!.SessionId);
            Assert.Equal(completed.ResponseId, (await runs.GetAsync(Owner, completed.AgentRunId))!.ResponseId);
            var item = (await runs.GetCompletionInboxAsync(Owner, parent.SessionId, completed.AgentRunId, Now))!;
            Assert.Equal(Owner, item.Owner); Assert.Equal(child.Run.SessionId, item.ChildSessionId);
            Assert.Equal(CompletionInboxStatus.Skipped, item.Status); Assert.Equal("quiet-outcome", item.SkipReason);
            Assert.True(await runs.HasCompletionReceiptAsync(Owner, completed.AgentRunId));
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={path}"); SqliteConnection.ClearPool(connection);
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Take_and_report_race_has_one_owner_and_busy_parent_report_cannot_steal(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite); var parent = await ParentTurn(f);
        var child = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        await f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, parent.Claim!.Generation); await CompleteChild(f, child, false);
        var item = (await f.Runs.GetCompletionInboxAsync(Owner, parent.SessionId, child.Run.AgentRunId, Now))!;
        var snapshot = (await f.Memory.LoadAsync(parent.SessionId))!;
        var report = NewRun(new Activation(Guid.NewGuid(), parent.SessionId, ActivationKind.BackgroundCompleted, [], child.Run.AgentRunId, null,
            child.Run.SessionId, child.Run.AgentRunId, $"completion:{child.Run.AgentRunId:D}", Now, "{}"));
        var take = f.Runs.TakeCompletionAsync(Owner, parent.AgentRunId, parent.Claim.Generation, child.Run.AgentRunId, item.Revision, "racing-take", Guid.NewGuid(), Now).AsTask();
        var delivery = Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitCompletionReportAsync(snapshot with { Revision = snapshot.Revision + 1 }, snapshot.Revision, report, child.Run.AgentRunId).AsTask());
        Assert.Equal("Conflict", (await delivery).Code); Assert.Equal(CompletionInboxStatus.Claimed, (await take).Status);
        await f.ReopenAsync();
        Assert.DoesNotContain(await f.Runs.ListForSessionAsync(Owner, parent.SessionId), r => r.ActivationId == report.ActivationId);
        Assert.Equal(CompletionInboxStatus.Claimed, (await f.Runs.GetCompletionInboxAsync(Owner, parent.SessionId, child.Run.AgentRunId, Now))!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wait_denies_self_foreign_child_and_preserves_the_running_parent(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite); var parent = await ParentTurn(f); var other = await ParentTurn(f);
        var child = BackgroundTurn(other.SessionId, other.AgentRunId);
        await f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, other.Claim!.Generation);
        foreach (var target in new[] { parent.SessionId, child.Run.SessionId, Guid.NewGuid() })
        {
            var call = new ModelToolCall("wait-denied", "execution.wait", "{}");
            var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 15000);
            var wait = new AgentRunWait(call.Id, AgentRunWaitMode.Background, [target], AgentRunWaitUntil.Any, Now, Now.AddSeconds(10));
            Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.ApplyAsync(Owner, parent.AgentRunId,
                new AgentRunCommand.SuspendWait(parent.Revision, Now, parent.Claim!.Generation, checkpoint, wait)).AsTask())).Code);
        }
        Assert.Equal(AgentRunStatus.Running, (await f.Runs.GetAsync(Owner, parent.AgentRunId))!.Status);
        Assert.Null((await f.Runs.GetAsync(Owner, parent.AgentRunId))!.Wait);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_successful_answer_rolls_back_handled_accounting_and_preserves_provisional_ack(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite); var parent = await ParentTurn(f);
        var child = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        await f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, parent.Claim!.Generation); await CompleteChild(f, child, false);
        var item = (await f.Runs.GetCompletionInboxAsync(Owner, parent.SessionId, child.Run.AgentRunId, Now))!;
        item = await f.Runs.TakeCompletionAsync(Owner, parent.AgentRunId, parent.Claim.Generation, child.Run.AgentRunId, item.Revision, "take", Guid.NewGuid(), Now);
        item = await f.Runs.AcknowledgeCompletionAsync(Owner, parent.AgentRunId, parent.Claim.Generation, child.Run.AgentRunId, item.Revision, item.ClaimToken!.Value, "Used the evidence", Now);
        var snapshot = (await f.Memory.LoadAsync(parent.SessionId))!;
        await f.Memory.SaveAsync(snapshot with { Revision = snapshot.Revision + 1, Summary = "Concurrent durable update" }, snapshot.Revision);
        var answer = new ConversationEntry(Guid.NewGuid(), snapshot.LastEntrySequence + 1, null, ConversationRole.Assistant, "Used the evidence", parent.ResponseId, EntryStatus.Completed, SessionMode.Text, 0, 17, Now);
        var outcome = snapshot with { Revision = snapshot.Revision + 1, Entries = snapshot.Entries.Append(answer).ToArray(), LastEntrySequence = answer.Sequence };
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.CommitOutcomeAsync(outcome, snapshot.Revision, Owner, parent.AgentRunId,
            new AgentRunCommand.Complete(parent.Revision, Now, parent.Claim.Generation, answer.Text, AgentRunOutcomeKind.Response, answer.EntryId), null).AsTask())).Code);
        await f.ReopenAsync();
        Assert.Equal(AgentRunStatus.Running, (await f.Runs.GetAsync(Owner, parent.AgentRunId))!.Status);
        var staged = (await f.Runs.GetCompletionInboxAsync(Owner, parent.SessionId, child.Run.AgentRunId, Now))!;
        Assert.Equal(CompletionInboxStatus.Claimed, staged.Status); Assert.Equal(item.Acknowledgment, staged.Acknowledgment); Assert.Null(staged.HandledByRunId);
        Assert.DoesNotContain((await f.Memory.LoadAsync(parent.SessionId))!.Entries, entry => entry.EntryId == answer.EntryId);
    }

    private static async Task<AgentRun> CompleteParent(Fixture f, AgentRun parent)
    {
        var snapshot = (await f.Memory.LoadAsync(parent.SessionId))!;
        var result = new ConversationEntry(Guid.NewGuid(), snapshot.Entries.Max(e => e.Sequence) + 1, null, ConversationRole.Assistant,
            "Used background findings.", parent.ResponseId, EntryStatus.Completed, SessionMode.Text, 0, 25, Now);
        return await f.Runs.CommitOutcomeAsync(snapshot with { Revision = snapshot.Revision + 1, Entries = snapshot.Entries.Append(result).ToArray() }, snapshot.Revision,
            Owner, parent.AgentRunId, new AgentRunCommand.Complete(parent.Revision, Now, parent.Claim!.Generation,
                result.Text, AgentRunOutcomeKind.Response, result.EntryId), null);
    }

    private static (SessionSnapshot Snapshot, AgentRun Run) UserTurn(Guid? sessionId = null)
    {
        var entries = new[] { "Check A", "Check B" }.Select((text, index) => new ConversationEntry(Guid.NewGuid(), index + 1,
            null, ConversationRole.User, text, null, EntryStatus.Completed, SessionMode.Text, 0, text.Length, Now)).ToArray();
        var snapshot = Snapshot(sessionId ?? Guid.NewGuid(), entries);
        var activation = new Activation(Guid.NewGuid(), snapshot.SessionId, ActivationKind.UserTurn,
            entries.Select(entry => entry.EntryId).ToArray(), null, null, null, null, "user:batch", Now);
        return (snapshot, NewRun(activation));
    }

    private static (SessionSnapshot Snapshot, AgentRun Run) BackgroundTurn(Guid parentSessionId, Guid parentRunId, string objective = "Check the site", string callKey = "background-1")
    {
        var entry = new ConversationEntry(Guid.NewGuid(), 1, null, ConversationRole.User, objective, null,
            EntryStatus.Completed, SessionMode.Text, 0, objective.Length, Now);
        var sessionId = Guid.NewGuid();
        var activation = new Activation(Guid.NewGuid(), sessionId, ActivationKind.ImmediateBackground, [entry.EntryId],
            null, null, parentSessionId, parentRunId, $"tool:{callKey}", Now);
        var run = NewRun(activation);
        var origin = new SessionOrigin(SessionOriginKind.ImmediateBackground, parentSessionId, parentRunId, run.AgentRunId,
            reportCompletionToOrigin: true);
        return (Snapshot(sessionId, [entry]) with { Origin = origin, Surfaces = origin.InitialSurface }, run);
    }

    private static SessionSnapshot Snapshot(Guid sessionId, IReadOnlyList<ConversationEntry> entries) =>
        new(1, sessionId, 1, Definition, SessionMode.Text, null, SessionStatus.Created, entries, "", 0, null,
            Owner.ProfileId, Now, Now, Owner.AgentInstanceId, PinnedPersona: Definition.Identity);

    private static AgentRun NewRun(Activation activation) => AgentRun.Create(Guid.NewGuid(), Owner,
        new AgentRunAdmission(activation, Definition.Id, Definition.Version, Definition.Identity, Guid.NewGuid(), activation.Kind == ActivationKind.BackgroundCompleted ? AgentRunOutputContract.CompletionReport
            : activation.SourceEntryIds.Count > 0 && activation.Kind != ActivationKind.UserTurn ? AgentRunOutputContract.BackgroundOutcome : AgentRunOutputContract.ConversationResponse),
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
