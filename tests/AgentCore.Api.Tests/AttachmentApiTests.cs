using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AgentCore.Contracts.Http;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class AttachmentApiTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public AttachmentApiTests(AgentCoreApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Upload_bind_and_content_require_capability_and_fail_closed()
    {
        var anonymous = _factory.CreateClient();
        var created = await Owner().PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        var denied = await anonymous.PostAsync(
            $"/api/v2/sessions/{view!.SessionId}/attachments",
            FileContent("notes.txt", "hello world"));
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var uploaded = await Owner().PostAsync(
            $"/api/v2/sessions/{view.SessionId}/attachments",
            FileContent("notes.txt", "hello world"));
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var attachment = await uploaded.Content.ReadFromJsonAsync<AttachmentResponse>();
        Assert.Equal("pending", attachment!.State);
        Assert.Equal("notes.txt", attachment.DisplayName);

        var listed = await Owner().GetFromJsonAsync<AttachmentResponse[]>(
            $"/api/v2/sessions/{view.SessionId}/attachments");
        Assert.Contains(listed!, item => item.AttachmentId == attachment.AttachmentId);

        var other = await Owner().PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var otherView = await other.Content.ReadFromJsonAsync<SessionViewResponse>();
        var leaked = await Owner().GetAsync(
            $"/api/v2/sessions/{otherView!.SessionId}/attachments/{attachment.AttachmentId}");
        Assert.Equal(HttpStatusCode.NotFound, leaked.StatusCode);

        await using var hub = await ConnectAsync();
        var ready = ReadyWaiter(hub);
        var attached = await hub.InvokeAsync<CommandAck>("Attach", Attach(view.SessionId));
        Assert.True(attached.Accepted, attached.Error?.Message);
        var lease = await ready;
        var send = await hub.InvokeAsync<CommandAck>(
            "SendText",
            Text(view.SessionId, 1, lease, "", Guid.NewGuid().ToString(), [attachment.AttachmentId]));
        Assert.True(send.Accepted, send.Error?.Message);

        var bound = await Owner().GetFromJsonAsync<AttachmentResponse>(
            $"/api/v2/sessions/{view.SessionId}/attachments/{attachment.AttachmentId}");
        Assert.Equal("bound", bound!.State);
        Assert.Equal(attachment.Sha256, bound.Sha256);
        var bytes = await Owner().GetByteArrayAsync(
            $"/api/v2/sessions/{view.SessionId}/attachments/{attachment.AttachmentId}/content");
        Assert.Equal("hello world", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task Archive_and_delete_denials_are_distinct()
    {
        var client = Owner();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        await client.PostAsync($"/api/v2/sessions/{view!.SessionId}/archive", null);
        var archived = await client.PostAsync(
            $"/api/v2/sessions/{view.SessionId}/attachments",
            FileContent("a.txt", "hi"));
        Assert.Equal(HttpStatusCode.Conflict, archived.StatusCode);
        Assert.Contains("SessionArchived", await archived.Content.ReadAsStringAsync());

        var unarchived = await client.PostAsync($"/api/v2/sessions/{view.SessionId}/unarchive", null);
        var item = await unarchived.Content.ReadFromJsonAsync<SessionCatalogItemResponse>();
        await client.DeleteAsync($"/api/v2/sessions/{view.SessionId}");
        var missing = await client.PostAsync(
            $"/api/v2/sessions/{view.SessionId}/attachments",
            FileContent("a.txt", "hi"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Zip_is_rejected_even_with_unread_policy()
    {
        var client = Owner();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        using var zip = FileContent("x.zip", "PK\u0003\u0004abcd", "application/zip");
        zip.Headers.TryAddWithoutValidation("X-AgentCore-Allow-Store-Unread", "true");
        var rejected = await client.PostAsync($"/api/v2/sessions/{view!.SessionId}/attachments", zip);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task Ended_session_can_read_attachments_but_not_upload()
    {
        var client = Owner();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        var uploaded = await client.PostAsync(
            $"/api/v2/sessions/{view!.SessionId}/attachments",
            FileContent("notes.txt", "hello world"));
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);

        var ended = await client.DeleteAsync($"/api/v1/sessions/{view.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);

        var listed = await client.GetAsync($"/api/v2/sessions/{view.SessionId}/attachments");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var items = await listed.Content.ReadFromJsonAsync<AttachmentResponse[]>();
        var notes = Assert.Single(items!, item => item.DisplayName == "notes.txt");
        var content = await client.GetAsync($"/api/v2/sessions/{view.SessionId}/attachments/{notes.AttachmentId}/content");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("hello world", await content.Content.ReadAsStringAsync());

        var rejected = await client.PostAsync(
            $"/api/v2/sessions/{view.SessionId}/attachments",
            FileContent("more.txt", "nope"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("Ended sessions cannot accept attachments", await rejected.Content.ReadAsStringAsync());
    }

    private HttpClient Owner()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            OwnerCapabilityHeaders.Name,
            TestOwnerCapability.Token(_factory.Services));
        return client;
    }

    private static MultipartFormDataContent FileContent(string name, string text, string type = "text/plain")
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue(type);
        content.Add(file, "file", name);
        return content;
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
            EventId = eventId,
            Sequence = sequence,
            AttachmentId = lease,
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
}
