using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class ScopedConfigurationMigrationTests
{
    [Fact]
    public async Task Populated_cutover_preserves_legacy_choices_and_run_budget_or_fails_closed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scoped-migration-{Guid.NewGuid():N}.db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        var now = DateTimeOffset.UtcNow; var owner = new AgentRunOwner(Guid.NewGuid(), Guid.NewGuid());
        var definition = new AgentDefinition(1, "migration", 1, new("Alex", "Assistant", "Practice", "Calm"), ["Practice"], "Original instructions",
            new("acknowledgeThenContinue", true, true), new("concise", true, "en", 256), new(false, 8000, 30000, 1, []), new(false, "default", 1),
            new("primary-llm", "primary-stt", "primary-tts"), new Dictionary<string,string>());
        var snapshot = new SessionSnapshot(1, Guid.NewGuid(), 1, definition, SessionMode.Text, null, SessionStatus.Created, [], "", 0, null,
            owner.ProfileId, now, now, owner.AgentInstanceId, PinnedPersona: definition.Identity, ModelSelection: new("synthetic", "synthetic", "synthetic", ModelSelectionSource.AgentDefault, "high"));
        await MigrationSessionSeed.CopyPersistedSessionAsync(factory, snapshot, "20261009160526_ExecutionBudgets");
        var budget = ExecutionBudgetPolicy.Resolve(ExecutionBudgetClass.InteractiveBrowser,
            new(InteractiveBrowser: ExecutionBudgetProfile.For(ExecutionBudgetClass.InteractiveBrowser, ExecutionBudgetPreset.Extended)), null)
            with { RequestedCleanup = true, CleanupIntent = new(true, false) };
        AgentRun NewRun(int version) => AgentRun.Create(Guid.NewGuid(), owner,
            new(new(Guid.NewGuid(), snapshot.SessionId, ActivationKind.Initiative, [], Guid.NewGuid(), null, null, null, Guid.NewGuid().ToString(), now),
                definition.Id, version, definition.Identity, Guid.NewGuid(), AgentRunOutputContract.ConversationResponse, budget), new("synthetic", "synthetic", "synthetic", null), 3, now);
        var good = NewRun(1); var bad = NewRun(2);
        var generation = Guid.NewGuid(); good = good.TakeClaim(generation, now, now.AddMinutes(1));
        good = good.SaveCheckpoint(good.Revision, generation, new("{}", 7, 100, 500000, 100000), null, now);
        good = good.Fail(good.Revision, generation, "temporary", "Retry safely", true, now, now.AddSeconds(5));
        await using (var db = factory.CreateDbContext())
        {
            foreach (var run in new[] {good, bad})
            {
                var row = AgentRunStoreMapping.ToRecord(run);
                var json = JsonNode.Parse(row.PayloadJson)!; json["admission"]!.AsObject().Remove("configuration"); row.PayloadJson = json.ToJsonString();
                db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(snapshot, run));
                db.AgentRuns.Add(row);
            }
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentInstances (InstanceId, DefinitionId, ActiveVersion, PersonaJson, Lifecycle, CreatedAtUtc, UpdatedAtUtc, Revision, PersonaRevision) VALUES ({owner.AgentInstanceId.ToString("D")}, {definition.Id}, 1, {JsonSerializer.Serialize(definition.Identity)}, 0, {now.ToUnixTimeMilliseconds()}, {now.ToUnixTimeMilliseconds()}, 1, 1)");
            foreach (var (enabled, revision) in new[] {(true, 1L), (false, 1L), (true, 2L), (false, 3L), (true, 0L), (false, -1L)})
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentDefinitionSkillStates (AgentInstanceId, DefinitionSkillId, Enabled, Revision, UpdatedAtUtc) VALUES ({owner.AgentInstanceId.ToString("D")}, {Guid.NewGuid().ToString("D")}, {enabled}, {revision}, {now.ToUnixTimeMilliseconds()})");
            await db.Database.MigrateAsync();
        }
        await using (var reopened = factory.CreateDbContext())
        {
            var persistedSession = await reopened.Snapshots.SingleAsync();
            Assert.True(persistedSession.ModelHasExplicitReasoningEffort);
            Assert.Equal("high", persistedSession.ModelReasoningEffort);
            var states = await reopened.AgentDefinitionSkillStates.AsNoTracking().ToArrayAsync();
            Assert.Equal(2, states.Count(s => s.Revision == 1));
            Assert.All(states.Where(s => s.Revision == 1), s => Assert.Null(s.EnabledOverride));
            Assert.True(states.Single(s => s.Revision == 2).EnabledOverride);
            Assert.False(states.Single(s => s.Revision == 3).EnabledOverride);
            Assert.True(states.Single(s => s.Revision == 0).EnabledOverride);
            Assert.False(states.Single(s => s.Revision == -1).EnabledOverride);
            Assert.False(reopened.Database.HasPendingModelChanges());
            var restored = AgentRunStoreMapping.ToDomain(await reopened.AgentRuns.SingleAsync(r => r.AgentRunId == good.AgentRunId.ToString("D")));
            Assert.Equal("Original instructions", restored.Admission.Configuration!.Definition.SystemInstructions);
            Assert.Equal(AgentRunStatus.WaitingToRetry, restored.Status); Assert.Equal(7, restored.Checkpoint!.StepCount); Assert.Equal(100000, restored.Checkpoint.ActiveExecutionMs);
            Assert.Equal(1, restored.DefinitionVersion); Assert.Equal(budget, restored.Admission.ExecutionBudget); Assert.Equal(good.PinnedModel.CatalogKey, restored.PinnedModel.CatalogKey); Assert.Equal(good.PinnedModel.ProviderAlias, restored.PinnedModel.ProviderAlias); Assert.Equal(good.PinnedModel.ModelId, restored.PinnedModel.ModelId);
            var rejected = AgentRunStoreMapping.ToDomain(await reopened.AgentRuns.SingleAsync(r => r.AgentRunId == bad.AgentRunId.ToString("D")));
            Assert.Equal(AgentRunStatus.Failed, rejected.Status); Assert.Equal("configuration-migration-unavailable", rejected.Failure!.Code);
            Assert.Equal(budget, rejected.Admission.ExecutionBudget); Assert.Null(rejected.Claim);
        }
    }
    [Fact]
    public async Task Already_applied_cutover_repairs_only_untouched_skill_states_and_preserves_integrated_schema()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scoped-repair-{Guid.NewGuid():N}.db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync("20261010082055_SessionReasoningPreference");
            var instance = Guid.NewGuid().ToString("D");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentInstances (InstanceId, DefinitionId, ActiveVersion, PersonaJson, Lifecycle, CreatedAtUtc, UpdatedAtUtc, Revision, PersonaRevision) VALUES ({instance}, 'repair', 1, '{{}}', 'Active', 0, 0, 1, 1)");
            foreach (var (enabled, revision) in new[] { (true, 1L), (false, 1L), (true, 2L), (false, 3L), (true, 0L), (false, -1L) })
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentDefinitionSkillStates (AgentInstanceId, DefinitionSkillId, EnabledOverride, Revision, UpdatedAtUtc) VALUES ({instance}, {Guid.NewGuid().ToString("D")}, {enabled}, {revision}, 0)");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO BrowserPrivacy (Id, Revision, PolicyJson) VALUES (1, 7, {"{}"})");
            await db.Database.MigrateAsync();
        }
        await using var reopened = factory.CreateDbContext();
        Assert.False(reopened.Database.HasPendingModelChanges());
        var states = await reopened.AgentDefinitionSkillStates.AsNoTracking().ToArrayAsync();
        Assert.All(states.Where(s => s.Revision == 1), s => Assert.Null(s.EnabledOverride));
        Assert.True(states.Single(s => s.Revision == 2).EnabledOverride);
        Assert.False(states.Single(s => s.Revision == 3).EnabledOverride);
        Assert.True(states.Single(s => s.Revision == 0).EnabledOverride);
        Assert.False(states.Single(s => s.Revision == -1).EnabledOverride);
        Assert.Equal(7, (await reopened.BrowserPrivacy.SingleAsync()).Revision);
        Assert.Empty(await reopened.AutomationTriggers.ToArrayAsync());
        Assert.Empty(await reopened.AgentInstanceResources.ToArrayAsync());
    }

    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
