using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
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
        await f.Runs.AdmitAsync(first.Snapshot, 0, first.Run);
        var candidates = Enumerable.Range(0, 6).Select(_ => BackgroundTurn(parentSessionId, parentRunId)).ToArray();
        var results = await Task.WhenAll(candidates.Select(candidate => f.Runs.AdmitAsync(candidate.Snapshot, 0, candidate.Run).AsTask()));
        Assert.All(results, result => { Assert.False(result.Created); Assert.Equal(first.Run.AgentRunId, result.Run.AgentRunId); });
        foreach (var candidate in candidates) Assert.Null(await f.Memory.LoadAsync(candidate.Snapshot.SessionId));
        var child = await f.Memory.LoadAsync(first.Snapshot.SessionId);
        Assert.Equal(SessionOriginKind.ImmediateBackground, child!.Origin.Kind);
        Assert.Equal(first.Run.AgentRunId, child.Origin.InitialBackgroundAgentRunId);
        Assert.Equal(SessionSurface.BackgroundWork, child.Surfaces);
        var changed = BackgroundTurn(parentSessionId, parentRunId, "Different objective");
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitAsync(changed.Snapshot, 0, changed.Run).AsTask())).Code);
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
            Assert.Contains("foundation schema is incomplete", error.Message, StringComparison.Ordinal);
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
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitAsync(missing.Snapshot, 0, missing.Run).AsTask())).Code);
        Assert.Null(await f.Memory.LoadAsync(missing.Snapshot.SessionId));
        var parent = await ParentTurn(f);
        var child = BackgroundTurn(parent.SessionId, parent.AgentRunId);
        var foreign = new AgentRunOwner(Guid.NewGuid(), Owner.ProfileId);
        var foreignRun = AgentRun.Create(child.Run.AgentRunId, foreign, child.Run.Admission, child.Run.PinnedModel, 3, Now);
        var foreignSnapshot = child.Snapshot with { AgentInstanceId = foreign.AgentInstanceId };
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitAsync(foreignSnapshot, 0, foreignRun).AsTask())).Code);
        Assert.Null(await f.Memory.LoadAsync(child.Snapshot.SessionId));
        await f.Runs.ApplyAsync(Owner, parent.AgentRunId, new AgentRunCommand.RequestCancellation(parent.Revision, Now, null));
        Assert.Equal("ValidationError", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitAsync(child.Snapshot, 0, child.Run).AsTask())).Code);
        Assert.Null(await f.Memory.LoadAsync(child.Snapshot.SessionId));
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
                f.Runs = new InMemoryAgentRunStore(memory, new Diagnostics());
            }
            return f;
        }
        public async Task ReopenAsync()
        {
            if (_factory is null)
            {
                Runs = new InMemoryAgentRunStore((InMemoryMemoryStore)Memory, new Diagnostics());
                return;
            }
            var memory = new SqliteMemoryStore(_factory, new FakeTimeProvider(Now));
            await memory.EnsureCreatedAsync();
            Memory = memory;
            Runs = new SqliteAgentRunStore(_factory, memory, new Diagnostics());
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
