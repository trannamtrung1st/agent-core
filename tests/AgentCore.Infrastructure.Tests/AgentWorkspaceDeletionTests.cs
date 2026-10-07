using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AgentCore.Infrastructure.Tests;

public sealed class AgentWorkspaceDeletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_logical_delete_preserves_archived_owner_metadata_and_exact_bytes(bool sqlite)
    {
        await using var fixture = await Fixture.CreateAsync(sqlite);
        fixture.Failure.FailCommit = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Deletion.DeleteInstanceAsync(fixture.Command).AsTask());
        Assert.Equal(AgentInstanceLifecycle.Archived, (await fixture.Instances.FindAsync(fixture.Owner))!.Lifecycle);
        Assert.Null(await fixture.Events.TryGetByOperationIdAsync(fixture.Command.OperationId));
        var content = await fixture.ReadHomeAsync();
        Assert.Equal(fixture.Item, content.Item);
        Assert.Equal(Fixture.Bytes, content.Bytes);
        Assert.True(Directory.Exists(fixture.OwnerDirectory));
        fixture.Failure.FailCommit = false;
        await fixture.Deletion.DeleteInstanceAsync(fixture.Command);
        Assert.Null(await fixture.Instances.FindAsync(fixture.Owner));
        Assert.Empty((await fixture.Home.ListAsync(fixture.Owner, "/home", null, 256)).Items);
        Assert.False(Directory.Exists(fixture.OwnerDirectory));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Post_commit_purge_failure_recovers_by_receipt_retry_or_sweep(bool sqlite, bool sweep)
    {
        await using var fixture = await Fixture.CreateAsync(sqlite);
        fixture.Workspace.FailCleanup = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Deletion.DeleteInstanceAsync(fixture.Command).AsTask());
        Assert.Null(await fixture.Instances.FindAsync(fixture.Owner));
        Assert.Equal(AdminEventOperationKind.InstanceDeleted,
            (await fixture.Events.TryGetByOperationIdAsync(fixture.Command.OperationId))!.Operation);
        Assert.True(Directory.Exists(fixture.OwnerDirectory));
        if (sqlite) Assert.Empty((await fixture.Home.ListAsync(fixture.Owner, "/home", null, 256)).Items);
        fixture.Workspace.FailCleanup = false;
        // Reconstruct the durable deletion service/workspace adapter to exercise restart recovery.
        var deletion = sqlite ? fixture.NewSqliteDeletion() : fixture.Deletion;
        if (sweep) await deletion.RecoverWorkspaceCleanupAsync();
        else await deletion.DeleteInstanceAsync(fixture.Command);
        Assert.Empty((await fixture.Home.ListAsync(fixture.Owner, "/home", null, 256)).Items);
        Assert.False(Directory.Exists(fixture.OwnerDirectory));
        await deletion.RecoverWorkspaceCleanupAsync();
        await deletion.DeleteInstanceAsync(fixture.Command);
        var receipts = await fixture.Events.ListAsync(new("agent.instance", fixture.Owner.ToString("D")));
        Assert.Single(receipts, e => e.Operation == AdminEventOperationKind.InstanceDeleted);
        var conflict = await Assert.ThrowsAsync<AgentCoreException>(() => deletion.DeleteInstanceAsync(
            fixture.Command with { ExpectedRevision = fixture.Command.ExpectedRevision + 1 }).AsTask());
        Assert.Equal("Conflict", conflict.Code);
        await Assert.ThrowsAsync<AgentCoreException>(() => deletion.DeleteInstanceAsync(
            fixture.Command with { InstanceId = Guid.NewGuid() }).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_never_purges_an_existing_owner_even_with_an_old_deletion_receipt(bool sqlite)
    {
        await using var fixture = await Fixture.CreateAsync(sqlite);
        await fixture.Deletion.DeleteInstanceAsync(fixture.Command);
        await fixture.Instances.InsertAsync(fixture.Instance);
        var item = await fixture.Home.WriteFileAsync(fixture.Owner, "/home/new.bin", "application/octet-stream", BytesForNewOwner,
            null, null, null);
        await fixture.Deletion.RecoverWorkspaceCleanupAsync();
        Assert.Equal(BytesForNewOwner, (await fixture.Home.ReadAsync(fixture.Owner, item.ItemId, null)).Bytes);
        await Assert.ThrowsAsync<AgentCoreException>(() => fixture.Deletion.DeleteInstanceAsync(fixture.Command).AsTask());
    }

    private static readonly byte[] BytesForNewOwner = [42, 0, 128];

    private sealed class Failure : SaveChangesInterceptor
    {
        internal bool FailCommit;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FailCommit && eventData.Context!.ChangeTracker.Entries<AdminEventRecord>()
                .Any(e => e.State == EntityState.Added && e.Entity.Operation == nameof(AdminEventOperationKind.InstanceDeleted)))
                throw new IOException("Injected durable deletion failure.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailingEvents(IIdGenerator ids, Failure failure) : InMemoryAdminEventStore(ids)
    {
        internal override void AppendWithinLock(AdminEventAppend append)
        {
            if (failure.FailCommit && append.Operation == AdminEventOperationKind.InstanceDeleted)
                throw new IOException("Injected deletion-event failure.");
            base.AppendWithinLock(append);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly byte[] Bytes = [0, 255, 128, 10, 13];
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "workspace-delete-" + Guid.NewGuid().ToString("N"));
        internal readonly Guid Owner = Guid.NewGuid();
        internal readonly Failure Failure = new();
        internal readonly IIdGenerator Ids = new SystemIdGenerator(TimeProvider.System);
        internal Factory? Contexts;
        internal IAgentInstanceStore Instances = null!;
        internal IAdminEventStore Events = null!;
        internal FileAgentInstanceWorkspaceStore Home = null!;
        internal FailingWorkspace Workspace = null!;
        internal IAdminLifecycleDeletion Deletion = null!;
        internal AgentWorkspaceItem Item = null!;
        internal AgentInstance Instance = null!;
        internal AdminInstanceDeleteCommand Command = null!;
        internal string OwnerDirectory => AgentWorkspacePhysicalPaths.AgentRoot(Path.Combine(Root, "home"), Owner);

        internal static async Task<Fixture> CreateAsync(bool sqlite)
        {
            var f = new Fixture(); Directory.CreateDirectory(f.Root);
            if (sqlite)
            {
                f.Contexts = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>()
                    .UseSqlite($"Data Source={f.Root}/store.db").AddInterceptors(f.Failure).Options);
                await new SqliteMemoryStore(f.Contexts, TimeProvider.System).EnsureCreatedAsync();
                f.Instances = new SqliteAgentInstanceStore(f.Contexts, f.Ids);
                f.Events = new SqliteAdminEventStore(f.Contexts, f.Ids);
            }
            else
            {
                f.Instances = new InMemoryAgentInstanceStore();
                f.Events = new FailingEvents(f.Ids, f.Failure);
            }
            f.Home = f.NewHome(); f.Workspace = new FailingWorkspace(f.Home);
            if (sqlite) f.Deletion = new SqliteAdminLifecycleDeletion(f.Contexts!, f.Ids, f.Workspace);
            else
            {
                var state = new InMemoryDurableState();
                f.Deletion = new InMemoryAdminLifecycleDeletion((InMemoryAgentInstanceStore)f.Instances,
                    new InMemoryMemoryStore(), new InMemoryStructuredMemoryStore(), new InMemoryTriggerStore(state),
                    new InMemoryWorkItemStore(state), new InMemoryConversationTurnExecutionStore(state),
                    new InMemoryAgentDefinitionAdminStore(f.Ids), (InMemoryAdminEventStore)f.Events, workspace: f.Workspace);
            }
            var now = TimeProvider.System.GetUtcNow();
            f.Instance = new AgentInstance(f.Owner, "field-guide", 1, new AgentIdentity("Guide", "role", "desc", "tone"),
                AgentInstanceLifecycle.Archived, now, now);
            await f.Instances.InsertAsync(f.Instance);
            f.Item = await f.Home.WriteFileAsync(f.Owner, "/home/original.bin", "application/octet-stream", Bytes,
                Guid.NewGuid(), null, null);
            f.Command = new(f.Owner, f.Instance.Revision, Guid.NewGuid(), now);
            return f;
        }

        private FileAgentInstanceWorkspaceStore NewHome() => new(Path.Combine(Root, "home"), TimeProvider.System, Ids, Contexts);
        internal IAdminLifecycleDeletion NewSqliteDeletion() => new SqliteAdminLifecycleDeletion(Contexts!, Ids, NewHome());
        internal ValueTask<AgentWorkspaceContent> ReadHomeAsync() =>
            (Contexts is null ? Home : NewHome()).ReadAsync(Owner, Item.ItemId, null);
        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); return ValueTask.CompletedTask;
        }
    }

    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);
    }

    private sealed class FailingWorkspace(IAgentInstanceWorkspaceStore inner) : IAgentInstanceWorkspaceStore
    {
        internal bool FailCleanup;
        public ValueTask DeleteInstanceAsync(Guid id, CancellationToken ct = default) =>
            FailCleanup ? ValueTask.FromException(new IOException("Injected byte cleanup failure.")) : inner.DeleteInstanceAsync(id, ct);
        public ValueTask DeleteInstanceContentAsync(Guid id, CancellationToken ct = default) =>
            FailCleanup ? ValueTask.FromException(new IOException("Injected byte cleanup failure.")) : inner.DeleteInstanceContentAsync(id, ct);
        public ValueTask<AgentWorkspacePage> ListAsync(Guid id, string prefix, string? after, int limit, CancellationToken ct = default) => inner.ListAsync(id, prefix, after, limit, ct);
        public ValueTask<AgentWorkspaceContent> ReadAsync(Guid id, Guid? item, string? path, CancellationToken ct = default) => inner.ReadAsync(id, item, path, ct);
        public ValueTask<AgentWorkspaceItem> WriteFileAsync(Guid id, string path, string type, ReadOnlyMemory<byte> bytes, Guid? session,
            long? revision, string? hash, CancellationToken ct = default) => inner.WriteFileAsync(id, path, type, bytes, session, revision, hash, ct);
        public ValueTask DeleteAsync(Guid id, Guid item, long revision, CancellationToken ct = default) => inner.DeleteAsync(id, item, revision, ct);
    }
}
