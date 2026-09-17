using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class SessionCatalogTests
{
    [Fact]
    public void Empty_user_text_keeps_default_title()
    {
        Assert.Equal(SessionTitles.Default, SessionTitles.FromUserText("   \n  "));
    }

    [Fact]
    public void First_message_title_collapses_whitespace()
    {
        Assert.Equal("Hello there", SessionTitles.FromUserText("Hello\n there  "));
    }

    [Fact]
    public async Task Create_pins_agent_version_default_title_and_workspace_ownership()
    {
        var manager = CreateManager(new InMemoryMemoryStore());
        var created = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        Assert.Equal(SessionTitles.Default, created.Title);
        Assert.True(created.WorkspaceOwned);
        Assert.Equal(0, created.RuntimeEpoch);
        Assert.Equal("examiner", created.Definition.Id);
        Assert.Equal(1, created.Definition.Version);
        Assert.Null(created.ArchivedAt);
    }

    [Fact]
    public async Task Catalog_orders_by_updated_at_descending_with_stable_cursor()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var manager = CreateManager(new InMemoryMemoryStore(), time);
        var first = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        time.Advance(TimeSpan.FromSeconds(1));
        var second = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        time.Advance(TimeSpan.FromSeconds(1));
        await manager.RenameAsync(first.SessionId, "Older chat");
        var page = await manager.ListCatalogAsync(cursor: null, limit: 1, includeArchived: false);
        Assert.True(page.HasMore);
        Assert.Equal(first.SessionId, page.Items[0].SessionId);
        Assert.Equal("Older chat", page.Items[0].Title);
        var next = await manager.ListCatalogAsync(page.NextCursor, limit: 1, includeArchived: false);
        Assert.Equal(second.SessionId, next.Items[0].SessionId);
        Assert.False(next.HasMore);
    }

    [Fact]
    public async Task Archive_hides_from_default_catalog_and_reopen_increments_epoch()
    {
        var manager = CreateManager(new InMemoryMemoryStore());
        var created = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        await manager.ArchiveAsync(created.SessionId);
        var hidden = await manager.ListCatalogAsync(null, 50, includeArchived: false);
        Assert.Empty(hidden.Items);
        var archived = await manager.ListCatalogAsync(null, 50, includeArchived: true);
        Assert.True(archived.Items[0].ArchivedAt is not null);
        await manager.UnarchiveAsync(created.SessionId);
        var reopened = await manager.ReopenAsync(created.SessionId);
        Assert.Equal(1, reopened.RuntimeEpoch);
    }

    [Fact]
    public async Task Ended_rows_remain_labeled_and_cannot_reopen()
    {
        var manager = CreateManager(new InMemoryMemoryStore());
        var created = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        await manager.EndAsync(created.SessionId);
        var page = await manager.ListCatalogAsync(null, 50, false);
        Assert.Equal(SessionStatus.Ended, page.Items[0].Status);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => manager.ReopenAsync(created.SessionId));
        Assert.Equal("ValidationError", error.Code);
    }

    [Fact]
    public async Task Durable_delete_removes_ended_sessions()
    {
        var manager = CreateManager(new InMemoryMemoryStore());
        var created = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        await manager.EndAsync(created.SessionId);
        var ended = await manager.GetAsync(created.SessionId);
        await manager.DurablyDeleteAsync(created.SessionId, ended.Revision);
        var page = await manager.ListCatalogAsync(null, 50, false);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Durable_delete_is_versioned_and_hides_the_session()
    {
        var store = new InMemoryMemoryStore();
        var manager = CreateManager(store);
        var created = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        var stale = await Assert.ThrowsAsync<AgentCoreException>(
            () => manager.DurablyDeleteAsync(created.SessionId, created.Revision + 1));
        Assert.Equal("Conflict", stale.Code);
        await manager.DurablyDeleteAsync(created.SessionId, created.Revision);
        await manager.DurablyDeleteAsync(created.SessionId, created.Revision);
        var missing = await Assert.ThrowsAsync<AgentCoreException>(() => manager.GetAsync(created.SessionId));
        Assert.Equal("NotFound", missing.Code);
        var page = await manager.ListCatalogAsync(null, 50, true);
        Assert.Empty(page.Items);
        var tombstone = await store.LoadAsync(created.SessionId);
        Assert.NotNull(tombstone!.DurablyDeletedAt);
        Assert.Empty(tombstone.Entries);
    }

    [Fact]
    public async Task Reopen_and_deactivate_preserve_catalog_order()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var manager = CreateManager(new InMemoryMemoryStore(), time);
        var first = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        time.Advance(TimeSpan.FromSeconds(1));
        var second = await manager.CreateAsync("examiner", 1, SessionMode.Text);

        var before = await manager.ListCatalogAsync(null, 50, false);
        Assert.Equal([second.SessionId, first.SessionId], before.Items.Select(item => item.SessionId).ToArray());

        await manager.ReopenAsync(first.SessionId);
        var afterReopen = await manager.ListCatalogAsync(null, 50, false);
        Assert.Equal([second.SessionId, first.SessionId], afterReopen.Items.Select(item => item.SessionId).ToArray());

        await manager.DeactivateAsync(second.SessionId);
        var afterDeactivate = await manager.ListCatalogAsync(null, 50, false);
        Assert.Equal([second.SessionId, first.SessionId], afterDeactivate.Items.Select(item => item.SessionId).ToArray());
    }

    [Fact]
    public async Task Deactivate_pauses_without_archive_or_delete_and_is_idempotent()
    {
        var manager = CreateManager(new InMemoryMemoryStore());
        var created = await manager.CreateAsync("examiner", 1, SessionMode.Text);
        var first = await manager.DeactivateAsync(created.SessionId);
        Assert.Equal(SessionStatus.Paused, first.Status);
        Assert.Equal(1, first.RuntimeEpoch);
        Assert.Null(first.ArchivedAt);
        var second = await manager.DeactivateAsync(created.SessionId);
        Assert.Equal(first.Revision, second.Revision);
        Assert.Equal(1, second.RuntimeEpoch);
        var listed = await manager.ListCatalogAsync(null, 50, false);
        Assert.Contains(listed.Items, item => item.SessionId == created.SessionId && item.Status == SessionStatus.Paused);
    }

    private static SessionManager CreateManager(IMemoryStore store, TimeProvider? time = null)
    {
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 16).Select(index => Guid.Parse($"019944af-0003-7000-8000-{index:D12}")),
            Enumerable.Range(1, 8).Select(index => Guid.Parse($"873f07d1-e264-4c81-a31b-7e59e940b8{index:D2}")).ToArray());
        return new SessionManager(
            new StaticDefinitions(SampleDefinitions.Examiner),
            store,
            ids,
            time ?? TimeProvider.System,
            new VoiceAvailability { SpeechAdaptersResolved = true });
    }

    private sealed class StaticDefinitions(AgentDefinition definition) : IAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);

        public ValueTask<AgentDefinition?> GetAsync(
            string id,
            int? version = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDefinition?>(
                string.Equals(id, definition.Id, StringComparison.Ordinal) ? definition : null);
    }
}
