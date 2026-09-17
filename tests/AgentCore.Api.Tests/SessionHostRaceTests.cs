using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Realtime;
using AgentCore.Contracts.Http;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class SessionHostRaceTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public SessionHostRaceTests(AgentCoreApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Concurrent_delete_and_reattach_rejects_ended_session()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        await using var hub = await ConnectAsync();
        var ready = ReadyWaiter(hub);
        var attached = await hub.InvokeAsync<CommandAck>("Attach", Attach(session.SessionId));
        Assert.True(attached.Accepted, attached.Error?.Message);
        await ready;

        var deleteTask = client.DeleteAsync($"/api/v1/sessions/{session.SessionId}");
        var reattachTask = hub.InvokeAsync<CommandAck>("Attach", Attach(session.SessionId));
        await Task.WhenAll(deleteTask, reattachTask);

        Assert.Equal(HttpStatusCode.NoContent, (await deleteTask).StatusCode);
        var reattach = await reattachTask;
        Assert.False(reattach.Accepted);

        await using var followUpHub = await ConnectAsync();
        var lateAttach = await followUpHub.InvokeAsync<CommandAck>("Attach", Attach(session.SessionId));
        Assert.False(lateAttach.Accepted);
        Assert.Equal("NotFound", lateAttach.Error?.Code);
    }

    [Fact]
    public async Task Stale_disconnect_does_not_evict_replacement_owner()
    {
        var host = _factory.Services.GetRequiredService<SessionHost>();
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        var sessionId = Guid.Parse(session.SessionId);

        var hubA = await ConnectAsync();
        var readyA = ReadyWaiter(hubA);
        var attachedA = await hubA.InvokeAsync<CommandAck>("Attach", Attach(session.SessionId));
        Assert.True(attachedA.Accepted, attachedA.Error?.Message);
        var attachmentA = await readyA;
        var staleConnectionId = hubA.ConnectionId!;
        await hubA.StopAsync();
        await hubA.DisposeAsync();

        var reopen = await client.PostAsync($"/api/v2/sessions/{session.SessionId}/reopen", null);
        reopen.EnsureSuccessStatusCode();

        await using var hubB = await ConnectAsync();
        var readyB = ReadyWaiter(hubB);
        var attachedB = await hubB.InvokeAsync<CommandAck>("Attach", Attach(session.SessionId));
        Assert.True(attachedB.Accepted, attachedB.Error?.Message);
        var attachmentB = await readyB;
        Assert.NotEqual(attachmentA, attachmentB);

        await host.DetachAsync(staleConnectionId);

        Assert.NotNull(host.LiveSnapshot(sessionId));
        Assert.Equal(Guid.Parse(attachmentB), host.LiveAttachmentId(sessionId));
    }

    private async Task<HubConnection> ConnectAsync()
    {
        var connection = new HubConnectionBuilder()
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
        await connection.StartAsync();
        return connection;
    }

    private static ClientCommand<AttachPayload> Attach(string sessionId) =>
        new()
        {
            ProtocolVersion = 1,
            SessionId = sessionId,
            EventId = Guid.NewGuid().ToString(),
            Sequence = 0,
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            Type = "session.attach",
            Payload = new AttachPayload()
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
        return ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
