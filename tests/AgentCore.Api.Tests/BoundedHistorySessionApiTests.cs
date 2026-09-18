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
        var db = Path.Combine(Path.GetTempPath(), $"agent-core-hist-{Guid.NewGuid():N}.db");
        try
        {
            await using var factory = new DurableSqliteHostFactory(db);
            var client = TestOwnerCapability.CreateOwnerClient(factory);
            var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
            created.EnsureSuccessStatusCode();
            var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
            var sessionId = Guid.Parse(view!.SessionId);
            var store = (SqliteMemoryStore)factory.Services.GetRequiredService<IMemoryStore>();
            var snapshot = await store.LoadMetadataAsync(sessionId);
            var entries = new ConversationEntry[240];
            for (var sequence = 1; sequence <= 240; sequence++)
            {
                var id = Guid.Parse($"019944af-1111-7000-8000-{sequence:D12}");
                entries[sequence - 1] = new ConversationEntry(
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
                snapshot! with { Revision = snapshot.Revision + 1, Entries = entries },
                snapshot.Revision);

            var get = await client.GetFromJsonAsync<SessionViewResponse>($"/api/v1/sessions/{sessionId}");
            Assert.Equal(240, get!.LastEntrySequence);
            Assert.Equal(0, store.MaterializedEntryRows);

            var page = await client.GetFromJsonAsync<HistoryPageResponse>(
                $"/api/v1/sessions/{sessionId}/messages?after=0&limit=50");
            Assert.Equal(50, page!.Items.Count);
            Assert.Equal(50, store.MaterializedEntryRows);
        }
        finally
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            File.Delete(db);
            File.Delete(db + "-wal");
            File.Delete(db + "-shm");
        }
    }
}
