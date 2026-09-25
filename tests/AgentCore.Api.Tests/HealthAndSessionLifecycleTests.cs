using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using AgentCore.Api.Http;
using AgentCore.Contracts.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class HealthAndSessionLifecycleTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public HealthAndSessionLifecycleTests(AgentCoreApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_is_synthetic_and_performs_no_outbound_http()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("healthy", body!.Status);
        Assert.Equal("Synthetic", body.Profile);
        Assert.Equal(1, body.ProtocolVersion);
        Assert.Equal(0, _factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }

    [Fact]
    public async Task Synthetic_agents_advertise_voice_when_speech_paths_resolve()
    {
        var client = _factory.CreateClient();
        var agents = await client.GetFromJsonAsync<AgentListResponse>("/api/v1/agents");
        Assert.Contains(agents!.Agents, agent => agent.Id == "examiner" && agent.VoiceAvailable);
    }

    [Fact]
    public async Task General_assistant_publishes_auto_conversation_language_policy()
    {
        var client = _factory.CreateClient();
        var agents = await client.GetFromJsonAsync<AgentListResponse>("/api/v1/agents");
        var riley = Assert.Single(agents!.Agents, agent => agent.Id == "general-assistant");
        Assert.Equal("auto", riley.Language);
        Assert.Contains(agents.Agents, agent => agent.Id == "examiner" && agent.Language == "en");
    }

    [Fact]
    public async Task V1_session_create_rejects_managed_instance_identity()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var response = await client.PostAsJsonAsync(
            "/api/v1/sessions",
            new CreateSessionRequest(null, null, "text", AgentInstanceId: Guid.CreateVersion7()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Session_lifecycle_create_get_history_end()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(created.Headers.Location);
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        Assert.Equal("examiner", view!.AgentId);
        Assert.Equal("created", view.Status);
        Assert.Equal("text", view.Mode);
        Assert.Null(view.PendingMode);

        var fetched = await client.GetFromJsonAsync<SessionViewResponse>($"/api/v1/sessions/{view.SessionId}");
        Assert.Equal(view.SessionId, fetched!.SessionId);

        var history = await client.GetFromJsonAsync<HistoryPageResponse>(
            $"/api/v1/sessions/{view.SessionId}/messages?after=0&limit=50");
        Assert.NotNull(history);
        Assert.Empty(history!.Items);

        var ended = await client.DeleteAsync($"/api/v1/sessions/{view.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        var endedView = await client.GetFromJsonAsync<SessionViewResponse>($"/api/v1/sessions/{view.SessionId}");
        Assert.Equal("ended", endedView!.Status);
        var endedHistory = await client.GetFromJsonAsync<HistoryPageResponse>(
            $"/api/v1/sessions/{view.SessionId}/messages?after=0&limit=50");
        Assert.NotNull(endedHistory);
        var endedAgain = await client.DeleteAsync($"/api/v1/sessions/{view.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, endedAgain.StatusCode);
        Assert.Equal(0, _factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }

    [Fact]
    public async Task Session_view_exposes_lifecycle_status_without_private_policy()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal("active", createdDoc.RootElement.GetProperty("lifecycleStatus").GetString());
        Assert.False(createdDoc.RootElement.TryGetProperty("completionPolicy", out _));
        Assert.False(createdDoc.RootElement.TryGetProperty("purpose", out _));
        Assert.False(createdDoc.RootElement.TryGetProperty("userCompletionAllowed", out _));

        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        var complete = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{view!.SessionId}/lifecycle",
            new TransitionLifecycleRequest("completed", "user", "finished"));
        complete.EnsureSuccessStatusCode();
        var catalog = await complete.Content.ReadFromJsonAsync<SessionCatalogItemResponse>();
        Assert.Equal("ended", catalog!.Status);
        Assert.Equal("completed", catalog.LifecycleStatus);

        using var fetchedDoc = JsonDocument.Parse(
            await (await client.GetAsync($"/api/v2/sessions/{view.SessionId}")).Content.ReadAsStringAsync());
        Assert.Equal("ended", fetchedDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal("completed", fetchedDoc.RootElement.GetProperty("lifecycleStatus").GetString());
        Assert.False(fetchedDoc.RootElement.TryGetProperty("completionPolicy", out _));
    }

    [Fact]
    public async Task Synthetic_voice_create_is_available()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "voice"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        Assert.Equal("voice", view!.Mode);
        Assert.Equal(0, _factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }

    [Fact]
    public async Task There_is_no_public_text_chat_http_endpoint()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", null, "text"));
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        var chat = await client.PostAsJsonAsync($"/api/v1/sessions/{view!.SessionId}/messages", new { text = "hi" });
        Assert.Equal(HttpStatusCode.MethodNotAllowed, chat.StatusCode);
        var legacy = await client.PostAsJsonAsync("/api/v1/chat", new { text = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, legacy.StatusCode);
    }

    [Fact]
    public void Project_reference_graph_matches_docs_11()
    {
        var root = FindRepoRoot();
        Assert.DoesNotContain("Microsoft.AspNetCore", File.ReadAllText(Path.Combine(root, "src/AgentCore.Domain/AgentCore.Domain.csproj")));
        Assert.DoesNotContain("ProjectReference Include=\"..\\AgentCore.Infrastructure", File.ReadAllText(Path.Combine(root, "src/AgentCore.Application/AgentCore.Application.csproj")));
        Assert.DoesNotContain("ProjectReference Include=\"..\\AgentCore.Contracts", File.ReadAllText(Path.Combine(root, "src/AgentCore.Application/AgentCore.Application.csproj")));
        Assert.DoesNotContain("Contracts", File.ReadAllText(Path.Combine(root, "src/AgentCore.Infrastructure/AgentCore.Infrastructure.csproj")));
        Assert.Contains("AgentCore.Domain", File.ReadAllText(Path.Combine(root, "src/AgentCore.Application/AgentCore.Application.csproj")));
        Assert.Contains("AgentCore.Application", File.ReadAllText(Path.Combine(root, "src/AgentCore.Infrastructure/AgentCore.Infrastructure.csproj")));
        Assert.Contains("AgentCore.Contracts", File.ReadAllText(Path.Combine(root, "src/AgentCore.Api/AgentCore.Api.csproj")));

        var domainRefs = PackageOrProjectRefs(Path.Combine(root, "src/AgentCore.Domain/AgentCore.Domain.csproj"));
        Assert.Empty(domainRefs);
    }

    [Fact]
    public async Task Chat_agent_instances_list_active_managed_rows_only()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var create = await client.PostAsJsonAsync(
            "/api/v2/admin/agent-instances",
            new AdminCreateAgentInstanceRequest("examiner", 1));
        create.EnsureSuccessStatusCode();
        var managed = await create.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>();
        Assert.NotNull(managed);

        var archive = await client.PatchAsJsonAsync(
            $"/api/v2/admin/agent-instances/{managed!.InstanceId}/lifecycle",
            new AdminUpdateAgentInstanceLifecycleRequest(managed.Revision, "Archived"));
        archive.EnsureSuccessStatusCode();

        var list = await client.GetFromJsonAsync<ChatAgentInstanceListResponse>("/api/v2/agent-instances");
        Assert.NotNull(list);
        Assert.DoesNotContain(list!.Items, item => item.InstanceId == managed.InstanceId);

        var createSecond = await client.PostAsJsonAsync(
            "/api/v2/admin/agent-instances",
            new AdminCreateAgentInstanceRequest("examiner", 1));
        createSecond.EnsureSuccessStatusCode();
        var active = await createSecond.Content.ReadFromJsonAsync<AdminAgentInstanceResponse>();
        Assert.NotNull(active);

        list = await client.GetFromJsonAsync<ChatAgentInstanceListResponse>("/api/v2/agent-instances");
        Assert.Contains(list!.Items, item => item.InstanceId == active!.InstanceId && item.DefinitionId == "examiner");
    }

    [Fact]
    public void Frontend_and_contracts_do_not_embed_provider_credentials()
    {
        var root = FindRepoRoot();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "web"), "*", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                    && !path.Contains($"{Path.DirectorySeparatorChar}dist{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            if (new FileInfo(file).Length > 2_000_000)
            {
                continue;
            }

            var text = File.ReadAllText(file);
            Assert.DoesNotContain("OPENROUTER_API_KEY", text, StringComparison.Ordinal);
            Assert.DoesNotContain("OPENAI_API_KEY", text, StringComparison.Ordinal);
        }

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src/AgentCore.Contracts"), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("OPENROUTER_API_KEY", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ApiKey", text, StringComparison.Ordinal);
        }
    }

    private static IReadOnlyList<string> PackageOrProjectRefs(string path)
    {
        var document = XDocument.Load(path);
        return document.Descendants()
            .Where(element => element.Name.LocalName is "PackageReference" or "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToArray();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentCore.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException();
    }
}
