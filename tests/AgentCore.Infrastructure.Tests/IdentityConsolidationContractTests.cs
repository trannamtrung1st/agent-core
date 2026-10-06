using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Work;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class IdentityConsolidationContractTests
{
    private static readonly Guid Instance = Guid.NewGuid(), Profile = Guid.NewGuid(), Session = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1791240000000);
    private static StructuredMemoryItem Memory(string subject) => new(Guid.NewGuid(), Session, MemoryKind.Preference,
        MemoryItemStatus.Active, subject, "Use TypeScript for frontend examples.", subject.ToLowerInvariant(),
        new("agentInferred", [], null, Now), Now, Now, MemoryScope.IdentityUser, Instance, Profile);
    private static StructuredMemoryItem Result(StructuredMemoryItem[] sources, string subject = "Frontend examples") =>
        sources[0] with { MemoryId = Guid.NewGuid(), Subject = subject, SubjectKey = subject.ToLowerInvariant(),
            Provenance = sources[0].Provenance with { DerivedFromMemoryIds = sources.Select(s => s.MemoryId).Order().ToArray() } };
    private static ExperienceContent Content => new("Browser write lessons", ["Refreshed page"], [], ["Write succeeded"], [], [], [], ["Check current page state"]);
    private static AgentExperience Experience() => new(Guid.NewGuid(), Instance, Profile, ExperienceSourceKind.Session,
        Guid.NewGuid(), 2, Now, "general-assistant", 7, Guid.NewGuid(), new WorkModelPin("synthetic", "synthetic", "scripted", null), Now, Content);
    private static AgentExperience Result(AgentExperience[] sources) => sources[0] with { ExperienceId = Guid.NewGuid(), SourceKind = ExperienceSourceKind.Consolidation,
        SourceId = Guid.NewGuid(), DerivedFromExperienceIds = sources.Select(s => s.ExperienceId).Order().ToArray() };

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Atomic_memory_reduces_capacity_preserves_lineage_and_retries(bool sqlite)
    {
        await WithStores(sqlite, async (memories, _, _) =>
        {
            var sources = Enumerable.Range(0, 8).Select(i => Memory($"Frontend {i}")).ToArray();
            foreach (var s in sources) await memories.InsertAsync(s);
            var result = Result(sources);
            var saved = await memories.ConsolidateAsync(sources, result);
            Assert.Equal(result.MemoryId, (await memories.ConsolidateAsync(sources, result)).MemoryId);
            Assert.Equal(1, await memories.CountActiveIdentityUserAsync(Instance, Profile));
            foreach (var s in sources) Assert.Equal(MemoryItemStatus.Superseded, (await memories.FindIdentityUserAsync(Instance, Profile, s.MemoryId))!.Status);
            Assert.Equal(8, saved.Provenance.DerivedFromMemoryIds!.Count);
            Assert.Equal(result.MemoryId, Assert.Single(await memories.ListActiveIdentityUserAsync(Instance, Profile)).MemoryId);
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Memory_conflict_and_late_delete_leave_every_source_unchanged(bool sqlite)
    {
        await WithStores(sqlite, async (memories, _, _) =>
        {
            var sources = new[] { Memory("A"), Memory("B") };
            foreach (var s in sources) await memories.InsertAsync(s);
            await memories.InsertAsync(Memory("Frontend examples"));
            await Assert.ThrowsAsync<AgentCoreException>(() => memories.ConsolidateAsync(sources, Result(sources)).AsTask());
            Assert.Equal(3, await memories.CountActiveIdentityUserAsync(Instance, Profile));
            await memories.TombstoneAsync(sources[1] with { Status = MemoryItemStatus.Deleted, Subject = "", Content = "", SubjectKey = "" });
            await Assert.ThrowsAsync<AgentCoreException>(() => memories.ConsolidateAsync(sources, Result(sources, "Unique")).AsTask());
            Assert.Equal(MemoryItemStatus.Active, (await memories.FindIdentityUserAsync(Instance, Profile, sources[0].MemoryId))!.Status);
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Experience_atomicity_visibility_revision_and_maintenance_CAS(bool sqlite)
    {
        await WithStores(sqlite, async (_, experiences, _) =>
        {
            Assert.False((await experiences.MaintenanceSettingsAsync(Instance)).AllowAgentConsolidation);
            await experiences.ConfigureMaintenanceAsync(Instance, 0, true);
            await Assert.ThrowsAsync<AgentCoreException>(() => experiences.ConfigureMaintenanceAsync(Instance, 0, false).AsTask());
            var sources = new[] { Experience(), Experience(), Experience() };
            foreach (var s in sources) await experiences.AdmitAsync(s);
            var result = Result(sources);
            await experiences.ConsolidateAsync(sources, result);
            Assert.Equal(result.ExperienceId, (await experiences.ConsolidateAsync(sources, result)).ExperienceId);
            foreach (var source in sources)
            {
                var historical = (await experiences.GetAsync(Instance, source.ExperienceId))!;
                Assert.Equal(ExperienceVisibility.Superseded, historical.Visibility);
                Assert.NotNull(historical.Content);
                await Assert.ThrowsAsync<AgentCoreException>(() => experiences.SetVisibilityAsync(Instance, source.ExperienceId, historical.Revision, ExperienceVisibility.Eligible).AsTask());
            }
            var racing = new[] { Experience(), Experience() };
            foreach (var s in racing) await experiences.AdmitAsync(s);
            await experiences.SetVisibilityAsync(Instance, racing[1].ExperienceId, 1, ExperienceVisibility.Suppressed);
            await Assert.ThrowsAsync<AgentCoreException>(() => experiences.ConsolidateAsync(racing, Result(racing)).AsTask());
            Assert.Equal(ExperienceVisibility.Eligible, (await experiences.GetAsync(Instance, racing[0].ExperienceId))!.Visibility);
            await experiences.ResetAsync(Instance);
            await Assert.ThrowsAsync<AgentCoreException>(() => experiences.ConsolidateAsync(sources, result).AsTask());
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Invalid_source_sets_owners_scopes_and_kinds_never_partially_mutate(bool sqlite)
    {
        await WithStores(sqlite, async (memories, experiences, _) =>
        {
            var sources = new[] { Memory("A"), Memory("B") };
            foreach (var source in sources) await memories.InsertAsync(source);
            foreach (var selected in new[] { new[] { sources[0] }, new[] { sources[0], sources[0] }, Enumerable.Repeat(sources[0], 9).ToArray() })
                await Assert.ThrowsAsync<AgentCoreException>(() => memories.ConsolidateAsync(selected, Result(selected)).AsTask());
            foreach (var altered in new[] { sources[1] with { OwnerInstanceId = Guid.NewGuid() }, sources[1] with { OwnerProfileId = Guid.NewGuid() },
                sources[1] with { Scope = MemoryScope.User }, sources[1] with { Kind = MemoryKind.Fact } })
                await Assert.ThrowsAsync<AgentCoreException>(() => memories.ConsolidateAsync([sources[0], altered], Result(sources)).AsTask());
            Assert.Equal(2, await memories.CountActiveIdentityUserAsync(Instance, Profile));
            var ex = new[] { Experience(), Experience() };
            foreach (var source in ex) await experiences.AdmitAsync(source);
            await Assert.ThrowsAsync<AgentCoreException>(() => experiences.ConsolidateAsync([ex[0], ex[1] with { AgentInstanceId = Guid.NewGuid() }], Result(ex)).AsTask());
            await experiences.SetVisibilityAsync(Instance, ex[1].ExperienceId, 1, ExperienceVisibility.Deleted);
            await Assert.ThrowsAsync<AgentCoreException>(() => experiences.ConsolidateAsync(ex, Result(ex)).AsTask());
            Assert.Equal(ExperienceVisibility.Eligible, (await experiences.GetAsync(Instance, ex[0].ExperienceId))!.Visibility);
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Exact_Experience_retry_rejects_payload_collision_and_suppressed_result(bool sqlite)
    {
        await WithStores(sqlite, async (_, experiences, _) =>
        {
            var sources = new[] { Experience(), Experience() };
            foreach (var s in sources) await experiences.AdmitAsync(s);
            var result = Result(sources);
            await experiences.ConsolidateAsync(sources, result);
            await Assert.ThrowsAsync<AgentCoreException>(() => experiences.ConsolidateAsync(sources, result with { Content = Content with { Goal = "Altered" } }).AsTask());
            await experiences.SetVisibilityAsync(Instance, result.ExperienceId, result.Revision, ExperienceVisibility.Suppressed);
            await Assert.ThrowsAsync<AgentCoreException>(() => experiences.ConsolidateAsync(sources, result).AsTask());
            Assert.Equal(ExperienceVisibility.Suppressed, (await experiences.GetAsync(Instance, result.ExperienceId))!.Visibility);
        });
    }

    [Fact]
    public async Task Old_SQLite_memory_and_Experience_upgrade_with_empty_lineage_and_default_off_setting()
    {
        var path = Path.Combine(Path.GetTempPath(), $"p910-upgrade-{Guid.NewGuid():N}.db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                var previous = db.Database.GetMigrations().Last(m => !m.Contains("P910"));
                await db.Database.MigrateAsync(previous);
                // Deliberately omit new columns, as a persisted pre-P9.10 row does.
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO StructuredMemories (MemoryId,SessionId,Kind,Status,Subject,Content,SubjectKey,Source,SourceEntryIdsJson,ProvenanceRecordedAtUtc,CreatedAtUtc,UpdatedAtUtc,Scope,OwnerInstanceId,OwnerProfileId) VALUES ({Guid.NewGuid().ToString()}, {Session.ToString()}, {0}, {0}, {"Legacy"}, {"Retained fact"}, {"legacy"}, {"agent_inferred"}, {"[]"}, {Now.ToUnixTimeMilliseconds()}, {Now.ToUnixTimeMilliseconds()}, {Now.ToUnixTimeMilliseconds()}, {1}, {Instance.ToString()}, {Profile.ToString()})");
                var ex = Experience();
                db.Experiences.Add(new() { ExperienceId = ex.ExperienceId.ToString(), AgentInstanceId = Instance.ToString(), ProfileId = Profile.ToString(), SourceKind = 0, SourceId = ex.SourceId.ToString(), ThroughCursor = ex.ThroughCursor, CreatedAtUtc = Now.ToUnixTimeMilliseconds(), PayloadJson = System.Text.Json.JsonSerializer.Serialize(ex), Revision = 1 });
                await db.SaveChangesAsync();
                await db.Database.MigrateAsync();
            }
            var memory = Assert.Single(await new SqliteStructuredMemoryStore(factory).ListActiveIdentityUserAsync(Instance, Profile));
            Assert.Empty(memory.Provenance.DerivedFromMemoryIds!);
            var experiences = new SqliteExperienceStore(factory);
            Assert.False((await experiences.MaintenanceSettingsAsync(Instance)).AllowAgentConsolidation);
            Assert.Equal(ExperienceVisibility.Eligible, Assert.Single(await experiences.ListAsync(Instance, 10)).Visibility);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Full_capacity_is_relieved_without_forgetting_or_mutating_source_history(bool sqlite)
    {
        await WithStores(sqlite, async (memories, _, _) =>
        {
            var all = Enumerable.Range(0, MemoryLimits.MaxActiveItems).Select(i => Memory($"Capacity {i}")).ToArray();
            foreach (var m in all) await memories.InsertAsync(m);
            await Assert.ThrowsAsync<AgentCoreException>(() => memories.InsertAsync(Memory("Overflow")).AsTask());
            await memories.ConsolidateAsync(all.Take(8).ToArray(), Result(all.Take(8).ToArray()));
            Assert.Equal(MemoryLimits.MaxActiveItems - 7, await memories.CountActiveIdentityUserAsync(Instance, Profile));
            await memories.InsertAsync(Memory("New useful preference"));
            foreach (var source in all.Take(8)) Assert.NotEmpty((await memories.FindIdentityUserAsync(Instance, Profile, source.MemoryId))!.Content);
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Concurrent_overlapping_consolidations_admit_one_complete_result(bool sqlite)
    {
        await WithStores(sqlite, async (memories, _, _) =>
        {
            var sources = new[] { Memory("Race A"), Memory("Race B"), Memory("Race C") };
            foreach (var source in sources) await memories.InsertAsync(source);
            var results = await Task.WhenAll(new[] { Result(sources, "First result"), Result(sources, "Second result") }.Select(result => Task.Run(async () => {
                try { await memories.ConsolidateAsync(sources, result); return true; }
                catch (AgentCoreException) { return false; }
            })));
            Assert.Single(results, accepted => accepted);
            Assert.Equal(1, await memories.CountActiveIdentityUserAsync(Instance, Profile));
            foreach (var source in sources) Assert.Equal(MemoryItemStatus.Superseded, (await memories.FindIdentityUserAsync(Instance, Profile, source.MemoryId))!.Status);
        });
    }

    internal static async Task WithStores(bool sqlite, Func<IStructuredMemoryStore, IExperienceStore, IDbContextFactory<AgentCoreDbContext>?, Task> exercise)
    {
        if (!sqlite) { await exercise(new InMemoryStructuredMemoryStore(), new InMemoryExperienceStore(), null); return; }
        var path = Path.Combine(Path.GetTempPath(), $"p910-{Guid.NewGuid():N}.db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await exercise(new SqliteStructuredMemoryStore(factory), new SqliteExperienceStore(factory), factory);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
