using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
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
        var created = await Owner().PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(Owner(), "examiner", 1), "text"));
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

        var other = await Owner().PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(Owner(), "examiner", 1), "text"));
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
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text"));
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

    [Theory]
    [InlineData("report.docx")]
    [InlineData("sales.xlsx")]
    [InlineData("archive.zip")]
    [InlineData("blob.bin")]
    [InlineData("program.exe")]
    [InlineData("corrupt.docx")]
    [InlineData("late-binary.txt")]
    [InlineData("unread-image.bmp")]
    public async Task All_file_types_upload_bind_and_preserve_original_bytes_without_a_role_opt_in(string name)
    {
        var client = Owner();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text"));
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        var office = _factory.Services.GetRequiredService<IOfficeProcessor>();
        var bytes = name switch
        {
            "report.docx" => office.CreateDocument(new("Quarterly report", [new(null, ["Uploaded document evidence."], null) ]), default).Bytes,
            "sales.xlsx" => office.CreateWorkbook(new([new("Sales", [[new("text", Text: "Uploaded workbook evidence.")]])]), default).Bytes,
            "archive.zip" or "corrupt.docx" => "PK\u0003\u0004abcd"u8.ToArray(),
            "program.exe" => "MZabc"u8.ToArray(),
            "late-binary.txt" => Enumerable.Repeat((byte)'a', 8192).Append((byte)255).ToArray(),
            _ => new byte[] { 0, 1, 2, 255 }
        };
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(name == "unread-image.bmp" ? "image/bmp" : "application/octet-stream");
        form.Add(file, "file", name);
        var uploaded = await client.PostAsync($"/api/v2/sessions/{view!.SessionId}/attachments", form);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var attachment = (await uploaded.Content.ReadFromJsonAsync<AttachmentResponse>())!;
        await using var hub = await ConnectAsync();
        var ready = ReadyWaiter(hub);
        Assert.True((await hub.InvokeAsync<CommandAck>("Attach", Attach(view.SessionId))).Accepted);
        var ack = await hub.InvokeAsync<CommandAck>("SendText", Text(view.SessionId, 1, await ready, "", Guid.NewGuid().ToString(), [attachment.AttachmentId]));
        Assert.True(ack.Accepted, ack.Error?.Message);
        var bound = (await client.GetFromJsonAsync<AttachmentResponse>($"/api/v2/sessions/{view.SessionId}/attachments/{attachment.AttachmentId}"))!;
        Assert.Equal("bound", bound.State);
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/sessions/{view.SessionId}/attachments/{attachment.AttachmentId}/content"));
        var processor = _factory.Services.GetRequiredService<IAttachmentProcessor>();
        var result = Assert.Single(await processor.ProcessTurnAsync(Guid.Parse(view.SessionId), [Guid.Parse(attachment.AttachmentId)]));
        var definition = (await _factory.Services.GetRequiredService<IMemoryStore>().LoadAsync(Guid.Parse(view.SessionId)))!.Definition;
        var reread = await _factory.Services.GetRequiredService<SessionToolExecutor>().ExecuteAsync(definition, Guid.Parse(view.SessionId),
            new ModelToolCall(Guid.NewGuid().ToString(), ToolCatalog.AttachmentsRead, JsonSerializer.Serialize(new { attachmentId = attachment.AttachmentId })), 256 * 1024);
        if (name is "report.docx" or "sales.xlsx")
        {
            Assert.True(attachment.Readable);
            Assert.Equal(AttachmentProcessKind.ExtractedText, result.Kind);
            Assert.Contains(name == "report.docx" ? "Uploaded document evidence." : "Uploaded workbook evidence.", result.Text);
            Assert.Contains(name == "report.docx" ? "Uploaded document evidence." : "Uploaded workbook evidence.", reread.Text);
        }
        else
        {
            Assert.Equal(AttachmentProcessKind.Unsupported, result.Kind);
            if (name == "late-binary.txt") Assert.Contains("unsupported_reader", reread.Text);
            else if (name != "corrupt.docx") Assert.Contains("Original bytes are available", reread.Text);
        }
    }

    [Fact]
    public async Task Spreadsheet_attachment_reads_all_rows_tabs_and_off_window_columns_with_bounded_continuation()
    {
        var client = Owner();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text"));
        var view = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        var sessionId = Guid.Parse(view.SessionId);
        var office = _factory.Services.GetRequiredService<IOfficeProcessor>();
        IReadOnlyList<IReadOnlyList<OfficeCell>> Items(int start, int count) => Enumerable.Range(start, count)
            .Select(i => (IReadOnlyList<OfficeCell>)new[] { new OfficeCell("text", Text: $"Wedding item {i}") }).ToArray();
        var original = office.CreateWorkbook(new([
            new("Check list", Items(1, 200)), new("Guest list", [[new("text", Text: "Guest beyond first tab")]]), new("Empty", [])]), default).Bytes;
        var bytes = office.EditWorkbook(original, [
            new("set_cells", "Check list", "A201:A240", Items(201, 40)),
            new("set_cells", "Check list", "BM1", [[new("text", Text: "Beyond column 64")]])], default).Bytes;
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", "wedding.xlsx");
        var uploaded = await client.PostAsync($"/api/v2/sessions/{view.SessionId}/attachments", form);
        uploaded.EnsureSuccessStatusCode();
        var attachment = (await uploaded.Content.ReadFromJsonAsync<AttachmentResponse>())!;
        await using var hub = await ConnectAsync();
        var ready = ReadyWaiter(hub);
        Assert.True((await hub.InvokeAsync<CommandAck>("Attach", Attach(view.SessionId))).Accepted);
        Assert.True((await hub.InvokeAsync<CommandAck>("SendText", Text(view.SessionId, 1, await ready, "Summarize all sheets", Guid.NewGuid().ToString(), [attachment.AttachmentId]))).Accepted);
        var processed = Assert.Single(await _factory.Services.GetRequiredService<IAttachmentProcessor>().ProcessTurnAsync(sessionId, [Guid.Parse(attachment.AttachmentId)]));
        using var initial = JsonDocument.Parse(processed.Text);
        Assert.Equal(3, initial.RootElement.GetProperty("sheetExtents").GetArrayLength());
        Assert.True(initial.RootElement.GetProperty("truncated").GetBoolean());
        var prompt = PromptContextBuilder.BuildCurrentUserMessage("Summarize all", [processed], true,
            new Dictionary<Guid, int> { [Guid.Parse(attachment.AttachmentId)] = 4096 });
        var excerpt = prompt.Text.Split("\"\"\"")[1].Trim();
        using var promptPage = JsonDocument.Parse(excerpt);
        Assert.True(promptPage.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Contains("without asking permission", prompt.Text);

        var definition = (await _factory.Services.GetRequiredService<IMemoryStore>().LoadAsync(sessionId))!.Definition;
        var tools = _factory.Services.GetRequiredService<SessionToolExecutor>();
        async Task<JsonElement> Read(Guid target, object args, int budget = 8192)
        {
            var result = await tools.ExecuteAsync(definition, target, new ModelToolCall(Guid.NewGuid().ToString(), ToolCatalog.AttachmentsRead, JsonSerializer.Serialize(args)), budget);
            Assert.True(Encoding.UTF8.GetByteCount(result.Text) <= budget);
            using var json = JsonDocument.Parse(result.Text);
            return json.RootElement.Clone();
        }
        var seen = new List<string>();
        string? range = "A1:A200";
        for (var page = 0; page < 20 && range is not null; page++)
        {
            var result = (await Read(sessionId, new { attachmentId = attachment.AttachmentId, sheet = "Check list", range })).GetProperty("inspection");
            seen.AddRange(result.GetProperty("rows").EnumerateArray().Select(r => r[0].GetProperty("value").GetString()!).Where(v => v.StartsWith("Wedding item")));
            var next = result.GetProperty("nextRange").GetString();
            range = next?.StartsWith('A') == true ? next : null;
        }
        Assert.Equal(Enumerable.Range(1, 240).Select(i => $"Wedding item {i}"), seen);
        var column = await Read(sessionId, new { attachmentId = attachment.AttachmentId, sheet = "Check list", range = "BM1" });
        Assert.Contains("Beyond column 64", column.ToString());
        var guest = (await Read(sessionId, new { attachmentId = attachment.AttachmentId, sheet = "Guest list" })).GetProperty("inspection");
        Assert.Contains("Guest beyond first tab", guest.ToString());
        Assert.False(guest.GetProperty("truncated").GetBoolean());
        var empty = (await Read(sessionId, new { attachmentId = attachment.AttachmentId, sheet = "Empty" })).GetProperty("inspection");
        Assert.Empty(empty.GetProperty("rows").EnumerateArray());
        Assert.False(empty.GetProperty("truncated").GetBoolean());
        var invalid = await Read(sessionId, new { attachmentId = attachment.AttachmentId, sheet = "Check list", range = "A1:A201" });
        Assert.Contains("office_operation_unsupported", invalid.ToString());
        var recovered = await Read(sessionId, new { attachmentId = attachment.AttachmentId, sheet = "Check list", range = "A240" });
        Assert.Contains("Wedding item 240", recovered.ToString());
        var other = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text"));
        var foreign = (await other.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        Assert.Contains("notFound", (await Read(Guid.Parse(foreign.SessionId), new { attachmentId = attachment.AttachmentId })).ToString());
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/sessions/{view.SessionId}/attachments/{attachment.AttachmentId}/content"));
    }

    [Fact]
    public async Task Document_attachment_read_continues_with_complete_elements_and_rejects_spreadsheet_arguments()
    {
        var client = Owner();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text"));
        var view = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        var sessionId = Guid.Parse(view.SessionId);
        var paragraphs = Enumerable.Range(1, 120).Select(i => $"Wedding detail {i}").ToArray();
        var bytes = _factory.Services.GetRequiredService<IOfficeProcessor>().CreateDocument(new(null,
            [new(null, paragraphs.Take(100).ToArray(), null), new(null, paragraphs.Skip(100).ToArray(), null)]), default).Bytes;
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "file", "wedding.docx");
        var uploaded = await client.PostAsync($"/api/v2/sessions/{view.SessionId}/attachments", form);
        uploaded.EnsureSuccessStatusCode();
        var attachment = (await uploaded.Content.ReadFromJsonAsync<AttachmentResponse>())!;
        await using var hub = await ConnectAsync();
        var ready = ReadyWaiter(hub);
        Assert.True((await hub.InvokeAsync<CommandAck>("Attach", Attach(view.SessionId))).Accepted);
        Assert.True((await hub.InvokeAsync<CommandAck>("SendText", Text(view.SessionId, 1, await ready, "Summarize", Guid.NewGuid().ToString(), [attachment.AttachmentId]))).Accepted);
        var processed = Assert.Single(await _factory.Services.GetRequiredService<IAttachmentProcessor>().ProcessTurnAsync(sessionId, [Guid.Parse(attachment.AttachmentId)]));
        using var initial = JsonDocument.Parse(processed.Text);
        Assert.Equal(100, initial.RootElement.GetProperty("nextOffset").GetInt32());
        var prompt = PromptContextBuilder.BuildCurrentUserMessage("Summarize", [processed], true,
            new Dictionary<Guid, int> { [Guid.Parse(attachment.AttachmentId)] = 4096 });
        using var excerpt = JsonDocument.Parse(prompt.Text.Split("\"\"\"")[1].Trim());
        Assert.True(excerpt.RootElement.GetProperty("nextOffset").GetInt32() > 0);
        Assert.True(excerpt.RootElement.GetProperty("nextOffset").GetInt32() < 100);
        var definition = (await _factory.Services.GetRequiredService<IMemoryStore>().LoadAsync(sessionId))!.Definition;
        async Task<JsonElement> Read(object args)
        {
            var result = await _factory.Services.GetRequiredService<SessionToolExecutor>().ExecuteAsync(definition, sessionId,
                new ModelToolCall(Guid.NewGuid().ToString(), ToolCatalog.AttachmentsRead, JsonSerializer.Serialize(args)), 4096);
            Assert.True(Encoding.UTF8.GetByteCount(result.Text) <= 4096);
            using var json = JsonDocument.Parse(result.Text); return json.RootElement.Clone();
        }
        var seen = new List<string>();
        int? offset = 0;
        for (var page = 0; page < 20 && offset is not null; page++)
        {
            var inspection = (await Read(new { attachmentId = attachment.AttachmentId, offset, limit = 100 })).GetProperty("inspection");
            seen.AddRange(inspection.GetProperty("elements").EnumerateArray().Select(e => e.GetProperty("text").GetString()!));
            offset = inspection.GetProperty("nextOffset").ValueKind == JsonValueKind.Null ? null : inspection.GetProperty("nextOffset").GetInt32();
        }
        Assert.Equal(paragraphs, seen);
        Assert.Contains("invalid", (await Read(new { attachmentId = attachment.AttachmentId, sheet = "Sheet1" })).ToString());
        Assert.Contains("invalid", (await Read(new { attachmentId = attachment.AttachmentId, range = (string?)null })).ToString());
        Assert.Equal(bytes, await client.GetByteArrayAsync($"/api/v2/sessions/{view.SessionId}/attachments/{attachment.AttachmentId}/content"));
    }

    [Fact]
    public async Task Ended_session_can_read_attachments_but_not_upload()
    {
        var client = Owner();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text"));
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
