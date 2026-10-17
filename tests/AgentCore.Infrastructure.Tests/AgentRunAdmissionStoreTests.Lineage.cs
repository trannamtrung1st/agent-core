using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class AgentRunAdmissionStoreTests
{
    [Fact]
    public async Task Root_migration_refuses_historical_execution_without_inventing_lineage_or_deleting_rows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"activation-root-refusal-{Guid.NewGuid():N}.db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            await using var db = factory.CreateDbContext();
            await db.GetService<IMigrator>().MigrateAsync("20261016233720_TenantSessionOwnership");
            var turn = UserTurn();
            await new SqliteMemoryStore(factory, TimeProvider.System).SaveAsync(turn.Snapshot, 0);
            var a = turn.Run.ActivationId.ToString("D"); var s = turn.Snapshot.SessionId.ToString("D");
            var agent = Owner.AgentInstanceId.ToString("D"); var profile = Owner.ProfileId.ToString("D");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Activations (ActivationId, SessionId, DedupeKey, AdmissionHash, AgentInstanceId, ProfileId, PayloadJson, AdmittedAtUtc) VALUES ({a}, {s}, 'retained', 'retained', {agent}, {profile}, '{{}}', 0)");
            var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
            Assert.Contains("Execution_history_requires_explicit_reset", error.Message);
            Assert.Equal(1, await db.Activations.CountAsync());
            Assert.Equal(1, await db.Sessions.CountAsync());
            Assert.Equal("20261016233720_TenantSessionOwnership", (await db.Database.GetAppliedMigrationsAsync()).Last());
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            SqliteConnection.ClearPool(connection);
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Continuation_cannot_forge_another_root_or_parent_and_rejection_leaves_no_graph(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var parent = await ParentTurn(f);
        var other = await ParentTurn(f);
        foreach (var wrongRoot in new[] { true, false })
        {
            var child = BackgroundTurn(parent);
            var a = child.Run.Admission.Activation;
            var forged = new Activation(a.ActivationId, a.SessionId, a.Kind, a.SourceEntryIds, a.SourceEventId,
                a.TriggerOccurrenceId, a.SourceSessionId, a.SourceAgentRunId, a.DedupeKey, a.AdmittedAtUtc,
                wrongRoot ? other.ActivationId : a.RootActivationId, wrongRoot ? a.ParentActivationId : other.ActivationId);
            var run = AgentRun.Create(child.Run.AgentRunId, Owner,
                new(forged, Definition.Id, Definition.Version, Definition.Identity, child.Run.ResponseId, AgentRunOutputContract.BackgroundOutcome),
                child.Run.PinnedModel, 3, Now);
            Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() =>
                f.Runs.AdmitImmediateAsync(child.Snapshot, run, parent.Claim!.Generation).AsTask())).Code);
            Assert.Null(await f.Memory.LoadAsync(child.Snapshot.SessionId));
            Assert.Null(await f.Runs.GetAsync(Owner, run.AgentRunId));
        }
        await f.ReopenAsync();
        Assert.Equal(parent.ActivationId, (await f.Runs.GetAsync(Owner, parent.AgentRunId))!.Admission.Activation.RootActivationId);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Completion_batch_cannot_merge_independent_roots_and_failure_preserves_pending_receipts(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var firstParent = await ParentTurn(f);
        var first = BackgroundTurn(firstParent);
        await f.Runs.AdmitImmediateAsync(first.Snapshot, first.Run, firstParent.Claim!.Generation);
        await CompleteChild(f, first, false);
        await CompleteParent(f, firstParent);

        var snapshot = (await f.Memory.LoadAsync(firstParent.SessionId))!;
        var entry = new ConversationEntry(Guid.NewGuid(), snapshot.DurableLastEntrySequence + 1, null,
            ConversationRole.User, "Independent request", null, EntryStatus.Completed, SessionMode.Text, 0, 19, Now);
        var secondSnapshot = snapshot with { Revision = snapshot.Revision + 1, Entries = snapshot.Entries.Append(entry).ToArray() };
        var secondRun = NewRun(Activation.Start(Guid.NewGuid(), snapshot.SessionId, ActivationKind.UserTurn,
            [entry.EntryId], null, null, null, null, "second-root", Now));
        await f.Runs.AdmitAsync(secondSnapshot, snapshot.Revision, secondRun);
        secondRun = await Claim(f, secondRun);
        var second = BackgroundTurn(secondRun);
        await f.Runs.AdmitImmediateAsync(second.Snapshot, second.Run, secondRun.Claim!.Generation);
        await CompleteChild(f, second, false);
        await CompleteParent(f, secondRun);
        snapshot = (await f.Memory.LoadAsync(firstParent.SessionId))!;
        var report = NewRun(new Activation(Guid.NewGuid(), snapshot.SessionId, ActivationKind.BackgroundCompleted, [],
            first.Run.AgentRunId, null, first.Run.SessionId, first.Run.AgentRunId, "mixed-roots", Now,
            first.Run.Admission.Activation.RootActivationId, first.Run.ActivationId, "{}"));
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.AdmitCompletionReportBatchAsync(
            snapshot with { Revision = snapshot.Revision + 1 }, snapshot.Revision, report,
            [first.Run.AgentRunId, second.Run.AgentRunId]).AsTask())).Code);
        await f.ReopenAsync();
        Assert.Null(await f.Runs.GetAsync(Owner, report.AgentRunId));
        var pending = await f.Runs.ListCompletionInboxAsync(Owner, snapshot.SessionId, 10, Now);
        Assert.Equal(2, pending.Count);
        Assert.All(pending, item => Assert.Equal(CompletionInboxStatus.Pending, item.Status));
        Assert.Equal(snapshot.Revision, (await f.Memory.LoadAsync(snapshot.SessionId))!.Revision);
    }

    [Fact]
    public async Task Sqlite_lineage_constraints_and_payload_checks_reject_corruption()
    {
        await using var f = await Fixture.CreateAsync(true);
        var parent = await ParentTurn(f);
        var other = await ParentTurn(f);
        var child = BackgroundTurn(parent);
        await f.Runs.AdmitImmediateAsync(child.Snapshot, child.Run, parent.Claim!.Generation);
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var root = other.ActivationId.ToString("D"); var childActivation = child.Run.ActivationId.ToString("D");
            var runId = child.Run.AgentRunId.ToString("D");
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Activations SET RootActivationId = {root} WHERE ActivationId = {childActivation}"));
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AgentRuns SET RootActivationId = {root} WHERE AgentRunId = {runId}"));
            var row = await db.AgentRuns.SingleAsync(r => r.AgentRunId == runId);
            var payload = JsonNode.Parse(row.PayloadJson)!;
            payload["admission"]!["activation"]!["rootActivationId"] = root;
            row.PayloadJson = payload.ToJsonString(); await db.SaveChangesAsync();
        }
        Assert.Equal("SessionPersistenceUnavailable", (await Assert.ThrowsAsync<AgentCoreException>(() => f.Runs.GetAsync(Owner, child.Run.AgentRunId).AsTask())).Code);
    }
}
