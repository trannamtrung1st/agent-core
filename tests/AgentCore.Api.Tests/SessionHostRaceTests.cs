using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
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

        await using var hubB = await ConnectAsync();
        var readyB = ReadyWaiter(hubB);
        var attachedB = await AttachWhenSessionAvailableAsync(hubB, session.SessionId);
        Assert.True(attachedB.Accepted, attachedB.Error?.Message);
        var attachmentB = await readyB;
        Assert.NotEqual(attachmentA, attachmentB);

        await host.DetachAsync(staleConnectionId);

        Assert.NotNull(host.LiveSnapshot(sessionId));
        Assert.Equal(Guid.Parse(attachmentB), host.LiveAttachmentId(sessionId));
    }

    [Fact(Timeout = 30_000)]
    public async Task Refresh_attach_loads_interrupted_assistant_after_previous_runtime_disposes()
    {
        var releaseModel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueAttach = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                foreach (var descriptor in services.Where(item => item.ServiceType == typeof(ILanguageModel)).ToArray())
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton<ILanguageModel>(
                    new ScriptedLanguageModel(["Hello, this is...", " more text."], releaseModel));
            });
        });

        var host = factory.Services.GetRequiredService<SessionHost>();
        var store = factory.Services.GetRequiredService<IMemoryStore>();
        var client = TestOwnerCapability.CreateOwnerClient(factory);
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        var sessionId = Guid.Parse(session.SessionId);

        var hubA = await ConnectFactoryAsync(factory);
        var readyA = ReadyWaiter(hubA);
        var attachedA = await hubA.InvokeAsync<CommandAck>("Attach", Attach(session.SessionId));
        Assert.True(attachedA.Accepted, attachedA.Error?.Message);
        var attachmentA = await readyA;

        var deltaSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hubA.On<ServerEvent>("SessionEvent", evt =>
        {
            if (evt.Type == "agent.text.delta")
            {
                deltaSeen.TrySetResult();
            }
        });

        try
        {
            host.AfterAttachDurableSnapshotRead = async cancellationToken =>
            {
                snapshotRead.TrySetResult();
                await continueAttach.Task.WaitAsync(cancellationToken);
            };

            var send = hubA.InvokeAsync<CommandAck>(
                "SendText",
                Text(session.SessionId, 1, attachmentA, "Hi", Guid.NewGuid().ToString()));
            await deltaSeen.Task.WaitAsync(TimeSpan.FromSeconds(15));

            var hubB = await ConnectFactoryAsync(factory);
            var readyB = ReadyWaiter(hubB);
            var attachB = AttachWhenSessionAvailableAsync(hubB, session.SessionId);

            var stopA = hubA.StopAsync();
            await snapshotRead.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await WaitForInterruptedAssistantAsync(store, sessionId);
            continueAttach.TrySetResult();

            var attachedB = await attachB.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(attachedB.Accepted, attachedB.Error?.Message);
            var attachmentB = await readyB.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotEqual(attachmentA, attachmentB);

            var durable = (await store.LoadAsync(sessionId))!;
            var durableAssistant = durable.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
            Assert.Equal(EntryStatus.Interrupted, durableAssistant.Status);
            Assert.StartsWith("Hello, this is", durableAssistant.Text, StringComparison.Ordinal);

            var live = host.LiveSnapshot(sessionId);
            Assert.NotNull(live);
            Assert.Equal(durable.Revision, live.Revision);
            var liveAssistant = live.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
            Assert.Equal(durableAssistant.EntryId, liveAssistant.EntryId);
            Assert.Equal(EntryStatus.Interrupted, liveAssistant.Status);
            Assert.Equal(durableAssistant.Text, liveAssistant.Text);

            await stopA;
            await hubA.DisposeAsync();
            await hubB.DisposeAsync();
        }
        finally
        {
            host.AfterAttachDurableSnapshotRead = null;
            releaseModel.TrySetResult();
            continueAttach.TrySetResult();
        }
    }

    private static async Task WaitForInterruptedAssistantAsync(IMemoryStore store, Guid sessionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await store.LoadAsync(sessionId);
            var assistant = snapshot?.Entries.LastOrDefault(entry => entry.Role == ConversationRole.Assistant);
            if (assistant is not null
                && assistant.Status == EntryStatus.Interrupted
                && assistant.Text.StartsWith("Hello, this is", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Interrupted assistant text was not persisted before refresh attach continued.");
    }

    private async Task<HubConnection> ConnectFactoryAsync(WebApplicationFactory<Program> factory)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(
                new Uri(factory.Server.BaseAddress!, "/hubs/session"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                    TestOwnerCapability.Apply(options, factory.Services);
                })
            .AddMessagePackProtocol()
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private static ClientCommand<UserTextPayload> Text(
        string sessionId,
        long sequence,
        string attachmentId,
        string text,
        string eventId) =>
        new()
        {
            ProtocolVersion = 1,
            SessionId = sessionId,
            EventId = eventId,
            Sequence = sequence,
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            AttachmentId = attachmentId,
            Type = "user.text",
            Payload = new UserTextPayload { Text = text }
        };

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

    private static async Task<CommandAck> AttachWhenSessionAvailableAsync(HubConnection hub, string sessionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        CommandAck? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await hub.InvokeAsync<CommandAck>("Attach", Attach(sessionId));
            if (last.Accepted)
            {
                return last;
            }

            if (last.Error?.Code is not ("SessionInUse" or "SessionBusy"))
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.True(last?.Accepted ?? false, last?.Error?.Message);
        return last!;
    }

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
