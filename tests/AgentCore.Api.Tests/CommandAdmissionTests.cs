using System.Net.Http.Json;
using AgentCore.Api.Realtime;
using AgentCore.Contracts.Http;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class CommandAdmissionTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public CommandAdmissionTests(AgentCoreApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Parallel_controls_admit_without_losing_either_event_id()
    {
        var host = _factory.Services.GetRequiredService<SessionHost>();
        var admitted = 0;
        var bothAdmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var client = TestOwnerCapability.CreateOwnerClient(_factory);
            var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
            created.EnsureSuccessStatusCode();
            var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
            await using var hub = await ConnectAsync();
            var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            hub.On<ServerEvent>("SessionEvent", evt =>
            {
                if (evt.Type == "session.ready" && evt.AttachmentId is not null)
                {
                    ready.TrySetResult(evt.AttachmentId);
                }
            });
            var attach = Command(session.SessionId, 0, "session.attach", new AttachPayload());
            var attached = await hub.InvokeAsync<CommandAck>("Attach", attach);
            Assert.True(attached.Accepted, attached.Error?.Message);
            var attachment = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var mode = await hub.InvokeAsync<CommandAck>(
                "SetMode",
                Command(session.SessionId, 1, "session.mode.set", new SetModePayload { Mode = "voice" }, attachment));
            Assert.True(mode.Accepted, mode.Error?.Message);
            host.AfterAdmitHold = async () =>
            {
                if (Interlocked.Increment(ref admitted) == 2)
                {
                    bothAdmitted.TrySetResult();
                }

                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            };
            var firstId = Guid.NewGuid().ToString();
            var secondId = Guid.NewGuid().ToString();
            var firstTask = hub.InvokeAsync<CommandAck>(
                "SetMuted",
                Command(session.SessionId, 2, "session.mute", new MutePayload { Muted = true }, attachment, firstId));
            var secondTask = hub.InvokeAsync<CommandAck>(
                "SetMuted",
                Command(session.SessionId, 3, "session.mute", new MutePayload { Muted = false }, attachment, secondId));
            await bothAdmitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var firstRetry = hub.InvokeAsync<CommandAck>(
                "SetMuted",
                Command(session.SessionId, 2, "session.mute", new MutePayload { Muted = true }, attachment, firstId));
            var secondRetry = hub.InvokeAsync<CommandAck>(
                "SetMuted",
                Command(session.SessionId, 3, "session.mute", new MutePayload { Muted = false }, attachment, secondId));
            release.TrySetResult();
            var first = await firstTask.WaitAsync(TimeSpan.FromSeconds(10));
            var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(10));
            var retriedFirst = await firstRetry.WaitAsync(TimeSpan.FromSeconds(10));
            var retriedSecond = await secondRetry.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(first.Accepted, first.Error?.Message);
            Assert.True(second.Accepted, second.Error?.Message);
            Assert.True(retriedFirst.Accepted);
            Assert.True(retriedSecond.Accepted);
            Assert.Equal(first.EventId, retriedFirst.EventId);
            Assert.Equal(second.EventId, retriedSecond.EventId);
            Assert.False(host.RuntimeMuted(Guid.Parse(session.SessionId)));
        }
        finally
        {
            host.AfterAdmitHold = null;
        }
    }

    [Fact]
    public async Task Detach_completes_queued_and_in_flight_commands()
    {
        var host = _factory.Services.GetRequiredService<SessionHost>();
        var admitted = 0;
        var bothAdmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var client = TestOwnerCapability.CreateOwnerClient(_factory);
            var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
            created.EnsureSuccessStatusCode();
            var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
            await using var hub = await ConnectAsync();
            var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            hub.On<ServerEvent>("SessionEvent", evt =>
            {
                if (evt.Type == "session.ready" && evt.AttachmentId is not null)
                {
                    ready.TrySetResult(evt.AttachmentId);
                }
            });
            var attached = await hub.InvokeAsync<CommandAck>("Attach", Command(session.SessionId, 0, "session.attach", new AttachPayload()));
            Assert.True(attached.Accepted, attached.Error?.Message);
            var attachment = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var mode = await hub.InvokeAsync<CommandAck>(
                "SetMode",
                Command(session.SessionId, 1, "session.mode.set", new SetModePayload { Mode = "voice" }, attachment));
            Assert.True(mode.Accepted, mode.Error?.Message);
            host.AfterAdmitHold = async () =>
            {
                if (Interlocked.Increment(ref admitted) == 2)
                {
                    bothAdmitted.TrySetResult();
                }

                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            };
            var firstTask = hub.InvokeAsync<CommandAck>(
                "SetMuted",
                Command(session.SessionId, 2, "session.mute", new MutePayload { Muted = true }, attachment));
            var secondTask = hub.InvokeAsync<CommandAck>(
                "SetMuted",
                Command(session.SessionId, 3, "session.mute", new MutePayload { Muted = false }, attachment));
            await bothAdmitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await host.DetachAsync(hub.ConnectionId!);
            release.TrySetResult();
            var first = await firstTask.WaitAsync(TimeSpan.FromSeconds(10));
            var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(first.Accepted);
            Assert.False(second.Accepted);
            Assert.Equal("NotFound", first.Error?.Code);
            Assert.Equal("NotFound", second.Error?.Code);
            Assert.Null(host.LiveSnapshot(Guid.Parse(session.SessionId)));
        }
        finally
        {
            host.AfterAdmitHold = null;
        }
    }

    [Fact]
    public async Task Unknown_user_text_behavior_is_rejected()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        await using var hub = await ConnectAsync();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<ServerEvent>("SessionEvent", evt =>
        {
            if (evt.Type == "session.ready" && evt.AttachmentId is not null)
            {
                ready.TrySetResult(evt.AttachmentId);
            }
        });
        var attached = await hub.InvokeAsync<CommandAck>("Attach", Command(session.SessionId, 0, "session.attach", new AttachPayload()));
        Assert.True(attached.Accepted, attached.Error?.Message);
        var attachment = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var denied = await hub.InvokeAsync<CommandAck>(
            "SendText",
            Command(
                session.SessionId,
                1,
                "user.text",
                new UserTextPayload { Text = "Hello", Behavior = "drop" },
                attachment));
        Assert.False(denied.Accepted);
        Assert.Equal("ValidationError", denied.Error?.Code);
    }

    [Fact]
    public async Task CancelResponse_rejects_unknown_and_missing_ids()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        await using var hub = await ConnectAsync();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<ServerEvent>("SessionEvent", evt =>
        {
            if (evt.Type == "session.ready" && evt.AttachmentId is not null)
            {
                ready.TrySetResult(evt.AttachmentId);
            }
        });
        var attached = await hub.InvokeAsync<CommandAck>("Attach", Command(session.SessionId, 0, "session.attach", new AttachPayload()));
        Assert.True(attached.Accepted, attached.Error?.Message);
        var attachment = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var missing = await hub.InvokeAsync<CommandAck>(
            "CancelResponse",
            Command(session.SessionId, 1, "agent.response.cancel", new CancelResponsePayload(), attachment));
        Assert.False(missing.Accepted);
        Assert.Equal("ValidationError", missing.Error?.Code);
        var unknown = await hub.InvokeAsync<CommandAck>(
            "CancelResponse",
            Command(
                session.SessionId,
                2,
                "agent.response.cancel",
                new CancelResponsePayload(),
                attachment,
                responseId: Guid.NewGuid().ToString()));
        Assert.False(unknown.Accepted);
        Assert.Equal("ValidationError", unknown.Error?.Code);
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

    private static ClientCommand<T> Command<T>(
        string sessionId,
        long sequence,
        string type,
        T payload,
        string? attachmentId = null,
        string? eventId = null,
        string? responseId = null)
    {
        return new ClientCommand<T>
        {
            ProtocolVersion = 1,
            SessionId = sessionId,
            EventId = eventId ?? Guid.NewGuid().ToString(),
            Sequence = sequence,
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            AttachmentId = attachmentId,
            ResponseId = responseId,
            Type = type,
            Payload = payload
        };
    }
}
