using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class SkillPinStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly AgentRunOwner Owner = new(Guid.NewGuid(), Guid.NewGuid());
    private static readonly AgentDefinition Definition = new(1, "skill-guide", 1,
        new("Guide", "Role", "Guidance", "Calm"), [], "Instructions", new("answerNewTurn", true, true),
        new("concise", false, "en", 256), new(false, 8000, 30000, 1, ["longSilence"]),
        new(false, "default", 1), new("primary-llm", null, null), new Dictionary<string, string>());
    private static readonly EffectiveSkill[] Catalog = [
        new("definition:refund.handle", SkillOrigin.Definition, "refund.handle", "Refund", "Guidance", "PINNED_REFUND",
            SkillProjection.OnDemand, [], []),
        new("definition:billing.note", SkillOrigin.Definition, "billing.note", "Billing", "Guidance", "PINNED_BILLING",
            SkillProjection.OnDemand, [], []),
        new("instance:019944af-00d1-7000-8000-0000000000c1", SkillOrigin.Instance, "019944af-00d1-7000-8000-0000000000c1",
            "Local", "Local procedure", "PINNED_LOCAL_PROCEDURE", SkillProjection.OnDemand, ["workspace.read"], [])];

    [Fact]
    public async Task Startup_rejects_legacy_execution_instead_of_synthesizing_a_pin_or_stamping_it()
    {
        await using var f = await Fixture.CreateAsync(true);
        await using var db = f.Factory!.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE ConversationTurnExecutions (ExecutionId TEXT NOT NULL PRIMARY KEY, PinnedSkillCatalogJson TEXT NOT NULL)");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO ConversationTurnExecutions VALUES ('demo', '')");
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => new SqliteMemoryStore(f.Factory!, TimeProvider.System).EnsureCreatedAsync().AsTask());
        Assert.Contains("reset", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM ConversationTurnExecutions").SingleAsync());
    }

    [Fact]
    public async Task Sqlite_reopen_keeps_catalog_skill_loads_and_capabilities_across_lease_recovery()
    {
        await using var f = await Fixture.CreateAsync(true);
        var snapshot = AgentRunTestFixtures.Snapshot(Owner, Definition, Now);
        var run = (await f.Store.AdmitAsync(snapshot, 0, AgentRunTestFixtures.Run(snapshot, Now, Catalog))).Run;
        run = await f.Store.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, Now, Guid.NewGuid(), Now.AddMinutes(5)));
        var generation = run.Claim!.Generation;
        run = await f.Store.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.LoadSkills(run.Revision, Now, generation, ["definition:refund.handle"]));
        run = await f.Store.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.LoadCapabilities(run.Revision, Now, generation, ["workspace.read", "email.search"]));
        var reopened = new SqliteAgentRunStore(f.Factory!, (SqliteMemoryStore)f.Memory, new SystemDiagnosticIdSource());
        var loaded = (await reopened.GetAsync(Owner, run.AgentRunId))!;
        Assert.Equal(Catalog.Select(s => s.Procedure), loaded.PinnedSkillCatalog.Select(s => s.Procedure));
        Assert.Equal(["definition:refund.handle"], loaded.ActiveSkillKeys);
        Assert.Equal(["workspace.read", "email.search"], loaded.LoadedCapabilityIds);
        Assert.Equal(1, loaded.SkillLoadCount); Assert.Equal(1, loaded.CapabilityLoadCount);
        await Assert.ThrowsAsync<AgentCoreException>(() => reopened.ApplyAsync(Owner, run.AgentRunId,
            new AgentRunCommand.LoadCapabilities(run.Revision - 1, Now, generation, ["workspace.write"])).AsTask());
        run = await reopened.ApplyAsync(Owner, loaded.AgentRunId, new AgentRunCommand.Recover(loaded.Revision, Now.AddMinutes(6)));
        run = await reopened.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, Now.AddMinutes(6), Guid.NewGuid(), Now.AddMinutes(11)));
        await Assert.ThrowsAsync<AgentCoreException>(() => reopened.ApplyAsync(Owner, run.AgentRunId,
            new AgentRunCommand.LoadSkills(run.Revision, Now.AddMinutes(7), generation, ["definition:billing.note"])).AsTask());
        run = await reopened.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.RequestCancellation(run.Revision, Now.AddMinutes(7), null));
        await Assert.ThrowsAsync<AgentCoreException>(() => reopened.ApplyAsync(Owner, run.AgentRunId,
            new AgentRunCommand.LoadCapabilities(run.Revision, Now.AddMinutes(7), run.Claim!.Generation, ["workspace.write"])).AsTask());
        loaded = (await reopened.GetAsync(Owner, run.AgentRunId))!;
        Assert.Equal(["workspace.read", "email.search"], loaded.LoadedCapabilityIds);
        Assert.Equal(1, loaded.CapabilityLoadCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_capability_admission_leaves_revision_ids_and_count_unchanged(bool sqlite)
    {
        await using var f = await Fixture.CreateAsync(sqlite);
        var snapshot = AgentRunTestFixtures.Snapshot(Owner, Definition, Now);
        var run = (await f.Store.AdmitAsync(snapshot, 0, AgentRunTestFixtures.Run(snapshot, Now, Catalog))).Run;
        var claim = await f.Store.ApplyAsync(Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, Now, Guid.NewGuid(), Now.AddMinutes(5)));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Store.ApplyAsync(Owner, claim.AgentRunId,
            new AgentRunCommand.LoadCapabilities(claim.Revision, Now, claim.Claim!.Generation, ["workspace.write"]), cancellation.Token).AsTask());
        var unchanged = (await f.Store.GetAsync(Owner, claim.AgentRunId))!;
        Assert.Equal(claim.Revision, unchanged.Revision); Assert.Empty(unchanged.LoadedCapabilityIds); Assert.Equal(0, unchanged.CapabilityLoadCount);
        var admitted = await f.Store.ApplyAsync(Owner, claim.AgentRunId,
            new AgentRunCommand.LoadCapabilities(claim.Revision, Now, claim.Claim!.Generation, ["workspace.read"]));
        Assert.Equal(["workspace.read"], admitted.LoadedCapabilityIds); Assert.Equal(1, admitted.CapabilityLoadCount);
    }

    private sealed class Fixture(string? path, IMemoryStore memory, IAgentRunStore store, SqliteContextFactory? factory) : IAsyncDisposable
    {
        public IMemoryStore Memory { get; } = memory;
        public IAgentRunStore Store { get; } = store;
        public SqliteContextFactory? Factory { get; } = factory;
        public static async Task<Fixture> CreateAsync(bool sqlite)
        {
            if (!sqlite) { var memory = new InMemoryMemoryStore(); return new(null, memory, new InMemoryAgentRunStore(memory, new SystemDiagnosticIdSource()), null); }
            var path = Path.Combine(Path.GetTempPath(), $"agent-core-skill-pin-{Guid.NewGuid():N}.db");
            var factory = new SqliteContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
            var sqliteMemory = new SqliteMemoryStore(factory, TimeProvider.System); await sqliteMemory.EnsureCreatedAsync();
            return new(path, sqliteMemory, new SqliteAgentRunStore(factory, sqliteMemory, new SystemDiagnosticIdSource()), factory);
        }
        public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); if (path is not null) File.Delete(path); return ValueTask.CompletedTask; }
    }
    private sealed class SqliteContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);
        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }
}
