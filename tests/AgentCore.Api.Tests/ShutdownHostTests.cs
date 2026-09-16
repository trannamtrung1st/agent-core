using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class ShutdownHostTests
{
    [Fact]
    public async Task Shutdown_stops_admitting_new_sessions()
    {
        await using var factory = new AgentCoreApiFactory();
        var host = factory.Services.GetRequiredService<SessionHost>();
        Assert.True(host.Admitting);
        await host.DrainAsync();
        var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, created.StatusCode);
        Assert.False(host.Admitting);
    }

    [Fact]
    public async Task Shutdown_evicts_live_runtimes()
    {
        await using var factory = new AgentCoreApiFactory();
        var host = factory.Services.GetRequiredService<SessionHost>();
        var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        await using var hub = new HubConnectionBuilder()
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
        await hub.StartAsync();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<ServerEvent>("SessionEvent", evt =>
        {
            if (evt.Type == "session.ready" && evt.AttachmentId is not null)
            {
                ready.TrySetResult(evt.AttachmentId);
            }
        });
        var attached = await hub.InvokeAsync<CommandAck>(
            "Attach",
            new ClientCommand<AttachPayload>
            {
                ProtocolVersion = 1,
                SessionId = session.SessionId,
                EventId = Guid.NewGuid().ToString(),
                Sequence = 0,
                Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                Type = "session.attach",
                Payload = new AttachPayload()
            });
        Assert.True(attached.Accepted, attached.Error?.Message);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(host.LiveSnapshot(Guid.Parse(session.SessionId)));
        await host.DrainAsync();
        Assert.Null(host.LiveSnapshot(Guid.Parse(session.SessionId)));
        Assert.False(host.Admitting);
    }

    [Fact]
    public async Task Drain_does_not_wait_forever_for_hung_recognizer_dispose()
    {
        await using var factory = new HungDisposeApiFactory();
        var host = factory.Services.GetRequiredService<SessionHost>();
        var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        await using var hub = new HubConnectionBuilder()
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
        await hub.StartAsync();
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var voice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<ServerEvent>("SessionEvent", evt =>
        {
            if (evt.Type == "session.ready" && evt.AttachmentId is not null)
            {
                ready.TrySetResult(evt.AttachmentId);
            }

            if (evt.Type == "session.state.changed"
                && evt.Payload.TryGetValue("mode", out var mode)
                && mode?.ToString() == "voice")
            {
                voice.TrySetResult();
            }
        });
        var attached = await hub.InvokeAsync<CommandAck>(
            "Attach",
            new ClientCommand<AttachPayload>
            {
                ProtocolVersion = 1,
                SessionId = session.SessionId,
                EventId = Guid.NewGuid().ToString(),
                Sequence = 0,
                Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                Type = "session.attach",
                Payload = new AttachPayload()
            });
        Assert.True(attached.Accepted, attached.Error?.Message);
        var attachment = await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var modeAck = await hub.InvokeAsync<CommandAck>(
            "SetMode",
            new ClientCommand<SetModePayload>
            {
                ProtocolVersion = 1,
                SessionId = session.SessionId,
                AttachmentId = attachment,
                EventId = Guid.NewGuid().ToString(),
                Sequence = 1,
                Timestamp = DateTimeOffset.UtcNow.ToString("o"),
                Type = "session.mode.set",
                Payload = new SetModePayload { Mode = "voice" }
            });
        Assert.True(modeAck.Accepted, modeAck.Error?.Message);
        await voice.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await host.DrainAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            factory.ReleaseDispose();
        }

        Assert.Null(host.LiveSnapshot(Guid.Parse(session.SessionId)));
        Assert.False(host.Admitting);
    }
}

internal sealed class HungDisposeApiFactory : AgentCoreApiFactory
{
    private readonly TaskCompletionSource _hold = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseDispose() => _hold.TrySetResult();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ISpeechRecognizer>();
            services.AddSingleton<ISpeechRecognizer>(new HungDisposeRecognizer(_hold.Task));
        });
    }
}

internal sealed class HungDisposeRecognizer(Task hold) : ISpeechRecognizer
{
    public RecognitionCapabilities Capabilities { get; } = new(true, true, true, true);

    public ValueTask<ISpeechRecognitionSession> OpenAsync(
        RecognitionOptions options,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<ISpeechRecognitionSession>(new HungDisposeSession(hold));

    private sealed class HungDisposeSession(Task hold) : ISpeechRecognitionSession
    {
        public ValueTask PushAudioAsync(AudioFrame frame, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask ObserveBoundaryAsync(
            Guid utteranceId,
            SpeechBoundary boundary,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask CompleteInputAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<SpeechRecognitionEvent> ReadEventsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async ValueTask DisposeAsync() => await hold.ConfigureAwait(false);
    }
}
