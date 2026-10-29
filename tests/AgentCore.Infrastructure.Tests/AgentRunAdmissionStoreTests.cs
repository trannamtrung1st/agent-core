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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class AgentRunAdmissionStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly AgentRunOwner Owner = new(Guid.Parse("019944af-0009-7000-8000-0000000000a1"), Guid.Parse("019944af-0009-7000-8000-0000000000b1"));
    private static readonly AgentDefinition Definition = new(2, "examiner", 1,
        new AgentIdentity("Alex", "Examiner", "Practice", "Calm"), ["Practice"], "Instructions",
        new BehaviorPolicy("acknowledgeThenContinue", true, true), new ConversationPolicy("concise", true, "en", 256),
        new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]), new VoiceConfiguration(true, "default", 1),
        new ProviderPreferences("primary-stt", "primary-tts"), new Dictionary<string, string>());
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Terminal_cleanup_scan_includes_background_surfaces_and_excludes_nonterminal_and_deleted_sessions(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var terminal = Snapshot(Guid.NewGuid(), []) with { Surfaces = SessionSurface.BackgroundWork,
            LifecycleStatus = SessionLifecycleStatus.Completed };
        var paused = Snapshot(Guid.NewGuid(), []) with { Status = SessionStatus.Paused, LifecycleStatus = SessionLifecycleStatus.Paused };
        var deleted = terminal with { SessionId = Guid.NewGuid(), DurablyDeletedAt = Now };
        await f.Memory.SaveAsync(terminal, 0); await f.Memory.SaveAsync(paused, 0); await f.Memory.SaveAsync(deleted, 0);
        await f.ReopenAsync();
        var candidates = await f.Memory.ListTerminalSessionsAsync(null, 100);
        Assert.Equal(terminal.SessionId, Assert.Single(candidates.Items).SessionId);
        Assert.Empty(await f.Memory.ReadHistoryAsync(terminal.SessionId, 0, 10));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Request_isolation_preserves_same_admission_retry_but_rejects_another_request_after_reopen(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var entry = new ConversationEntry(Guid.NewGuid(), 1, Guid.NewGuid(), ConversationRole.User, "First request", null,
            EntryStatus.Completed, SessionMode.Text, 0, 0, Now);
        var definition = Definition with { Voice = Definition.Voice with { Enabled = false },
            StateIsolationPolicy = new(ConversationContextScope.CurrentRequest) };
        var snapshot = Snapshot(Guid.NewGuid(), [entry]) with { Definition = definition,
            ModelSelection = new("synthetic", "synthetic", "synthetic", ModelSelectionSource.TenantDefault, null) };
        await f.Memory.SaveAsync(snapshot, 0);
        var configuration = new AgentRunConfiguration(definition, 0, 0, []);
        var run = AgentRunAdmissionFactory.ForAcceptedUserBatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), snapshot,
            [entry], Now, [], configuration: configuration);
        var proposed = snapshot with { Revision = 2 };
        Assert.True((await f.Runs.AdmitAsync(proposed, 1, run)).Created);
        await f.ReopenAsync();
        var replay = await f.Runs.AdmitAsync(proposed, 1, run);
        Assert.False(replay.Created); Assert.Equal(run.AgentRunId, replay.Run.AgentRunId);
        var next = entry with { EntryId = Guid.NewGuid(), SourceEventId = Guid.NewGuid(), Sequence = 2, Text = "Another request" };
        var second = AgentRunAdmissionFactory.ForAcceptedUserBatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), snapshot with { Entries = [entry, next] },
            [next], Now, [], configuration: configuration);
        Assert.Equal("fresh_session_required", (await Assert.ThrowsAsync<AgentCoreException>(() =>
            f.Runs.AdmitAsync(proposed with { Revision = 3, Entries = [entry, next] }, 2, second).AsTask())).Code);
        var relaxed = AgentRunAdmissionFactory.ForAcceptedUserBatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), snapshot with { Entries = [entry, next] },
            [next], Now, [], configuration: new AgentRunConfiguration(definition with { StateIsolationPolicy = StateIsolationPolicy.Default }, 0, 0, []));
        Assert.Equal("fresh_session_required", (await Assert.ThrowsAsync<AgentCoreException>(() =>
            f.Runs.AdmitAsync(proposed with { Revision = 3, Entries = [entry, next] }, 2, relaxed).AsTask())).Code);
        Assert.Single(await f.Runs.ListForSessionAsync(Owner, snapshot.SessionId));
        Assert.Equal(2, (await f.Memory.LoadAsync(snapshot.SessionId))!.Revision);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Resolved_browser_isolation_is_fingerprinted_and_survives_durable_reopen(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var entry = new ConversationEntry(Guid.NewGuid(), 1, Guid.NewGuid(), ConversationRole.User, "Request", null,
            EntryStatus.Completed, SessionMode.Text, 0, 0, Now);
        var snapshot = Snapshot(Guid.NewGuid(), [entry]) with { ModelSelection = new("synthetic", "synthetic", "synthetic", ModelSelectionSource.TenantDefault, null) };
        await f.Memory.SaveAsync(snapshot, 0);
        var configuration = new AgentRunConfiguration(Definition, 0, 0, [], resolvedBrowserIsolation: BrowserIsolation.EphemeralSession);
        var shared = new AgentRunConfiguration(Definition, 0, 0, [], resolvedBrowserIsolation: BrowserIsolation.PersistentAgent);
        Assert.NotEqual(configuration.ConfigurationHash, shared.ConfigurationHash);
        var run = AgentRunAdmissionFactory.ForAcceptedUserBatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), snapshot, [entry], Now,
            [], configuration: configuration);
        await f.Runs.AdmitAsync(snapshot with { Revision = 2 }, 1, run);
        await f.ReopenAsync();
        var admitted = (await f.Runs.GetAsync(Owner, run.AgentRunId))!.Admission.Configuration!;
        Assert.Equal(BrowserIsolation.EphemeralSession, admitted.ResolvedBrowserIsolation);
        Assert.Equal(configuration.ConfigurationHash, admitted.ConfigurationHash);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task State_counts_runs_once_across_retry_and_reopen_without_reading_transcripts(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var snapshot = Snapshot(Guid.NewGuid(), []);
        await f.Memory.SaveAsync(snapshot, 0);
        var activation = Activation.Start(Guid.NewGuid(), snapshot.SessionId, ActivationKind.Initiative, [], Guid.NewGuid(), null, null, null, "state-count", Now);
        var run = NewRun(activation);
        await f.Runs.AdmitAsync(snapshot with { Revision = 2 }, 1, run);
        var initial = await f.Runs.ReadStateAsync(Owner);
        Assert.Equal(1, initial.Queued); Assert.Equal(0, initial.Executing);
        var claimed = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Claim(1, Now, Guid.NewGuid(), Now.AddMinutes(1)));
        Assert.Equal(1, (await f.Runs.ReadStateAsync(Owner)).Executing);
        await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Fail(claimed.Revision, Now, claimed.Claim!.Generation,
            "provider-unavailable", "Temporary failure", true, Now.AddSeconds(1)));
        await f.ReopenAsync();
        var retry = await f.Runs.ReadStateAsync(Owner);
        Assert.Equal(0, retry.Executing); Assert.Equal(0, retry.Queued); Assert.Equal(1, retry.Retrying);
        Assert.Equal(0, retry.Terminal);
        Assert.Equal(1, (await f.Runs.ReadSessionStateAsync(Owner, snapshot.SessionId)).Retrying);
        Assert.Equal(0, (await f.Runs.ReadSessionStateAsync(Owner, Guid.NewGuid())).Retrying);
        var state = await f.Memory.ReadStateAsync(Owner);
        Assert.Equal(1, state.Open); Assert.Equal(0, state.Paused); Assert.Equal(0, state.Terminal);
        Assert.Equal(0, (await f.Memory.ReadStateAsync(new AgentRunOwner(Guid.NewGuid(), Owner.ProfileId))).Open);
        Assert.Equal(0, (await f.Runs.ReadStateAsync(new AgentRunOwner(Owner.AgentInstanceId, Guid.NewGuid()))).Retrying);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batched_explicit_input_and_reference_evidence_are_frozen_across_retry_and_reopen(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        UserMessagePart[] first = [new("invocation",InvocationKind:"skill",SkillKey:"definition:first"),new("text",Text:" Check A")];
        UserMessagePart[] second = [new("invocation",InvocationKind:"skill",SkillKey:"definition:second"),new("reference",Reference:new("session",SessionId:Guid.NewGuid()),Label:"Evidence")];
        var entries = new[]{first,second}.Select((parts,i)=>new ConversationEntry(Guid.NewGuid(),i+1,Guid.NewGuid(),ConversationRole.User,UserMessageContent.DisplayText(parts),null,EntryStatus.Completed,SessionMode.Text,0,0,Now,Parts:parts)).ToArray();
        var snapshot = Snapshot(Guid.NewGuid(),entries) with {ModelSelection = new("synthetic", "synthetic", "synthetic", ModelSelectionSource.TenantDefault, null)};
        await f.Memory.SaveAsync(snapshot,0);
        EffectiveSkill[] catalog = [new("definition:first",SkillOrigin.Definition,"first","First","First","FIRST_PROCEDURE",SkillProjection.OnDemand,[],[]),new("definition:second",SkillOrigin.Definition,"second","Second","Second","SECOND_PROCEDURE",SkillProjection.OnDemand,[],[])];
        var references = new[]{new ComposerReferenceEvidence(second[1].Reference!,"Evidence","valid","SOURCE_ONE",7,"hash")};
        var keys = new[]{"definition:first","definition:second"};
        var pin = new ComposerRunInput(keys,references,Now);
        var run = AgentRunAdmissionFactory.ForAcceptedUserBatch(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),snapshot,entries,Now,catalog,composerInput:pin);
        keys[0]="definition:mutated"; references[0]=references[0] with {Text="SOURCE_TWO"};
        await f.Runs.AdmitAsync(snapshot with {Revision=2},1,run);
        var claim = await f.Runs.ApplyAsync(Owner,run.AgentRunId,new AgentRunCommand.Claim(1,Now,Guid.NewGuid(),Now.AddMinutes(1)));
        var retry = await f.Runs.ApplyAsync(Owner,run.AgentRunId,new AgentRunCommand.Fail(claim.Revision,Now,claim.Claim!.Generation,"provider-unavailable","Temporary failure",true,Now.AddSeconds(1)));
        await f.ReopenAsync();
        var saved=(await f.Runs.GetAsync(Owner,run.AgentRunId))!;
        Assert.Equal(["definition:first","definition:second"],saved.ActiveSkillKeys); Assert.Equal(0,saved.SkillLoadCount);
        Assert.Equal(entries.Select(e=>e.EntryId),saved.Admission.Activation.SourceEntryIds);
        Assert.Equal("SOURCE_ONE",Assert.Single(saved.Admission.ComposerInput!.References).Text);
        Assert.Equal(run.ResponseId,saved.ResponseId); Assert.Equal(retry.Status,saved.Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Protocol_recovery_allowance_and_read_receipt_survive_reopen_and_retry(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var snapshot = Snapshot(Guid.NewGuid(), []);
        await f.Memory.SaveAsync(snapshot, 0);
        var activation = Activation.Start(Guid.NewGuid(), snapshot.SessionId, ActivationKind.Initiative, [], Guid.NewGuid(), null, null, null, "repair-pin", Now);
        var run = AgentRun.Create(Guid.NewGuid(), Owner, new(activation, Definition.Id, Definition.Version, Definition.Identity,
            Guid.NewGuid(), AgentRunOutputContract.ConversationResponse), new("synthetic", "synthetic", "synthetic", null), 3, Now);
        await f.Runs.AdmitAsync(snapshot with { Revision = 2 }, 1, run);
        var claimed = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Claim(1, Now, Guid.NewGuid(), Now.AddMinutes(1)));
        var recovery = new RunProtocolRecovery(ProviderFailureReason.InvalidJson, ProviderResponseChannel.StructuredOutput, "eligible", "terminalText", "started");
        ModelMessage[] messages = [new(ModelRole.Assistant, "", ToolCalls: [new("read", "web.fetch", "{}")]),
            new(ModelRole.Tool, "{\"status\":\"ok\"}", ToolCallId: "read", Name: "web.fetch")];
        var payload = AgentRunToolCallCheckpoint.Write(messages, protocolRepairReason: recovery.Reason,
            executionState: new(TranscriptId: run.ResponseId, ProtocolRecovery: recovery));
        var saved = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(claimed.Revision, Now, claimed.Claim!.Generation,
            new(payload, 1, 15, 60000), null));
        var retry = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Fail(saved.Revision, Now, saved.Claim!.Generation,
            "provider-unavailable", "Temporary provider failure", true, Now.AddSeconds(1)));
        await f.ReopenAsync();
        var restored = (await f.Runs.GetAsync(Owner, run.AgentRunId))!;
        Assert.Equal(run.ResponseId, restored.ResponseId);
        Assert.Equal(recovery, AgentRunToolCallCheckpoint.ReadExecutionState(restored.Checkpoint).ProtocolRecovery);
        Assert.Equal(recovery.Reason, AgentRunToolCallCheckpoint.ReadProtocolRepairReason(restored.Checkpoint));
        Assert.True(AgentRunToolCallCheckpoint.TryRead(restored.Checkpoint, out var receipts));
        Assert.Single(receipts!, m => m.Name == "web.fetch");
        var second = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Claim(retry.Revision, Now.AddSeconds(1), Guid.NewGuid(), Now.AddMinutes(1)));
        Assert.Equal(2, second.AttemptCount);
        Assert.Equal(payload, second.Checkpoint!.PayloadJson);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Effective_budget_and_accounting_survive_reopen_retry_and_reclaim(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var snapshot = Snapshot(Guid.NewGuid(), []);
        await f.Memory.SaveAsync(snapshot, 0);
        var activation = Activation.Start(Guid.NewGuid(), snapshot.SessionId, ActivationKind.Initiative, [], Guid.NewGuid(), null, null, null, "budget-pin", Now);
        var pin = ExecutionBudgetPolicy.Resolve(ExecutionBudgetClass.InteractiveBrowser,
            new(InteractiveBrowser: ExecutionBudgetProfile.For(ExecutionBudgetClass.InteractiveBrowser, ExecutionBudgetPreset.Extended)), null)
            with { RequestedCleanup = true, CleanupIntent = new(true, false) };
        var run = AgentRun.Create(Guid.NewGuid(), Owner, new(activation, Definition.Id, Definition.Version, Definition.Identity,
            Guid.NewGuid(), AgentRunOutputContract.ConversationResponse, pin), new("synthetic", "synthetic", "synthetic", null), 3, Now);
        await f.Runs.AdmitAsync(snapshot with { Revision = 2 }, 1, run);
        var claimed = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Claim(1, Now, Guid.NewGuid(), Now.AddMinutes(1)));
        var saved = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(claimed.Revision, Now.AddSeconds(1), claimed.Claim!.Generation,
            new(AgentRunToolCallCheckpoint.Write([], executionState: new(TranscriptId: run.ResponseId)), 30, 5000, 450000, 150000), null));
        var retry = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Fail(saved.Revision, Now.AddSeconds(2), saved.Claim!.Generation,
            "provider-unavailable", "Temporary provider failure", true, Now.AddSeconds(3)));
        await f.ReopenAsync();
        var restored = (await f.Runs.GetAsync(Owner, run.AgentRunId))!;
        Assert.Equal(pin, restored.Admission.ExecutionBudget); Assert.Equal(30, restored.Checkpoint!.StepCount);
        Assert.Equal(151000, restored.Checkpoint.ActiveExecutionMs); Assert.Equal(449000, restored.Checkpoint.RemainingOverallBudgetMs);
        var second = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Claim(retry.Revision, Now.AddSeconds(3), Guid.NewGuid(), Now.AddMinutes(1)));
        var recovered = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Recover(second.Revision, Now.AddMinutes(1)));
        await f.ReopenAsync();
        Assert.Equal(pin, (await f.Runs.GetAsync(Owner, run.AgentRunId))!.Admission.ExecutionBudget);
        Assert.Equal(30, recovered.Checkpoint!.StepCount); Assert.Equal(5000, recovered.Checkpoint.OutputBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_live_receipt_and_run_admit_atomically_and_replay_reuses_the_committed_identity(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var snapshot = Snapshot(Guid.NewGuid(), []);
        await f.Memory.SaveAsync(snapshot, 0);
        var occurrence = await PrepareLive(f, snapshot.SessionId);
        var activation = Activation.Start(Guid.NewGuid(), snapshot.SessionId, ActivationKind.ApplicationEvent, [],
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
        var replay = NewRun(Activation.Start(Guid.NewGuid(), snapshot.SessionId, ActivationKind.ApplicationEvent, [],
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
        var receipt = Assert.Single(await f.CoreEvents.PendingAsync());
        Assert.Equal("run.completed", receipt.Event.Key);
        Assert.Equal(Owner.AgentInstanceId, receipt.Event.Owner.AgentInstanceId);
        await f.ReopenAsync();
        Assert.Equal(receipt.Event.EventId, Assert.Single(await f.CoreEvents.PendingAsync()).Event.EventId);
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
        Assert.Empty(await f.CoreEvents.PendingAsync());
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
        var activation = Activation.Start(Guid.NewGuid(), user.Snapshot.SessionId, ActivationKind.ScheduledWork, [],
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
        var activation = Activation.Start(Guid.NewGuid(), session.SessionId, ActivationKind.ScheduledWork,
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
        var activation = Activation.Start(Guid.NewGuid(), first.Snapshot.SessionId, ActivationKind.UserTurn,
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
        var task = IndependentManualTurn();
        await f.Runs.AdmitAsync(task.Snapshot, 0, task.Run);
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
        var nextActivation = Activation.Start(Guid.NewGuid(), child.SessionId, ActivationKind.UserTurn, [user.EntryId],
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
    public async Task Prepared_effect_identity_and_uncertain_outcome_survive_restart(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var turn = UserTurn();
        await f.Runs.AdmitAsync(turn.Snapshot, 0, turn.Run);
        var run = await Claim(f, turn.Run);
        run = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.MarkSideEffect(run.Revision,
            Now.AddSeconds(1), run.Claim!.Generation, AgentRunSideEffectDisposition.Prepared, "email-1", Hash));
        await f.ReopenAsync();
        run = (await f.Runs.GetAsync(Owner, run.AgentRunId))!;
        Assert.Equal(AgentRunSideEffectDisposition.Prepared, run.SideEffect.Disposition);
        Assert.Equal(Hash, run.SideEffect.ActionHash);
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
        var task = IndependentManualTurn();
        await f.Runs.AdmitAsync(task.Snapshot, 0, task.Run);
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
        var a = Activation.Start(Guid.NewGuid(), turn.Snapshot.SessionId, ActivationKind.UserTurn,
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
    public async Task Current_untracked_schema_is_refused_without_stamping_or_mutating_data()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-run-current-schema-{Guid.NewGuid():N}.db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
            var memory = new SqliteMemoryStore(factory, new FakeTimeProvider(Now));
            var turn = UserTurn();
            var runs = new SqliteAgentRunStore(factory,memory,new Diagnostics());
            await runs.AdmitAsync(turn.Snapshot,0,turn.Run);
            var error = await Assert.ThrowsAsync<AgentCoreException>(() => memory.EnsureCreatedAsync().AsTask());
            Assert.Contains("data_reset_required",error.Message);
            Assert.NotNull(await runs.GetAsync(Owner,turn.Run.AgentRunId));
            await using var verify = factory.CreateDbContext();
            Assert.Empty(await verify.Database.GetAppliedMigrationsAsync());
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
            Assert.Contains("data_reset_required", error.Message, StringComparison.Ordinal);
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
            Assert.Contains("data_reset_required", error.Message, StringComparison.Ordinal);
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
        var a = Activation.Start(Guid.NewGuid(), turn.Snapshot.SessionId, ActivationKind.UserTurn,
            turn.Run.Admission.Activation.SourceEntryIds, null, null, null, null, "different:key", Now);
        var duplicate = NewRun(a);
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitAsync(turn.Snapshot, 0, duplicate).AsTask())).Code);
        Assert.Null(await f.Runs.GetActivationAsync(Owner, duplicate.ActivationId));
        Assert.Single(await f.Runs.ListForSessionAsync(Owner, turn.Snapshot.SessionId));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_pages_recheck_session_visibility_and_ownership_after_reopen(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var admitted = new List<AgentRun>();
        for (var i = 0; i < 5; i++)
        {
            var turn = UserTurn();
            admitted.Add((await f.Runs.AdmitAsync(turn.Snapshot, 0, turn.Run)).Run);
        }
        // Retained evidence must not project a Session under a different owner.
        for (var i = 0; i < 4; i++)
        {
            var snapshot = (await f.Memory.LoadAsync(admitted[i].SessionId))!;
            var updated = i switch
            {
                0 => snapshot with { DurablyDeletedAt = Now },
                1 => snapshot with { AgentInstanceId = Guid.NewGuid() },
                2 => snapshot with { ProfileId = Guid.NewGuid() },
                _ => snapshot with { ArchivedAt = Now }
            };
            if (i == 1)
            {
                await Assert.ThrowsAsync<AgentCoreException>(() => f.Memory.SaveAsync(updated with { Revision = snapshot.Revision + 1 }, snapshot.Revision).AsTask());
                updated = snapshot with { DurablyDeletedAt = Now };
            }
            await f.Memory.SaveAsync(updated with { Revision = snapshot.Revision + 1 }, snapshot.Revision);
        }
        await f.ReopenAsync();
        var expected = admitted.Skip(3).OrderByDescending(run => run.AgentRunId.ToString("D"), StringComparer.Ordinal).ToArray();
        var first = await f.Runs.ListPageAsync(Owner, null, null, 1);
        Assert.Equal(expected[0].AgentRunId, Assert.Single(first.Items).AgentRunId);
        Assert.True(first.HasMore);
        var last = await f.Runs.ListPageAsync(Owner, null, first.NextCursor, 1);
        Assert.Equal(expected[1].AgentRunId, Assert.Single(last.Items).AgentRunId);
        Assert.False(last.HasMore);
        Assert.Null(last.NextCursor);
        foreach (var run in admitted.Take(3))
        {
            Assert.NotNull(await f.Runs.GetAsync(Owner, run.AgentRunId));
            Assert.Empty((await f.Runs.ListPageAsync(Owner, run.SessionId, null, 1)).Items);
            var after = await f.Runs.ListPageAsync(Owner, null, run.AgentRunId, 100);
            Assert.Equal(expected.Where(item => StringComparer.Ordinal.Compare(item.AgentRunId.ToString("D"), run.AgentRunId.ToString("D")) < 0)
                .Select(item => item.AgentRunId), after.Items.Select(item => item.AgentRunId));
            await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.ListPageAsync(
                new(Owner.AgentInstanceId, Guid.NewGuid()), null, run.AgentRunId, 1).AsTask());
        }
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

    private static async Task<AgentRun> CompleteParent(Fixture f, AgentRun parent, DateTimeOffset? at = null)
    {
        var snapshot = (await f.Memory.LoadAsync(parent.SessionId))!;
        var result = new ConversationEntry(Guid.NewGuid(), snapshot.Entries.Max(e => e.Sequence) + 1, null, ConversationRole.Assistant,
            "Used background findings.", parent.ResponseId, EntryStatus.Completed, SessionMode.Text, 0, 25, Now);
        return await f.Runs.CommitOutcomeAsync(snapshot with { Revision = snapshot.Revision + 1, Entries = snapshot.Entries.Append(result).ToArray() }, snapshot.Revision,
            Owner, parent.AgentRunId, new AgentRunCommand.Complete(parent.Revision, at ?? Now, parent.Claim!.Generation,
                result.Text, AgentRunOutcomeKind.Response, result.EntryId), null);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Terminal_failure_and_session_transitions_emit_once_and_rollback_has_no_receipt(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var (snapshot, run) = UserTurn();
        await f.Runs.AdmitAsync(snapshot, 0, run);
        run = await Claim(f, run);
        run = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Fail(run.Revision, Now,
            run.Claim!.Generation, "provider-unavailable", "Safe fixture", true, Now.AddSeconds(1)));
        Assert.Empty(await f.CoreEvents.PendingAsync());
        run = await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, Now.AddSeconds(1), Guid.NewGuid(), Now.AddMinutes(1)));
        await f.Runs.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Fail(run.Revision, Now.AddSeconds(2),
            run.Claim!.Generation, "provider-unavailable", "Safe fixture", false, null));
        var failed = Assert.Single(await f.CoreEvents.PendingAsync());
        Assert.Equal("run.failed", failed.Event.Key);
        Assert.DoesNotContain("Safe fixture", failed.Event.DataJson);
        var complete = snapshot with { Revision = 2, LifecycleStatus = SessionLifecycleStatus.Completed };
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Memory.SaveAsync(complete with { Revision = 3 }, 2).AsTask());
        Assert.Single(await f.CoreEvents.PendingAsync());
        await f.Memory.SaveAsync(complete, 1);
        await f.Memory.SaveAsync(complete, 1);
        var ended = complete with { Revision = 3, LifecycleStatus = SessionLifecycleStatus.Ended };
        await f.Memory.SaveAsync(ended, 2);
        await f.ReopenAsync();
        var emitted = await f.CoreEvents.PendingAsync();
        Assert.Equal(3, emitted.Count);
        Assert.Contains(emitted, e => e.Event.Key == "session.completed");
        Assert.Contains(emitted, e => e.Event.Key == "session.ended");
        Assert.All(emitted, e => Assert.Equal(new TriggerOwner(Owner.AgentInstanceId, Owner.ProfileId), e.Event.Owner));
    }

    [Fact]
    public void User_text_cannot_forge_causation_and_plain_native_signal_cannot_break_completion()
    {
        foreach (var kind in new[] { ActivationKind.UserTurn, ActivationKind.ApplicationEvent })
        {
            var evidence = kind == ActivationKind.UserTurn
                ? "{\"triggerContext\":{\"causation\":{\"triggerDepth\":4,\"visitedAutomationIds\":[]}}}"
                : "{\"Text\":\"Native plain signal\"}";
            var activation = Activation.Start(Guid.NewGuid(), Guid.NewGuid(), kind, [Guid.NewGuid()], null,
                kind == ActivationKind.ApplicationEvent ? Guid.NewGuid() : null, null, null, "test", Now, evidence);
            var run = NewRun(activation).TakeClaim(Guid.NewGuid(), Now, Now.AddMinutes(1));
            var failed = run.Fail(run.Revision, run.Claim!.Generation, "safe-failure", "Safe fixture", false, Now, null, Guid.NewGuid);
            Assert.Equal(0, CoreEventPersistence.Run(run, failed)!.TriggerDepth);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Coalesced_causation_preserves_all_distinct_visited_automations(bool wrapped)
    {
        var visited = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
        var root = Guid.NewGuid();
        var evidence = System.Text.Json.JsonSerializer.Serialize(new { triggerContext = new { causation = new
        { rootAgentRunId = root, triggerDepth = 2, visitedAutomationIds = visited.Concat(visited).ToArray() } } });
        if (wrapped) evidence = System.Text.Json.JsonSerializer.Serialize(new { Text = evidence });
        var activation = Activation.Start(Guid.NewGuid(), Guid.NewGuid(), ActivationKind.CoreEvent, [Guid.NewGuid()],
            null, Guid.NewGuid(), null, null, "bucket:fixture", Now, evidence);
        var run = NewRun(activation).TakeClaim(Guid.NewGuid(), Now, Now.AddMinutes(1));
        var failed = run.Fail(run.Revision, run.Claim!.Generation, "safe-failure", "Safe fixture", false, Now, null, Guid.NewGuid);
        var emitted = CoreEventPersistence.Run(run, failed)!;
        Assert.Equal(root, emitted.RootAgentRunId);
        Assert.Equal(2, emitted.TriggerDepth);
        Assert.Equal(visited, emitted.VisitedAutomationIds);
    }

    private static (SessionSnapshot Snapshot, AgentRun Run) UserTurn(Guid? sessionId = null)
    {
        var entries = new[] { "Check A", "Check B" }.Select((text, index) => new ConversationEntry(Guid.NewGuid(), index + 1,
            null, ConversationRole.User, text, null, EntryStatus.Completed, SessionMode.Text, 0, text.Length, Now)).ToArray();
        var snapshot = Snapshot(sessionId ?? Guid.NewGuid(), entries);
        var activation = Activation.Start(Guid.NewGuid(), snapshot.SessionId, ActivationKind.UserTurn,
            entries.Select(entry => entry.EntryId).ToArray(), null, null, null, null, "user:batch", Now);
        return (snapshot, NewRun(activation));
    }

    private static (SessionSnapshot Snapshot, AgentRun Run) IndependentManualTurn(string objective = "Check the site")
    {
        var entry = new ConversationEntry(Guid.NewGuid(), 1, null, ConversationRole.User, objective, null,
            EntryStatus.Completed, SessionMode.Text, 0, objective.Length, Now);
        var sessionId = Guid.NewGuid();
        var activation = Activation.Start(Guid.NewGuid(), sessionId, ActivationKind.ManualBackground, [entry.EntryId],
            null, null, null, null, "manual-fixture", Now);
        var run = NewRun(activation);
        var origin = new SessionOrigin(SessionOriginKind.ManualBackground, initialBackgroundAgentRunId: run.AgentRunId);
        return (Snapshot(sessionId, [entry]) with { Origin = origin, Surfaces = origin.InitialSurface }, run);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Instance_activity_pages_all_surfaces_once_with_exact_owner_and_stable_cursor(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var chat = Snapshot(Guid.Parse("019944af-0009-7000-8000-000000000002"), []) with { Title = "Conversation", UpdatedAt = Now.AddSeconds(3).AddTicks(3) };
        var background = Snapshot(Guid.Parse("019944af-0009-7000-8000-000000000001"), []) with { Title = "Task", UpdatedAt = Now.AddSeconds(3).AddTicks(9),
            Origin = new SessionOrigin(SessionOriginKind.ManualBackground, initialBackgroundAgentRunId: Guid.NewGuid()),
            Surfaces = SessionSurface.ChatList | SessionSurface.BackgroundWork };
        var archived = Snapshot(Guid.NewGuid(), []) with { Title = "Archived", UpdatedAt = Now, ArchivedAt = Now };
        var hidden = Snapshot(Guid.NewGuid(), []) with { DurablyDeletedAt = Now };
        var foreign = Snapshot(Guid.NewGuid(), []) with { AgentInstanceId = Guid.NewGuid(), UpdatedAt = Now.AddSeconds(4) };
        var otherProfile = Snapshot(Guid.NewGuid(), []) with { ProfileId = Guid.NewGuid(), UpdatedAt = Now.AddSeconds(5) };
        foreach (var row in new[] { chat, background, archived, hidden, foreign, otherProfile }) await f.Memory.SaveAsync(row, 0);
        await f.ReopenAsync();
        var first = await f.Memory.ListInstanceSessionsAsync(Owner, null, 1);
        var tied = new[] { chat.SessionId, background.SessionId }.OrderDescending().ToArray();
        Assert.Equal(tied[0], Assert.Single(first.Items).SessionId);
        Assert.True(first.HasMore);
        var rest = await f.Memory.ListInstanceSessionsAsync(Owner, first.NextCursor, 10);
        Assert.Equal(new[] { tied[1], archived.SessionId }, rest.Items.Select(row => row.SessionId));
        Assert.False(rest.HasMore);
        Assert.Empty((await f.Memory.ListInstanceSessionsAsync(new(Guid.NewGuid(), Owner.ProfileId), null, 20)).Items);
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Memory.ListInstanceSessionsAsync(Owner, "bad-cursor", 20).AsTask())).Code);
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Memory.ListInstanceSessionsAsync(Owner, null, 101).AsTask())).Code);
    }

    private static SessionSnapshot Snapshot(Guid sessionId, IReadOnlyList<ConversationEntry> entries) =>
        new(1, sessionId, 1, Definition, SessionMode.Text, null, SessionStatus.Created, entries, "", 0, null,
            Owner.ProfileId, Now, Now, Owner.AgentInstanceId, PinnedPersona: Definition.Identity);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Configuration_continuation_requires_exact_source_receipt_and_deduplicates_after_reopen(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        await f.InsertContinuationOwnerAsync();
        var (snapshot, _) = UserTurn();
        snapshot = snapshot with { ModelSelection = new("synthetic", "synthetic", "synthetic", ModelSelectionSource.TenantDefault, null) };
        var source = AgentRunAdmissionFactory.ForAcceptedUserBatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            snapshot, snapshot.Entries, Now, []);
        await f.Runs.AdmitAsync(snapshot, 0, source);
        source = await Claim(f, source);
        var task = new ConfigurationPendingTask(source.AgentRunId, ToolCatalog.WorkspaceList, "List the workspace", 1);
        var messages = new[] { new ModelMessage(ModelRole.Tool,
            System.Text.Json.JsonSerializer.Serialize(new { outcome = "applied", pendingTask = task },
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            Name: "configuration.propose", ToolCallId: "configuration") };
        source = await f.Runs.ApplyAsync(Owner, source.AgentRunId, new AgentRunCommand.Checkpoint(source.Revision,
            Now, source.Claim!.Generation, new(AgentRunToolCallCheckpoint.Write(messages), 1, 0, 10000), null));
        source = await CompleteParent(f, source);
        snapshot = (await f.Memory.LoadAsync(snapshot.SessionId))!;
        var next = snapshot with { Revision = snapshot.Revision + 1 };
        var configuration = new AgentRunConfiguration(Definition, 1, 0, []);
        var budget = ExecutionBudgetPolicy.Resolve(ExecutionBudgetClass.InteractiveBrowser, null, null);
        AgentRun Propose(ConfigurationPendingTask receipt) => AgentRunAdmissionFactory.ForConfigurationContinuation(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), snapshot, source, receipt, Now, [], budget, configuration);
        var invalid = Propose(task with { Target = ToolCatalog.WorkspaceSearch });
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(
            () => f.Runs.AdmitAsync(next, snapshot.Revision, invalid).AsTask())).Code);
        Assert.Equal(snapshot.Revision, (await f.Memory.LoadAsync(snapshot.SessionId))!.Revision);
        var admitted = await f.Runs.AdmitAsync(next, snapshot.Revision, Propose(task));
        Assert.True(admitted.Created);
        await f.ReopenAsync();
        var duplicate = await f.Runs.AdmitAsync(next, snapshot.Revision, Propose(task));
        Assert.False(duplicate.Created);
        Assert.Equal(admitted.Run.AgentRunId, duplicate.Run.AgentRunId);
        Assert.Equal(2, (await f.Runs.ListForSessionAsync(Owner, snapshot.SessionId)).Count);
        Assert.Empty(duplicate.Run.Admission.Activation.SourceEntryIds);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Duration_wait_reopens_and_resumes_same_run_once_without_an_attempt(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite); var parent = await ParentTurn(f);
        var call = new ModelToolCall("duration", "execution.wait", "{\"mode\":\"duration\",\"seconds\":10}");
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new ModelMessage(ModelRole.Assistant, "", ToolCalls: [call])], executionState: new(TranscriptId: parent.ResponseId)), 1, 0, 15000);
        var wait = new AgentRunWait(call.Id, AgentRunWaitMode.Duration, AgentRunWaitUntil.All, Now, Now.AddSeconds(10));
        var suspended = await f.Runs.ApplyAsync(Owner, parent.AgentRunId, new AgentRunCommand.SuspendWait(parent.Revision, Now, parent.Claim!.Generation, checkpoint, wait));
        Assert.Null(suspended.Claim);
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext();
            var migrator = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db);
            await migrator.MigrateAsync("20261027040000_SessionGoals");
            var id = parent.AgentRunId.ToString("D");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentRuns SET PayloadJson=json_set(PayloadJson,'$.wait.backgroundSessionIds',json('[]')) WHERE AgentRunId={id}");
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
        }
        await f.ReopenAsync();
        Assert.DoesNotContain(await f.Runs.ListRunnableAsync(Now.AddSeconds(9), 20), r => r.AgentRunId == parent.AgentRunId);
        Assert.Contains(await f.Runs.ListRunnableAsync(Now.AddSeconds(10), 20), r => r.AgentRunId == parent.AgentRunId);
        var generation = Guid.NewGuid(); var wake = new AgentRunCommand.ResumeWait(suspended.Revision, Now.AddSeconds(10), generation, Now.AddMinutes(1));
        var resumed = await f.Runs.ApplyAsync(Owner, parent.AgentRunId, wake);
        Assert.Equal(parent.AgentRunId, resumed.AgentRunId); Assert.Equal(parent.ActivationId, resumed.ActivationId);
        Assert.Equal(parent.ResponseId, resumed.ResponseId); Assert.Equal(parent.AttemptCount, resumed.AttemptCount); Assert.Equal(generation, resumed.Claim!.Generation);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Checkpoint, out var messages));
        Assert.Contains("elapsed", Assert.Single(messages!, m => m.Role == ModelRole.Tool).Text);
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.ApplyAsync(Owner, parent.AgentRunId, wake).AsTask());
    }

    private static AgentRun NewRun(Activation activation, AgentRunConfiguration? configuration = null) => AgentRun.Create(Guid.NewGuid(), Owner,
        new AgentRunAdmission(activation, Definition.Id, Definition.Version, Definition.Identity, Guid.NewGuid(), activation.Kind == ActivationKind.BackgroundCompleted ? AgentRunOutputContract.CompletionReport
            : activation.SourceEntryIds.Count > 0 && activation.Kind != ActivationKind.UserTurn ? AgentRunOutputContract.BackgroundOutcome : AgentRunOutputContract.ConversationResponse,
            configuration: configuration),
        new AgentRunModelPin("synthetic", "synthetic", "synthetic", null), 3, Now);

    private sealed class Diagnostics : IDiagnosticIdSource { public Guid NewId() => Guid.NewGuid(); }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);
        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal IDbContextFactory<AgentCoreDbContext>? Contexts => _factory;
        internal ICoreEventStore CoreEvents => _factory is null ? ((InMemoryMemoryStore)Memory).CoreEvents : new SqliteCoreEventStore(_factory);
        private string? _path;
        private Factory? _factory;
        private IAgentInstanceStore? _continuationInstances;
        public IMemoryStore Memory { get; private set; } = null!;
        public IAgentRunStore Runs { get; private set; } = null!;
        public ITriggerStore Triggers { get; private set; } = null!;
        public async Task InsertContinuationOwnerAsync()
        {
            _continuationInstances = _factory is null ? new InMemoryAgentInstanceStore()
                : new SqliteAgentInstanceStore(_factory, new SystemIdGenerator(TimeProvider.System));
            await _continuationInstances.InsertAsync(new AgentInstance(Owner.AgentInstanceId, Definition.Id,
                Definition.Version, Definition.Identity, AgentInstanceLifecycle.Active, Now, Now));
            await ReopenAsync();
        }
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
                Runs = new InMemoryAgentRunStore((InMemoryMemoryStore)Memory, new Diagnostics(), (InMemoryTriggerStore)Triggers,
                    _continuationInstances as InMemoryAgentInstanceStore);
                return;
            }
            var memory = new SqliteMemoryStore(_factory, new FakeTimeProvider(Now));
            await memory.EnsureCreatedAsync();
            Memory = memory;
            Runs = new SqliteAgentRunStore(_factory, memory, new Diagnostics());
            Triggers = new SqliteTriggerStore(_factory);
        }
        public async Task SeedEmbeddedApprovalPredecessorAsync()
        {
            // Prepare a real predecessor database instead of downgrading across
            // later cutovers that correctly require a matched backup restore.
            var sourcePath = _path!;
            var destinationPath = sourcePath + ".embedded-predecessor";
            var destination = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>()
                .UseSqlite($"Data Source={destinationPath};Foreign Keys=True").Options);
            await using (var db = destination.CreateDbContext())
            {
                await db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync(BeforeEmbeddedRetirement);
                await db.Database.OpenConnectionAsync();
                var connection = (SqliteConnection)db.Database.GetDbConnection();
                await using (var attach = connection.CreateCommand())
                {
                    attach.CommandText = "ATTACH DATABASE $path AS fixtureSource";
                    attach.Parameters.AddWithValue("$path", sourcePath); await attach.ExecuteNonQueryAsync();
                }
                async Task<HashSet<string>> Columns(string schema, string table)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"PRAGMA {schema}.table_info(\"{table}\")";
                    var columns = new HashSet<string>(StringComparer.Ordinal);
                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
                    return columns;
                }
                // Only this test-created admission graph; no account, tenant,
                // approval or customer state is copied and no guard is disabled.
                foreach (var table in new[] { "AgentInstances", "Sessions", "ConversationSnapshots", "ConversationEntries", "Activations", "ActivationSourceEntries", "AgentRuns" })
                {
                    var columns = await Columns("main", table);
                    columns.IntersectWith(await Columns("fixtureSource", table));
                    if (columns.Count == 0) continue;
                    var names = string.Join(",", columns.Order(StringComparer.Ordinal).Select(name => "\"" + name + "\""));
                    await using var insert = connection.CreateCommand();
                    insert.CommandText = $"INSERT INTO \"{table}\" ({names}) SELECT {names} FROM fixtureSource.\"{table}\"";
                    await insert.ExecuteNonQueryAsync();
                }
            }
            using (var old = new SqliteConnection($"Data Source={sourcePath}")) SqliteConnection.ClearPool(old);
            _path = destinationPath; _factory = destination;
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(sourcePath + suffix);
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
