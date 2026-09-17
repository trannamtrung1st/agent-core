using System.Net.Http.Json;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Contracts.Http;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class LiveSessionRenameApiTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public LiveSessionRenameApiTests(AgentCoreApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Rename_while_attached_keeps_live_revision_for_followup_turn()
    {
        var client = OwnerClient();
        var host = _factory.Services.GetRequiredService<SessionHost>();
        var store = _factory.Services.GetRequiredService<IMemoryStore>();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        created.EnsureSuccessStatusCode();
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        var sessionId = Guid.Parse(view!.SessionId);

        await using var hub = new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress!, "/hubs/session"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                    TestOwnerCapability.Apply(options, _factory.Services);
                })
            .AddMessagePackProtocol()
            .Build();
        var ready = ReadyWaiter(hub);
        await hub.StartAsync();
        var attached = await hub.InvokeAsync<CommandAck>("Attach", Attach(view.SessionId));
        Assert.True(attached.Accepted, attached.Error?.Message);
        var lease = await ready;
        var liveBefore = host.LiveSnapshot(sessionId);
        Assert.NotNull(liveBefore);
        var revisionBefore = liveBefore.Revision;

        var renamed = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{view.SessionId}/rename",
            new RenameSessionRequest("Planning notes"));
        renamed.EnsureSuccessStatusCode();
        var item = await renamed.Content.ReadFromJsonAsync<SessionCatalogItemResponse>();
        Assert.Equal("Planning notes", item!.Title);
        Assert.True(item.Revision > revisionBefore);

        var liveAfterRename = host.LiveSnapshot(sessionId);
        Assert.NotNull(liveAfterRename);
        Assert.Equal(item.Revision, liveAfterRename.Revision);

        var send = await hub.InvokeAsync<CommandAck>(
            "SendText",
            Text(view.SessionId, 1, lease, "Hello after rename", Guid.NewGuid().ToString()));
        Assert.True(send.Accepted, send.Error?.Message);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await WaitForHistoryAsync(client, view.SessionId, cts.Token);

        var liveAfterTurn = host.LiveSnapshot(sessionId);
        Assert.NotNull(liveAfterTurn);
        var loaded = await store.LoadAsync(sessionId);
        Assert.NotNull(loaded);
        Assert.Equal(liveAfterTurn.Revision, loaded.Revision);
        Assert.Contains(loaded.Entries, entry => entry.Role == ConversationRole.Assistant && entry.Status == EntryStatus.Completed);
    }

    private HttpClient OwnerClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            OwnerCapabilityHeaders.Name,
            TestOwnerCapability.Token(_factory.Services));
        return client;
    }

    private static ClientCommand<AttachPayload> Attach(string sessionId) =>
        new()
        {
            ProtocolVersion = 1,
            SessionId = sessionId,
            EventId = Guid.NewGuid().ToString(),
            Sequence = 0,
            Type = "session.attach",
            Payload = new AttachPayload()
        };

    private static ClientCommand<UserTextPayload> Text(
        string sessionId,
        long sequence,
        string lease,
        string text,
        string eventId) =>
        new()
        {
            ProtocolVersion = 1,
            SessionId = sessionId,
            EventId = eventId,
            Sequence = sequence,
            AttachmentId = lease,
            Type = "user.text",
            Payload = new UserTextPayload { Text = text }
        };

    private static Task<string> ReadyWaiter(HubConnection hub)
    {
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<ServerEvent>("SessionEvent", evt =>
        {
            if (evt.Type == "session.ready" && evt.AttachmentId is not null)
            {
                ready.TrySetResult(evt.AttachmentId);
            }
        });
        return ready.Task;
    }

    private static async Task WaitForHistoryAsync(HttpClient client, string sessionId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var page = await client.GetFromJsonAsync<HistoryPageResponse>(
                $"/api/v1/sessions/{sessionId}/messages?after=0&limit=50",
                cancellationToken);
            if (page?.Items.Any(item => item.Role == "assistant" && item.Status == "completed") == true)
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException("Assistant response was not persisted.");
    }
}
