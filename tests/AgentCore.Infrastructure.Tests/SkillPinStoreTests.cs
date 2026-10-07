using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Work;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class SkillPinStoreTests
{
    [Fact]
    public async Task Sqlite_reopen_keeps_the_complete_catalog_and_active_keys()
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
            var created = await store.CreateAsync(Execution(sessionId, sourceEventId, now, ["definition:refund.handle", "definition:order.lookup"]));
            Assert.Equal(ConversationTurnExecutionCreateKind.Created, created.Kind);

            var reopened = new SqliteConversationTurnExecutionStore(factory);
            var loaded = await reopened.GetBySourceEventAsync(sessionId, sourceEventId);
            Assert.Equal(["definition:refund.handle", "definition:order.lookup"], loaded!.ActiveSkillKeys);
            Assert.Equal("PINNED_LOCAL_PROCEDURE", loaded.PinnedSkillCatalog.Single(s => s.Origin == SkillOrigin.Instance).Procedure);

            var claimed = await reopened.TryClaimAsync(
                loaded.ExecutionId,
                Guid.Parse("019944af-00d1-7000-8000-0000000000b9"),
                now.AddMinutes(1),
                now.AddMinutes(6));
            Assert.Equal(["definition:refund.handle", "definition:order.lookup"], claimed!.ActiveSkillKeys);
            var admitted = await reopened.AdmitActiveSkillsAsync(
                claimed.ExecutionId,
                claimed.Revision,
                Guid.Parse("019944af-00d1-7000-8000-0000000000b9"),
                ["definition:billing.note"],
                now.AddMinutes(2));
            Assert.Equal(["definition:refund.handle", "definition:order.lookup", "definition:billing.note"], admitted.ActiveSkillKeys);
            Assert.Equal(1, admitted.SkillLoadCount);
            var reloadedCount = await reopened.GetAsync(admitted.ExecutionId);
            Assert.Equal(1, reloadedCount!.SkillLoadCount);

            var loadedCapabilities = await reopened.AdmitCapabilitiesAsync(admitted.ExecutionId, admitted.Revision,
                admitted.Claim!.Generation, ["workspace.read", "email.search"], now.AddMinutes(3));
            var afterReopen = await new SqliteConversationTurnExecutionStore(factory).GetAsync(loadedCapabilities.ExecutionId);
            Assert.Equal(["workspace.read", "email.search"], afterReopen!.LoadedCapabilityIds);
            Assert.Equal(1, afterReopen.CapabilityLoadCount);
            await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(async () =>
                await reopened.AdmitCapabilitiesAsync(admitted.ExecutionId, admitted.Revision, admitted.Claim.Generation, ["workspace.write"], now.AddMinutes(4)));

            Assert.Equal(1, await reopened.RecoverExpiredClaimsAsync(now.AddMinutes(7)));
            var reclaim = (await reopened.TryClaimAsync(admitted.ExecutionId, Guid.NewGuid(), now.AddMinutes(7), now.AddMinutes(12)))!;
            Assert.Equal(afterReopen.LoadedCapabilityIds, reclaim.LoadedCapabilityIds);
            Assert.Equal(1, reclaim.CapabilityLoadCount);
            await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(async () =>
                await reopened.AdmitCapabilitiesAsync(reclaim.ExecutionId, reclaim.Revision, admitted.Claim.Generation, ["workspace.write"], now.AddMinutes(8)));
            var cancelled = await reopened.RequestCancellationAsync(sessionId, reclaim.ExecutionId, reclaim.Revision, now.AddMinutes(8));
            await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(async () =>
                await reopened.AdmitCapabilitiesAsync(cancelled.ExecutionId, cancelled.Revision, reclaim.Claim!.Generation, ["workspace.write"], now.AddMinutes(9)));
            Assert.Equal(afterReopen.LoadedCapabilityIds, (await reopened.GetAsync(cancelled.ExecutionId))!.LoadedCapabilityIds);

            var empty = await reopened.CreateAsync(Execution(
                sessionId,
                Guid.Parse("019944af-00d1-7000-8000-0000000000b3"),
                now,
                []));
            var reloadedEmpty = await reopened.GetAsync(empty.Item.ExecutionId);
            Assert.Empty(reloadedEmpty!.ActiveSkillKeys);
            Assert.Empty(reloadedEmpty.LoadedCapabilityIds);
            Assert.Equal(0, reloadedEmpty.CapabilityLoadCount);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_capability_admission_leaves_revision_ids_and_count_unchanged(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-capability-cancel-{Guid.NewGuid():N}.db");
        var factory = new SqliteContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            IConversationTurnExecutionStore store;
            if (sqlite)
            {
                await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
                store = new SqliteConversationTurnExecutionStore(factory);
            }
            else store = new InMemoryConversationTurnExecutionStore();
            var now = DateTimeOffset.Parse("2026-10-07T00:00:00Z");
            var item = (await store.CreateAsync(Execution(Guid.NewGuid(), Guid.NewGuid(), now, []))).Item;
            var claim = (await store.TryClaimAsync(item.ExecutionId, Guid.NewGuid(), now, now.AddMinutes(5)))!;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await store.AdmitCapabilitiesAsync(claim.ExecutionId, claim.Revision, claim.Claim!.Generation,
                    ["workspace.write"], now.AddSeconds(1), cancellation.Token));
            var unchanged = (await store.GetAsync(claim.ExecutionId))!;
            Assert.Equal(claim.Revision, unchanged.Revision);
            Assert.Empty(unchanged.LoadedCapabilityIds);
            Assert.Equal(0, unchanged.CapabilityLoadCount);
            var admitted = await store.AdmitCapabilitiesAsync(claim.ExecutionId, claim.Revision, claim.Claim!.Generation,
                ["workspace.read"], now.AddSeconds(2));
            Assert.Equal(["workspace.read"], admitted.LoadedCapabilityIds);
            Assert.Equal(1, admitted.CapabilityLoadCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
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
            skillIds, pinnedSkillCatalog: new[] { "definition:refund.handle", "definition:order.lookup", "definition:billing.note" }
                .Select(key => new EffectiveSkill(key, SkillOrigin.Definition, key[11..], key, "Guidance", "Pinned procedure",
                    SkillProjection.OnDemand, [], [])).Append(new EffectiveSkill("instance:019944af-00d1-7000-8000-0000000000c1",
                    SkillOrigin.Instance, "019944af-00d1-7000-8000-0000000000c1", "Local", "Local procedure", "PINNED_LOCAL_PROCEDURE",
                    SkillProjection.OnDemand, ["workspace.read"], [])).ToArray());

    private sealed class SqliteContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
