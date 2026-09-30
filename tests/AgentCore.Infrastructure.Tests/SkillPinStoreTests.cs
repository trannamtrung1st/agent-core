using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Work;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class SkillPinStoreTests
{
    [Fact]
    public async Task Sqlite_reopen_keeps_the_pinned_skill_ids()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-skill-pin-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        var now = DateTimeOffset.Parse("2026-09-30T00:00:00Z");
        var sessionId = Guid.Parse("019944af-00d1-7000-8000-0000000000b1");
        var sourceEventId = Guid.Parse("019944af-00d1-7000-8000-0000000000b2");
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var store = new SqliteConversationTurnExecutionStore(factory);
            var created = await store.CreateAsync(Execution(sessionId, sourceEventId, now, ["refund.handle", "order.lookup"]));
            Assert.Equal(ConversationTurnExecutionCreateKind.Created, created.Kind);

            var reopened = new SqliteConversationTurnExecutionStore(factory);
            var loaded = await reopened.GetBySourceEventAsync(sessionId, sourceEventId);
            Assert.Equal(["refund.handle", "order.lookup"], loaded!.PinnedActiveSkillIds);

            var claimed = await reopened.TryClaimAsync(
                loaded.ExecutionId,
                Guid.Parse("019944af-00d1-7000-8000-0000000000b9"),
                now.AddMinutes(1),
                now.AddMinutes(6));
            Assert.Equal(["refund.handle", "order.lookup"], claimed!.PinnedActiveSkillIds);
            var admitted = await reopened.AdmitActiveSkillsAsync(
                claimed.ExecutionId,
                claimed.Revision,
                Guid.Parse("019944af-00d1-7000-8000-0000000000b9"),
                ["billing.note"],
                now.AddMinutes(2));
            Assert.Equal(["refund.handle", "order.lookup", "billing.note"], admitted.PinnedActiveSkillIds);
            Assert.Equal(1, admitted.SkillLoadCount);
            var reloadedCount = await reopened.GetAsync(admitted.ExecutionId);
            Assert.Equal(1, reloadedCount!.SkillLoadCount);

            var empty = await reopened.CreateAsync(Execution(
                sessionId,
                Guid.Parse("019944af-00d1-7000-8000-0000000000b3"),
                now,
                []));
            var reloadedEmpty = await reopened.GetAsync(empty.Item.ExecutionId);
            Assert.Empty(reloadedEmpty!.PinnedActiveSkillIds);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static ConversationTurnExecution Execution(
        Guid sessionId,
        Guid sourceEventId,
        DateTimeOffset now,
        IReadOnlyList<string> skillIds) =>
        ConversationTurnExecution.AcceptNew(
            Guid.NewGuid(),
            sessionId,
            Guid.NewGuid(),
            sourceEventId,
            Guid.NewGuid(),
            null,
            null,
            "skill-guide",
            1,
            null,
            new WorkModelPin("scripted-alpha", "primary-llm", "scripted-alpha", null),
            now,
            skillIds);

    private sealed class SqliteContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
