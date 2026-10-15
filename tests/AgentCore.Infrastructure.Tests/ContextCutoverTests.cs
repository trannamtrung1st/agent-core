using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class ContextCutoverTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static AgentDefinition Definition => new(2, "cutover", 1, new("Alex", "Assistant", "Practice", "Calm"), ["Practice"], "Owner authored behavior",
        new("acknowledgeThenContinue", true, true), new("concise", true, "en", 256), new(false, 8000, 30000, 1, []),
        new(false, "default", 1), new("primary-llm", null, null), new Dictionary<string, string>());

    [Fact]
    public async Task Explicit_conversion_preserves_versions_data_and_backup_and_rejects_old_input()
    {
        var path = Path.Combine(Path.GetTempPath(), $"context-{Guid.NewGuid():N}.db"); var backup = path + ".backup";
        try
        {
            await using (var db = Db(path))
            {
                await db.Database.MigrateAsync();
                db.AgentDefinitionPublications.Add(new() { DefinitionId = "cutover", Version = 1, PayloadJson = Old(JsonSerializer.Serialize(Definition, Json)), SourceDraftRevision = 3, MetadataRevision = 7 });
                db.AgentDefinitionDrafts.Add(new() { DraftId = Guid.NewGuid().ToString("D"), DefinitionId = "cutover", Revision = 9, CandidateJson = Old(JsonSerializer.Serialize(AgentDefinitionCandidate.FromDefinition(Definition), Json)) });
                db.AgentInstances.Add(new() { InstanceId = Guid.NewGuid().ToString("D"), DefinitionId = "cutover", ActiveVersion = 1,
                    PersonaJson = JsonSerializer.Serialize(Definition.Identity, Json), Lifecycle = "Active", Revision = 11,
                    SettingsOverridesJson = "{\"systemInstructions\":{\"value\":\"Local behavior\"}}" });
                await db.SaveChangesAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => ContextCutover.ValidateAsync(db));
            }
            Assert.Equal(3, await ContextCutover.MigrateAsync(path, backup));
            await using (var db = Db(path))
            {
                await ContextCutover.ValidateAsync(db);
                var publication = await db.AgentDefinitionPublications.SingleAsync();
                var definition = JsonSerializer.Deserialize<AgentDefinition>(publication.PayloadJson, Json)!;
                Assert.Equal(2, definition.SchemaVersion); Assert.Equal(1, definition.Version);
                Assert.Equal(Definition.AgentInstructions, definition.AgentInstructions);
                Assert.Equal(7, publication.MetadataRevision); Assert.Equal(3, publication.SourceDraftRevision);
                Assert.Equal(9, (await db.AgentDefinitionDrafts.SingleAsync()).Revision);
                var instance = await db.AgentInstances.SingleAsync(); Assert.Equal(11, instance.Revision);
                Assert.Equal("Local behavior", JsonSerializer.Deserialize<InstanceSettingsOverrides>(instance.SettingsOverridesJson!, Json)!.AgentInstructions!.Value);
            }
            await using (var original = Db(backup))
                Assert.Contains("systemInstructions", (await original.AgentDefinitionPublications.SingleAsync()).PayloadJson);
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AgentDefinition>(Old(JsonSerializer.Serialize(Definition, Json)), Json));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<InstanceSettingsOverrides>("{\"systemInstructions\":{\"value\":\"old\"}}", Json));
        }
        finally { File.Delete(path); File.Delete(backup); }
    }

    [Fact]
    public async Task Active_retired_checkpoint_blocks_conversion_transaction_without_discarding_receipts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"context-block-{Guid.NewGuid():N}.db"); var backup = path + ".backup";
        const string historical = "{\"Phase\":\"model-turn\",\"Messages\":[{\"Role\":\"Tool\",\"Text\":\"retained receipt\"}]}";
        try
        {
            await using (var db = Db(path))
            {
                await db.Database.MigrateAsync();
                var now = DateTimeOffset.UtcNow;
                var snapshot = AgentRunTestFixtures.Snapshot(new(Guid.NewGuid(), Guid.NewGuid()), Definition, now);
                var memory = new SqliteMemoryStore(new Factory(path), TimeProvider.System);
                await memory.SaveAsync(snapshot, 0);
                var run = AgentRunTestFixtures.Run(snapshot, now).TakeClaim(Guid.NewGuid(), now, now.AddMinutes(1));
                run = run.SaveCheckpoint(run.Revision, run.Claim!.Generation, new(historical, 1, 0, 1000), null, now);
                db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(snapshot, run));
                db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(run));
                db.AgentDefinitionPublications.Add(new() { DefinitionId = "cutover", Version = 1, PayloadJson = Old(JsonSerializer.Serialize(Definition, Json)) });
                await db.SaveChangesAsync();
            }
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ContextCutover.MigrateAsync(path, backup));
            Assert.Contains("drained or explicitly cancelled", error.Message);
            await using var reopened = Db(path);
            Assert.Contains("systemInstructions", (await reopened.AgentDefinitionPublications.SingleAsync()).PayloadJson);
            Assert.Contains("retained receipt", (await reopened.AgentRuns.SingleAsync()).PayloadJson);
            Assert.True(File.Exists(backup));
        }
        finally { File.Delete(path); File.Delete(backup); }
    }

    [Fact]
    public async Task Sealed_run_configuration_hashes_and_inert_receipts_survive_conversion_and_reopen()
    {
        var path = Path.Combine(Path.GetTempPath(), $"context-sealed-{Guid.NewGuid():N}.db"); var backup = path + ".backup"; var secondBackup = path + ".second";
        const string receipt = "{\"Phase\":\"model-turn\",\"Messages\":[{\"Role\":\"System\",\"Text\":\"retired instruction\"},{\"Role\":\"Tool\",\"Text\":\"historical receipt\"}]}";
        Guid runId;
        try
        {
            await using (var db = Db(path))
            {
                await db.Database.MigrateAsync();
                var now = DateTimeOffset.UtcNow;
                var snapshot = AgentRunTestFixtures.Snapshot(new(Guid.NewGuid(), Guid.NewGuid()), Definition, now);
                var memory = new SqliteMemoryStore(new Factory(path), TimeProvider.System);
                await memory.SaveAsync(snapshot, 0);
                var run = AgentRunTestFixtures.Run(snapshot, now).TakeClaim(Guid.NewGuid(), now, now.AddMinutes(1));
                run = run.SaveCheckpoint(run.Revision, run.Claim!.Generation, new(receipt, 2, 31, 1000), null, now);
                run = run.Complete(run.Revision, run.Claim!.Generation, "Sealed original outcome", now, outcomeEntryId: Guid.NewGuid());
                runId = run.AgentRunId;
                var row = AgentRunStoreMapping.ToRecord(run);
                var payload = JsonNode.Parse(row.PayloadJson)!;
                payload["admission"]!["configuration"]!["definition"] = JsonNode.Parse(Old(JsonSerializer.Serialize(Definition, Json)));
                payload["admission"]!["configuration"]!["configurationHash"] = "retired-hash";
                row.PayloadJson = payload.ToJsonString();
                var activation = AgentRunStoreMapping.ToActivationRecord(snapshot, run); activation.AdmissionHash = "retired-admission-hash";
                db.Activations.Add(activation); db.AgentRuns.Add(row);
                var session = await db.Sessions.SingleAsync(); session.DefinitionJson = Old(session.DefinitionJson);
                await db.SaveChangesAsync();
            }
            Assert.Equal(2, await ContextCutover.MigrateAsync(path, backup));
            await using (var reopened = Db(path))
            {
                var row = await reopened.AgentRuns.SingleAsync();
                var run = AgentRunStoreMapping.ToDomain(row);
                Assert.Equal(runId, run.AgentRunId); Assert.Equal(AgentRunStatus.Completed, run.Status);
                Assert.Equal("Sealed original outcome", run.Result!.Text); Assert.Equal(2, run.Checkpoint!.StepCount);
                Assert.Equal(receipt, run.Checkpoint.PayloadJson);
                Assert.False(AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out _));
                Assert.Equal(Definition.AgentInstructions, run.Admission.Configuration!.Definition.AgentInstructions);
                Assert.Equal(run.Admission.Configuration.ConfigurationHash, JsonNode.Parse(row.PayloadJson)!["admission"]!["configuration"]!["configurationHash"]!.GetValue<string>());
                var session = await reopened.Sessions.Include(r => r.Snapshot).Include(r => r.Entries).SingleAsync();
                Assert.Equal(AgentRunStoreMapping.AdmissionHash(SqliteMemoryStore.ToSnapshot(session, session.Entries), run), (await reopened.Activations.SingleAsync()).AdmissionHash);
                await ContextCutover.ValidateAsync(reopened);
            }
            Assert.Equal(0, await ContextCutover.MigrateAsync(path, secondBackup));
        }
        finally { File.Delete(path); File.Delete(backup); File.Delete(secondBackup); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conversion_rehashes_original_skill_admission_and_replay_returns_sealed_run(bool composerError)
    {
        var path = Path.Combine(Path.GetTempPath(), $"context-skills-{Guid.NewGuid():N}.db");
        var backup = path + ".backup";
        var secondBackup = path + ".second";
        var now = DateTimeOffset.UtcNow;
        var snapshot = AgentRunTestFixtures.Snapshot(new(Guid.NewGuid(), Guid.NewGuid()), Definition, now);
        var user = snapshot.Entries.Single() with { Text = "/definition:explicit", Parts = [new("invocation", InvocationKind: "skill", SkillKey: "definition:explicit")] };
        snapshot = snapshot with { Entries = [user] };
        EffectiveSkill[] skills = [
            new("definition:always", SkillOrigin.Definition, "always", "Always", "Always", "Always procedure", SkillProjection.Always, [], []),
            new("definition:explicit", SkillOrigin.Definition, "explicit", "Explicit", "Explicit", "Explicit procedure", SkillProjection.OnDemand, [], []),
            new("definition:late", SkillOrigin.Definition, "late", "Late", "Late", "Late procedure", SkillProjection.OnDemand, [], [])
        ];
        var admitted = AgentRunAdmissionFactory.ForAcceptedUserBatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            snapshot, [user], now, skills, composerInput: new(["definition:explicit"], [], now, composerError ? "Unavailable reference" : null));
        try
        {
            var factory = new Factory(path);
            var memory = new SqliteMemoryStore(factory, TimeProvider.System);
            await using (var db = Db(path))
            {
                await db.Database.MigrateAsync();
                await memory.SaveAsync(snapshot, 0);
                var run = admitted.TakeClaim(Guid.NewGuid(), now, now.AddMinutes(1));
                run = run.AdmitActiveSkills(run.Revision, run.Claim!.Generation, ["definition:late"], now);
                run = run.Complete(run.Revision, run.Claim!.Generation, "Original sealed outcome", now, outcomeEntryId: Guid.NewGuid());
                var row = AgentRunStoreMapping.ToRecord(run);
                var payload = JsonNode.Parse(row.PayloadJson)!;
                payload["admission"]!["configuration"]!["definition"] = JsonNode.Parse(Old(JsonSerializer.Serialize(Definition, Json)));
                row.PayloadJson = payload.ToJsonString();
                var activation = AgentRunStoreMapping.ToActivationRecord(snapshot, admitted);
                activation.AdmissionHash = "retired-admission-hash";
                db.Activations.Add(activation); db.AgentRuns.Add(row);
                var session = await db.Sessions.SingleAsync(); session.DefinitionJson = Old(session.DefinitionJson);
                await db.SaveChangesAsync();
            }
            Assert.Equal(2, await ContextCutover.MigrateAsync(path, backup));
            var store = new SqliteAgentRunStore(factory, memory, new SystemDiagnosticIdSource());
            var replay = await store.AdmitAsync(snapshot, 0, admitted);
            Assert.False(replay.Created);
            Assert.Equal(admitted.AgentRunId, replay.Run.AgentRunId);
            Assert.Equal("Original sealed outcome", replay.Run.Result!.Text);
            Assert.Equal(admitted.ActiveSkillKeys.Append("definition:late"), replay.Run.ActiveSkillKeys);
            Assert.Equal(1, replay.Run.SkillLoadCount);
            Assert.Equal(0, await ContextCutover.MigrateAsync(path, secondBackup));
        }
        finally { File.Delete(path); File.Delete(backup); File.Delete(secondBackup); }
    }

    [Fact]
    public async Task Invalid_later_payload_rolls_back_earlier_definition_conversion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"context-rollback-{Guid.NewGuid():N}.db"); var backup = path + ".backup";
        try
        {
            await using (var db = Db(path))
            {
                await db.Database.MigrateAsync();
                db.AgentDefinitionPublications.Add(new() { DefinitionId = "cutover", Version = 1, PayloadJson = Old(JsonSerializer.Serialize(Definition, Json)) });
                var mixed = JsonSerializer.SerializeToNode(AgentDefinitionCandidate.FromDefinition(Definition), Json)!.AsObject();
                mixed["systemInstructions"] = "retired alias";
                db.AgentDefinitionDrafts.Add(new() { DraftId = Guid.NewGuid().ToString("D"), DefinitionId = "cutover", Revision = 5, CandidateJson = mixed.ToJsonString() });
                await db.SaveChangesAsync();
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => ContextCutover.MigrateAsync(path, backup));
            await using var reopened = Db(path);
            Assert.Contains("systemInstructions", (await reopened.AgentDefinitionPublications.SingleAsync()).PayloadJson);
            Assert.Equal(5, (await reopened.AgentDefinitionDrafts.SingleAsync()).Revision);
        }
        finally { File.Delete(path); File.Delete(backup); }
    }

    private static string Old(string json)
    { var obj = JsonNode.Parse(json)!.AsObject(); obj["schemaVersion"] = 1; var value = obj["agentInstructions"]; obj.Remove("agentInstructions"); obj["systemInstructions"] = value; return obj.ToJsonString(); }
    private static AgentCoreDbContext Db(string path) => new(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
    private sealed class Factory(string path) : IDbContextFactory<AgentCoreDbContext> { public AgentCoreDbContext CreateDbContext() => Db(path); }
}
