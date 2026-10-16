using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AgentCore.Contracts.Http;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class ArtifactApiTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public ArtifactApiTests(AgentCoreApiFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("symlink")]
    public async Task Materialization_preserves_late_page_collisions_and_concurrent_attachment_copies(string kind)
    {
        using var client = Owner();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text"));
        var view = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        var root = $"/api/v2/sessions/{view.SessionId}";
        (await client.GetAsync(root + "/workspace?prefix=/working")).EnsureSuccessStatusCode();
        var workspace = (FileSessionWorkspace)_factory.Services.GetRequiredService<ISessionWorkspace>();
        var physical = await workspace.PhysicalWorkingDirectoryAsync(Guid.Parse(view.SessionId));
        for (var n = 0; n < 300; n++) await File.WriteAllTextAsync(Path.Combine(physical, $"a-{n:D3}"), "existing");
        await File.WriteAllTextAsync(Path.Combine(physical, "original"), "original");
        var target = Path.Combine(physical, "z-target.txt");
        if (kind == "file") await File.WriteAllTextAsync(target, "external");
        else if (kind == "directory") Directory.CreateDirectory(target);
        else File.CreateSymbolicLink(target, "original");
        var uploaded = await client.PostAsync(root + "/attachments", FileContent("z-target.txt", "attachment"));
        uploaded.EnsureSuccessStatusCode();
        var attachment = (await uploaded.Content.ReadFromJsonAsync<AttachmentResponse>())!;
        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            client.PostAsync(root + $"/attachments/{attachment.AttachmentId}/materialize", null)));
        foreach (var response in responses) Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("attachment", await File.ReadAllTextAsync(Path.Combine(physical, "z-target-2.txt")));
        Assert.Equal("attachment", await File.ReadAllTextAsync(Path.Combine(physical, "z-target-3.txt")));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(physical, "original")));
        if (kind == "file") Assert.Equal("external", await File.ReadAllTextAsync(target));
        else if (kind == "directory") Assert.True(Directory.Exists(target));
        else Assert.Equal("original", new FileInfo(target).LinkTarget);
    }

    [Fact]
    public async Task Materialize_and_download_require_capability_and_fail_closed()
    {
        var anonymous = _factory.CreateClient();
        var created = await Owner().PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(Owner(), "examiner", 1), "text"));
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
        Assert.Equal("/working/notes.txt", artifact.WorkspaceLogicalPath);

        var metadataPath = $"/api/v2/sessions/{view.SessionId}/artifacts/{artifact.ArtifactId}";
        var canonical = await Owner().GetFromJsonAsync<ArtifactResponse>(metadataPath);
        Assert.Equal("notes.txt", canonical!.DisplayName);
        Assert.Equal("text/plain", canonical.ContentType);
        Assert.Equal(Encoding.UTF8.GetByteCount("hello-artifact"), canonical.ByteSize);
        Assert.Equal(view.SessionId, canonical.SessionId);
        Assert.Equal(artifact.ArtifactId, canonical.ArtifactId);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(metadataPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(metadataPath + "/content")).StatusCode);

        var download = await Owner().GetAsync(metadataPath + "/content");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("text/plain", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal(canonical.DisplayName, download.Content.Headers.ContentDisposition.FileNameStar);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetBytes("hello-artifact"), bytes);

        var other = await Owner().PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(Owner(), "examiner", 1), "text"));
        var otherView = await other.Content.ReadFromJsonAsync<SessionViewResponse>();
        var leaked = await Owner().GetAsync(
            $"/api/v2/sessions/{otherView!.SessionId}/artifacts/{artifact.ArtifactId}");
        Assert.Equal(HttpStatusCode.NotFound, leaked.StatusCode);
        var foreignContent = await Owner().GetAsync(
            $"/api/v2/sessions/{otherView.SessionId}/artifacts/{artifact.ArtifactId}/content");
        Assert.Equal(HttpStatusCode.NotFound, foreignContent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Owner().GetAsync(
            $"/api/v2/sessions/{view.SessionId}/artifacts/{Guid.NewGuid()}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Owner().GetAsync(
            $"/api/v2/sessions/{view.SessionId}/artifacts/fixture-artifact-1/content")).StatusCode);

        // Ending retains published bytes for read-only history; durable deletion removes them.
        Assert.Equal(HttpStatusCode.NoContent, (await Owner().DeleteAsync($"/api/v1/sessions/{view.SessionId}")).StatusCode);
        Assert.Equal(bytes, await Owner().GetByteArrayAsync(metadataPath + "/content"));
        Assert.Equal(HttpStatusCode.NoContent, (await Owner().DeleteAsync($"/api/v2/sessions/{view.SessionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Owner().GetAsync(metadataPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Owner().GetAsync(metadataPath + "/content")).StatusCode);
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
