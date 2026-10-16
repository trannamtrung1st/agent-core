using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgentCore.Contracts.Http;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class WorkspaceApiTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public WorkspaceApiTests(AgentCoreApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Execution_view_is_lazy_readonly_overlays_and_writable_workspace()
    {
        var anonymous = _factory.CreateClient();
        var created = await Owner().PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(Owner(), "examiner", 1), "text"));
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        var denied = await anonymous.GetAsync($"/api/v2/sessions/{view!.SessionId}/workspace");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var listed = await Owner().GetFromJsonAsync<WorkspaceNodePageResponse>(
            $"/api/v2/sessions/{view.SessionId}/workspace?prefix=/working");
        Assert.Empty(listed!.Entries);
        var agentNodes = await Owner().GetFromJsonAsync<WorkspaceNodePageResponse>($"/api/v2/sessions/{view.SessionId}/workspace?prefix=/agent");
        Assert.Contains(agentNodes!.Entries, node => node.LogicalPath == "/agent/definition.json");
        var attachmentNodes = await Owner().GetFromJsonAsync<WorkspaceNodePageResponse>($"/api/v2/sessions/{view.SessionId}/workspace?prefix=/attachments");
        Assert.Empty(attachmentNodes!.Entries);

        var definition = await Owner().GetByteArrayAsync(
            $"/api/v2/sessions/{view.SessionId}/workspace/content?path=/agent/definition.json");
        Assert.Contains("examiner", Encoding.UTF8.GetString(definition), StringComparison.Ordinal);

        var mutateAgent = await Owner().PutAsync(
            $"/api/v2/sessions/{view.SessionId}/workspace/content?path=/agent/definition.json",
            new ByteArrayContent("{}"u8.ToArray()));
        Assert.Equal(HttpStatusCode.Forbidden, mutateAgent.StatusCode);

        var written = await Owner().PutAsync(
            $"/api/v2/sessions/{view.SessionId}/workspace/content?path=/working/note.txt",
            new ByteArrayContent("hello"u8.ToArray()));
        Assert.Equal(HttpStatusCode.NoContent, written.StatusCode);
        var roundTrip = await Owner().GetByteArrayAsync(
            $"/api/v2/sessions/{view.SessionId}/workspace/content?path=/working/note.txt");
        Assert.Equal("hello", Encoding.UTF8.GetString(roundTrip));

        var traversal = await Owner().GetAsync(
            $"/api/v2/sessions/{view.SessionId}/workspace/content?path=/working/../secret");
        Assert.Equal(HttpStatusCode.Forbidden, traversal.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Working_lists_reflect_session_and_instance_archive_without_losing_bytes(bool archiveInstance)
    {
        using var client = Owner();
        var instance = TestInstances.Create(client, "examiner", 1);
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(instance, "text"));
        created.EnsureSuccessStatusCode();
        var view = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        var sessionId = Guid.Parse(view.SessionId);
        var root = $"/api/v2/sessions/{view.SessionId}/workspace";
        (await client.PutAsync(root + "/content?path=/working/nested/note.txt", new ByteArrayContent("retained"u8.ToArray()))).EnsureSuccessStatusCode();
        async Task AssertWritable(bool expected)
        {
            var page = (await client.GetFromJsonAsync<WorkspaceNodePageResponse>(root + "?prefix=/working/nested"))!;
            Assert.Equal(expected, Assert.Single(page.Entries).Writable);
            var snapshot = (await _factory.Services.GetRequiredService<IMemoryStore>().LoadAsync(sessionId))!;
            var workspace = _factory.Services.GetRequiredService<ISessionWorkspace>();
            Assert.Equal(expected, Assert.Single(await workspace.ListAsync(sessionId, snapshot.Definition, "/working/nested")).Writable);
            Assert.Equal(expected, (await workspace.ListAsync(sessionId, snapshot.Definition, "/")).Single(node => node.LogicalPath == "/working").Writable);
        }
        await AssertWritable(true);
        var response = archiveInstance
            ? await client.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{instance}/lifecycle", new AdminUpdateAgentInstanceLifecycleRequest(1, "Archived"))
            : await client.PostAsJsonAsync($"/api/v2/sessions/{view.SessionId}/archive", new { });
        response.EnsureSuccessStatusCode();
        await AssertWritable(false);
        Assert.Equal("retained", Encoding.UTF8.GetString(await client.GetByteArrayAsync(root + "/content?path=/working/nested/note.txt")));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync(root + "/content?path=/working/nested/note.txt", new ByteArrayContent("denied"u8.ToArray()))).StatusCode);
        response = archiveInstance
            ? await client.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{instance}/lifecycle", new AdminUpdateAgentInstanceLifecycleRequest(2, "Active"))
            : await client.PostAsJsonAsync($"/api/v2/sessions/{view.SessionId}/unarchive", new { });
        response.EnsureSuccessStatusCode();
        await AssertWritable(true);
    }

    [Theory]
    [InlineData("/home/oversized.txt")]
    [InlineData("/working/oversized.txt")]
    public async Task Oversized_file_content_length_is_rejected_before_writing(string path)
    {
        using var client = Owner();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text"));
        var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!.SessionId;
        using var body = new ByteArrayContent("must-not-be-written"u8.ToArray());
        body.Headers.ContentLength = AgentWorkspaceLimits.MaxFileBytes + 1;
        var response = await client.PutAsync($"/api/v2/sessions/{session}/workspace/content?path={path}", body);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v2/sessions/{session}/workspace/content?path={path}")).StatusCode);
    }

    [Fact]
    public async Task Unknown_length_upload_stops_reading_at_file_limit_and_leaves_no_file()
    {
        using var client = Owner();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text"));
        var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!.SessionId;
        var source = new GeneratedUpload(AgentWorkspaceLimits.MaxFileBytes + 2 * 1024 * 1024);
        var path = $"/api/v2/sessions/{session}/workspace/content?path=/working/stream.bin";
        // Supply the incoming body directly so the count measures endpoint reads,
        // not TestServer's independently buffered outgoing HttpContent producer.
        var response = await _factory.Server.SendAsync(context =>
        {
            context.Request.Method = "PUT";
            context.Request.Path = $"/api/v2/sessions/{session}/workspace/content";
            context.Request.QueryString = new("?path=/working/stream.bin");
            context.Request.Headers[OwnerCapabilityHeaders.Name] = TestOwnerCapability.Token(_factory.Services);
            context.Request.Body = source;
        });
        Assert.Equal((int)HttpStatusCode.RequestEntityTooLarge, response.Response.StatusCode);
        Assert.True(source.BytesRead < source.TotalBytes, $"Read the complete {source.TotalBytes}-byte oversized upload.");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    private sealed class GeneratedUpload(long totalBytes) : Stream
    {
        public long TotalBytes => totalBytes;
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var length = (int)Math.Min(count, totalBytes - BytesRead);
            Array.Clear(buffer, offset, length);
            BytesRead += length;
            return length;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = (int)Math.Min(buffer.Length, totalBytes - BytesRead);
            buffer.Span[..length].Clear();
            BytesRead += length;
            return ValueTask.FromResult(length);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private HttpClient Owner()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            OwnerCapabilityHeaders.Name,
            TestOwnerCapability.Token(_factory.Services));
        return client;
    }
}
