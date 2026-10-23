using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

// Native storage identity/CAS contract. Tenant actor authorization is qualified separately.
public sealed class AgentSkillOwnerIdentityTests
{
    private sealed class Contexts(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);
        public ValueTask<AgentCoreDbContext> CreateDbContextAsync(CancellationToken ct = default) => ValueTask.FromResult(CreateDbContext());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Skill_identity_is_unique_per_owner_and_reusable_across_owners(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), "skill-owner-identity-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var now = DateTimeOffset.Parse("2026-10-08T00:00:00Z"); var ids = new SystemIdGenerator(TimeProvider.System);
            IAgentInstanceStore instances;
            if (sqlite)
            {
                var contexts = new Contexts(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
                await new SqliteMemoryStore(contexts, TimeProvider.System).EnsureCreatedAsync();
                instances = new SqliteAgentInstanceStore(contexts, ids);
            }
            else instances = new InMemoryAgentInstanceStore();
            var first = Guid.NewGuid(); var second = Guid.NewGuid(); var skillId = Guid.NewGuid().ToString("D");
            foreach (var owner in new[] { first, second })
            {
                await instances.InsertAsync(new(owner, "field-guide", 1, new("Guide", "role", "desc", "tone"), AgentInstanceLifecycle.Active, now, now));
                await instances.MutateSkillsAsync(new(owner, 1, InstanceSkill: new(skillId, owner, "Review", "Review evidence", "Original", SkillProjection.OnDemand, true, [], 1, now, now, SkillAuthor.Admin)));
            }
            var original = Assert.Single((await instances.ReadSkillsAsync(first)).InstanceSkills);
            await Assert.ThrowsAsync<AgentCoreException>(() => instances.MutateSkillsAsync(new(first, 2, InstanceSkill: original)).AsTask());
            Assert.Equal(2, (await instances.FindAsync(first))!.Revision);
            await instances.MutateSkillsAsync(new(first, 2, InstanceSkill: original with { Procedure = "Updated", Revision = 2 }, ExpectedSkillRevision: 1));
            Assert.Equal("Original", Assert.Single((await instances.ReadSkillsAsync(second)).InstanceSkills).Procedure);
            await instances.MutateSkillsAsync(new(first, 3, DeleteSkillId: skillId, ExpectedSkillRevision: 2));
            Assert.Empty((await instances.ReadSkillsAsync(first)).InstanceSkills);
            Assert.Equal(skillId, Assert.Single((await instances.ReadSkillsAsync(second)).InstanceSkills).SkillId);
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }
}
