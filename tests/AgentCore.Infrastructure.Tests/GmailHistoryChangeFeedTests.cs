using System.Net;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Email;
using AgentCore.Infrastructure.Email;

namespace AgentCore.Infrastructure.Tests;

public sealed class GmailHistoryChangeFeedTests
{
    [Fact]
    public async Task From_now_baseline_paged_backlog_dedupe_and_label_updates_are_safe()
    {
        var handler = new Transport(); var id = Guid.NewGuid(); var feed = Feed(handler, id);
        var cursor = await feed.BaselineAsync(default); Assert.Equal("100", cursor.HistoryId); Assert.Single(handler.Paths);
        var refs = new List<string>();
        do { var batch = await feed.PollAsync(cursor, default); refs.AddRange(batch.MessageRefs); cursor = batch.NextCursor; if (!batch.HasMore) break; } while (true);
        Assert.Equal(31, refs.Distinct().Count()); Assert.All(refs, r => Assert.StartsWith($"gmail-{id:N}:", r)); Assert.Equal("200", cursor.HistoryId);
        Assert.DoesNotContain(handler.Paths, p => p.Contains("format=full") || p.Contains("Subject") || p.Contains("users.watch"));
        Assert.Contains(handler.Paths, p => p.Contains("pageToken=second"));
    }
    [Fact]
    public async Task Expired_history_records_a_gap_without_historical_replay_and_temporary_failure_preserves_position()
    {
        var handler = new Transport { Expired = true }; var feed = Feed(handler, Guid.NewGuid()); var cursor = new EmailWatchCursor(EmailWatchCursorKind.GmailHistory, HistoryId: "1");
        var batch = await feed.PollAsync(cursor, default); Assert.True(batch.CursorGap); Assert.Empty(batch.MessageRefs); Assert.Equal("100", batch.NextCursor.HistoryId);
        handler.Expired = false; handler.Denied = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => feed.PollAsync(cursor, default).AsTask()); Assert.Equal("1", cursor.HistoryId);
    }
    private static GmailHistoryChangeFeed Feed(Transport handler, Guid id)
    {
        var c = new EmailAccountContext(Guid.Empty, id, "mail", "Test", 1, Guid.Empty, 0, EmailAccountProfile.Gmail());
        return new(id, new GmailEmailProvider(c, new Clients(handler), _ => ValueTask.FromResult("fixture"), _ => ValueTask.CompletedTask, () => false, TimeProvider.System));
    }
    private sealed class Clients(Transport handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }
    private sealed class Transport : HttpMessageHandler
    {
        public bool Expired, Denied; public List<string> Paths = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery; Paths.Add(path); object body;
            if (path.Contains("history?") && (Expired || Denied)) return Task.FromResult(new HttpResponseMessage(Expired ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden));
            if (path.EndsWith("profile")) body = new { historyId = "100" };
            else if (path.Contains("history?"))
            {
                var second = path.Contains("pageToken=second");
                var messages = Enumerable.Range(second ? 30 : 0, second ? 1 : 30).Select(i => new { message = new { id = "m" + i, labelIds = new[] { "INBOX" } } }).ToArray();
                body = second ? (object)new { historyId = "200", history = new[] { new { id = "199", messagesAdded = messages } } }
                    : new { historyId = "200", nextPageToken = "second", history = new object[] { new { id = "101", messagesAdded = messages }, new { id = "102", messagesAdded = messages.Take(1) }, new { id = "103", labelsAdded = messages } } };
            }
            else body = new { id = "ignored", labelIds = new[] { "INBOX" } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") });
        }
    }
}
