using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgentCore.Contracts.Http;

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

        var listed = await Owner().GetFromJsonAsync<WorkspaceNodeResponse[]>(
            $"/api/v2/sessions/{view.SessionId}/workspace?prefix=/working");
        Assert.Empty(listed!);
        var agentNodes = await Owner().GetFromJsonAsync<WorkspaceNodeResponse[]>($"/api/v2/sessions/{view.SessionId}/workspace?prefix=/agent");
        Assert.Contains(agentNodes!, node => node.LogicalPath == "/agent/definition.json");
        var attachmentNodes = await Owner().GetFromJsonAsync<WorkspaceNodeResponse[]>($"/api/v2/sessions/{view.SessionId}/workspace?prefix=/attachments");
        Assert.Empty(attachmentNodes!);

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

    private HttpClient Owner()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            OwnerCapabilityHeaders.Name,
            TestOwnerCapability.Token(_factory.Services));
        return client;
    }
}
