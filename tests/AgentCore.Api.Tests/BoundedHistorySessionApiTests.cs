using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class BoundedHistorySessionApiTests
{
    [Fact]
    public async Task GetSession_on_long_sqlite_transcript_returns_durable_cursor_without_loading_all_entries()
    {
        await using var host = await Seeded.CreateAsync(240);
        var store = host.Store;
        var get = await host.Client.GetFromJsonAsync<SessionViewResponse>($"/api/v1/sessions/{host.SessionId}");
        Assert.Equal(240, get!.LastEntrySequence);
        Assert.Equal(0, store.MaterializedEntryRows);
        Assert.Equal(0, store.LoadAsyncCalls);
    }

    [Fact]
    public async Task Messages_newest_before_and_after_page_without_full_transcript_load()
    {
        await using var host = await Seeded.CreateAsync(240);
        var loads = host.Store.LoadAsyncCalls;
        var meta = host.Store.LoadMetadataCalls;

        var newest = await host.Client.GetFromJsonAsync<HistoryPageResponse>(
            $"/api/v1/sessions/{host.SessionId}/messages?limit=50");
        Assert.Equal(50, newest!.Items.Count);
        Assert.Equal(191, newest.Items[0].Sequence);
        Assert.Equal(240, newest.Items[^1].Sequence);
        Assert.True(newest.Items.Zip(newest.Items.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
        Assert.True(newest.HasOlder);
        Assert.Equal(191, newest.NextBefore);
        Assert.False(newest.HasMore);
        Assert.True(host.Store.MaterializedEntryRows <= 51);
        Assert.Equal(loads, host.Store.LoadAsyncCalls);
        Assert.Equal(meta, host.Store.LoadMetadataCalls);

        var older = await host.Client.GetFromJsonAsync<HistoryPageResponse>(
            $"/api/v1/sessions/{host.SessionId}/messages?before=191&limit=50");
        Assert.Equal(141, older!.Items[0].Sequence);
        Assert.Equal(190, older.Items[^1].Sequence);
        Assert.True(older.HasOlder);
        Assert.Equal(141, older.NextBefore);
        Assert.Equal(loads, host.Store.LoadAsyncCalls);

        var forward = await host.Client.GetFromJsonAsync<HistoryPageResponse>(
            $"/api/v1/sessions/{host.SessionId}/messages?after=200&limit=50");
        Assert.Equal(201, forward!.Items[0].Sequence);
        Assert.Equal(240, forward.Items[^1].Sequence);
        Assert.False(forward.HasMore);
        Assert.Equal(40, forward.Items.Count);

        var both = await host.Client.GetAsync(
            $"/api/v1/sessions/{host.SessionId}/messages?after=1&before=10&limit=10");
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }

    [Fact]
    public async Task Messages_paused_and_ended_sessions_share_the_paging_contract()
    {
        await using var host = await Seeded.CreateAsync(40);
        var snapshot = await host.Store.LoadMetadataAsync(host.SessionId);
        await host.Store.SaveAsync(
            snapshot! with { Revision = snapshot.Revision + 1, Status = SessionStatus.Paused, PauseReason = "manual" },
            snapshot.Revision);
        var paused = await host.Client.GetFromJsonAsync<HistoryPageResponse>(
            $"/api/v1/sessions/{host.SessionId}/messages?limit=20");
        Assert.Equal(21, paused!.Items[0].Sequence);
        Assert.Equal(40, paused.Items[^1].Sequence);
        Assert.True(paused.HasOlder);

        var ended = await host.Client.DeleteAsync($"/api/v1/sessions/{host.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        var terminal = await host.Client.GetFromJsonAsync<HistoryPageResponse>(
            $"/api/v1/sessions/{host.SessionId}/messages?limit=20");
        Assert.Equal(21, terminal!.Items[0].Sequence);
        Assert.Equal(40, terminal.Items[^1].Sequence);
        Assert.True(terminal.HasOlder);
    }

    [Fact]
    public async Task Messages_before_cursor_stays_stable_after_a_newer_append()
    {
        await using var host = await Seeded.CreateAsync(80);
        var newest = await host.Client.GetFromJsonAsync<HistoryPageResponse>(
            $"/api/v1/sessions/{host.SessionId}/messages?limit=20");
        var before = newest!.NextBefore;
        var snapshot = await host.Store.LoadMetadataAsync(host.SessionId);
        var extra = new ConversationEntry(
            Guid.Parse("019944af-1111-7000-8000-0000000000f1"),
            81,
            Guid.Parse("019944af-1111-7000-8000-0000000000f1"),
            ConversationRole.User,
            "appended",
            null,
            EntryStatus.Completed,
            SessionMode.Text,
            8,
            8,
            snapshot!.CreatedAt);
        await host.Store.SaveAsync(
            snapshot with { Revision = snapshot.Revision + 1, Entries = [extra], LastEntrySequence = 81 },
            snapshot.Revision);
        var older = await host.Client.GetFromJsonAsync<HistoryPageResponse>(
            $"/api/v1/sessions/{host.SessionId}/messages?before={before}&limit=20");
        Assert.DoesNotContain(older!.Items, item => item.Sequence == 81);
        Assert.Equal(before - 20, older.Items[0].Sequence);
        Assert.Equal(before - 1, older.Items[^1].Sequence);
    }

    [Fact]
    public async Task Messages_page_uses_public_history_projection_fields()
    {
        await using var host = await Seeded.CreateAsync(0);
        var snapshot = await host.Store.LoadMetadataAsync(host.SessionId);
        var entry = new ConversationEntry(
            Guid.Parse("019944af-2222-7000-8000-000000000001"),
            1,
            Guid.Parse("019944af-2222-7000-8000-000000000010"),
            ConversationRole.Assistant,
            "Secret tail and shown",
            Guid.Parse("019944af-2222-7000-8000-000000000011"),
            EntryStatus.Interrupted,
            SessionMode.Voice,
            5,
            11,
            snapshot!.CreatedAt,
            new ResponseEnvelope(
                "Secret tail and shown",
                "Spoken hidden",
                [new ResponseBlock("b1", ResponseBlockKind.Markdown, "**Hi**", "Hi", null, null, true)]),
            [new ConversationAttachmentRef(Guid.Parse("019944af-2222-7000-8000-000000000012"), "note.txt", "text/plain")],
            FinishReason: "interrupted");
        await host.Store.SaveAsync(
            snapshot with { Revision = snapshot.Revision + 1, Entries = [entry], LastEntrySequence = 1 },
            snapshot.Revision);
        var page = await host.Client.GetFromJsonAsync<HistoryPageResponse>(
            $"/api/v1/sessions/{host.SessionId}/messages?limit=10");
        var item = Assert.Single(page!.Items);
        Assert.Equal("interrupted", item.Status);
        Assert.Equal("voice", item.DeliveryMode);
        Assert.Equal("Secret tail", item.Text);
        Assert.Equal(5, item.HeardTextEndExclusive);
        Assert.Equal(11, item.ReceivedTextEndExclusive);
        Assert.Equal("interrupted", item.FinishReason);
        Assert.Equal("**Hi**", Assert.Single(item.Blocks!).Text);
        Assert.Equal("note.txt", Assert.Single(item.Attachments!).DisplayName);
        Assert.DoesNotContain("Spoken hidden", item.Text, StringComparison.Ordinal);
        Assert.Equal("Spoken hidden", item.SpeechText);
    }

    private sealed class Seeded : IAsyncDisposable
    {
        private readonly string _db;
        private readonly DurableSqliteHostFactory _factory;

        private Seeded(string db, DurableSqliteHostFactory factory, HttpClient client, Guid sessionId, SqliteMemoryStore store)
        {
            _db = db;
            _factory = factory;
            Client = client;
            SessionId = sessionId;
            Store = store;
        }

        public HttpClient Client { get; }

        public Guid SessionId { get; }

        public SqliteMemoryStore Store { get; }

        public static async Task<Seeded> CreateAsync(int entries)
        {
            var db = Path.Combine(Path.GetTempPath(), $"agent-core-hist-{Guid.NewGuid():N}.db");
            var factory = new DurableSqliteHostFactory(db);
            var client = TestOwnerCapability.CreateOwnerClient(factory);
            var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
            created.EnsureSuccessStatusCode();
            var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
            var sessionId = Guid.Parse(view!.SessionId);
            var store = (SqliteMemoryStore)factory.Services.GetRequiredService<IMemoryStore>();
            if (entries > 0)
            {
                var snapshot = await store.LoadMetadataAsync(sessionId);
                var rows = new ConversationEntry[entries];
                for (var sequence = 1; sequence <= entries; sequence++)
                {
                    var id = Guid.Parse($"019944af-1111-7000-8000-{sequence:D12}");
                    rows[sequence - 1] = new ConversationEntry(
                        id,
                        sequence,
                        id,
                        sequence % 2 == 0 ? ConversationRole.Assistant : ConversationRole.User,
                        $"m{sequence}",
                        sequence % 2 == 0 ? id : null,
                        EntryStatus.Completed,
                        SessionMode.Text,
                        0,
                        $"m{sequence}".Length,
                        snapshot!.CreatedAt);
                }

                await store.SaveAsync(
                    snapshot! with { Revision = snapshot.Revision + 1, Entries = rows },
                    snapshot.Revision);
            }

            return new Seeded(db, factory, client, sessionId, store);
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db}");
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            File.Delete(_db);
            File.Delete(_db + "-wal");
            File.Delete(_db + "-shm");
        }
    }
}
