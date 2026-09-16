using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AgentCore.Contracts.Http;

namespace AgentCore.Api.Tests;

public sealed class ArtifactApiTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public ArtifactApiTests(AgentCoreApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Materialize_and_download_require_capability_and_fail_closed()
    {
        var anonymous = _factory.CreateClient();
        var created = await Owner().PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        var uploaded = await Owner().PostAsync(
            $"/api/v2/sessions/{view!.SessionId}/attachments",
            FileContent("notes.txt", "hello-artifact"));
        var attachment = await uploaded.Content.ReadFromJsonAsync<AttachmentResponse>();

        var denied = await anonymous.PostAsync(
            $"/api/v2/sessions/{view.SessionId}/attachments/{attachment!.AttachmentId}/materialize",
            null);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var materialized = await Owner().PostAsync(
            $"/api/v2/sessions/{view.SessionId}/attachments/{attachment.AttachmentId}/materialize",
            null);
        Assert.Equal(HttpStatusCode.Created, materialized.StatusCode);
        var artifact = await materialized.Content.ReadFromJsonAsync<ArtifactResponse>();
        Assert.Equal(attachment.Sha256, artifact!.Sha256);
        Assert.Equal(attachment.AttachmentId, artifact.SourceAttachmentId);
        Assert.Equal("/workspace/working/notes.txt", artifact.WorkspaceLogicalPath);

        var bytes = await Owner().GetByteArrayAsync(
            $"/api/v2/sessions/{view.SessionId}/artifacts/{artifact.ArtifactId}/content");
        Assert.Equal("hello-artifact", Encoding.UTF8.GetString(bytes));

        var other = await Owner().PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var otherView = await other.Content.ReadFromJsonAsync<SessionViewResponse>();
        var leaked = await Owner().GetAsync(
            $"/api/v2/sessions/{otherView!.SessionId}/artifacts/{artifact.ArtifactId}");
        Assert.Equal(HttpStatusCode.NotFound, leaked.StatusCode);
    }

    private HttpClient Owner()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            OwnerCapabilityHeaders.Name,
            TestOwnerCapability.Token(_factory.Services));
        return client;
    }

    private static MultipartFormDataContent FileContent(string name, string text)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", name);
        return content;
    }
}
