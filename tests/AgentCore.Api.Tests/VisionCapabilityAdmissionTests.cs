using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentCore.Contracts.Http;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace AgentCore.Api.Tests;

public sealed class VisionCapabilityAdmissionTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public VisionCapabilityAdmissionTests(AgentCoreApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SendText_with_image_on_non_vision_model_is_rejected_without_persisting()
    {
        var client = Owner();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        created.EnsureSuccessStatusCode();
        var view = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        var uploaded = await client.PostAsync(
            $"/api/v2/sessions/{view.SessionId}/attachments",
            ImageContent("photo.png"));
        uploaded.EnsureSuccessStatusCode();
        var attachment = await uploaded.Content.ReadFromJsonAsync<AttachmentResponse>();

        await using var hub = await ConnectAsync();
        var leaseTask = ReadyWaiter(hub);
        var attached = await hub.InvokeAsync<CommandAck>("Attach", Attach(view.SessionId));
        Assert.True(attached.Accepted, attached.Error?.Message);
        var lease = await leaseTask;

        var send = await hub.InvokeAsync<CommandAck>(
            "SendText",
            Text(view.SessionId, 1, lease, "Describe the image.", Guid.NewGuid().ToString(), [attachment!.AttachmentId]));
        Assert.False(send.Accepted);
        Assert.Equal("ModelCapabilityUnsupported", send.Error?.Code);
        Assert.Equal("Session", send.Error?.Category);
        Assert.False(send.Error?.Fatal);

        var reloaded = await client.GetFromJsonAsync<SessionViewResponse>($"/api/v2/sessions/{view.SessionId}");
        Assert.Equal(0, reloaded!.LastEntrySequence);
    }

    [Fact]
    public async Task Text_only_SendText_remains_accepted_on_non_vision_model()
    {
        var client = Owner();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var view = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        await using var hub = await ConnectAsync();
        var leaseTask = ReadyWaiter(hub);
        var attached = await hub.InvokeAsync<CommandAck>("Attach", Attach(view!.SessionId));
        Assert.True(attached.Accepted, attached.Error?.Message);
        var lease = await leaseTask;
        var send = await hub.InvokeAsync<CommandAck>(
            "SendText",
            Text(view.SessionId, 1, lease, "Hello", Guid.NewGuid().ToString(), []));
        Assert.True(send.Accepted, send.Error?.Message);
    }

    private HttpClient Owner()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            OwnerCapabilityHeaders.Name,
            TestOwnerCapability.Token(_factory.Services));
        return client;
    }

    private async Task<HubConnection> ConnectAsync()
    {
        var hub = new HubConnectionBuilder()
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
        await hub.StartAsync();
        return hub;
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
        string eventId,
        string[] attachmentIds) =>
        new()
        {
            ProtocolVersion = 1,
            SessionId = sessionId,
            AttachmentId = lease,
            EventId = eventId,
            Sequence = sequence,
            Type = "user.text",
            Payload = new UserTextPayload { Text = text, AttachmentIds = attachmentIds }
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

    private static MultipartFormDataContent ImageContent(string name)
    {
        var png = MinimalPng();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var content = new MultipartFormDataContent { { file, "file", name } };
        return content;
    }

    private static byte[] MinimalPng()
    {
        using var image = new Image<Rgba32>(2, 2);
        using var buffer = new MemoryStream();
        image.Save(buffer, new PngEncoder());
        return buffer.ToArray();
    }
}
