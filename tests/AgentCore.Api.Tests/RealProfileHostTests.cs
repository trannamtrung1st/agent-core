using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AgentCore.Contracts.Http;

namespace AgentCore.Api.Tests;

public sealed class RealComposeHostFixture : IAsyncLifetime
{
    private Process? _process;

    public string BaseAddress { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var root = FindRepoRoot();
        var port = GetFreePort();
        BaseAddress = $"http://127.0.0.1:{port}";
        var start = new ProcessStartInfo(
            "dotnet",
            $"run --project \"{Path.Combine(root, "src", "AgentCore.Api", "AgentCore.Api.csproj")}\" --no-launch-profile --urls {BaseAddress}")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        // Same keys as docker-compose.yml + docker-compose.real.yml (process env, like the container).
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["AgentCore__Profile"] = "Real";
        start.Environment["AgentCore__AgentDirectory"] = Path.Combine(root, "agents");
        start.Environment["Persistence__Provider"] = "InMemory";
        start.Environment["Persistence__WorkspaceRoot"] = Path.Combine(Path.GetTempPath(), "real-profile-fixture-" + Guid.NewGuid().ToString("N"), "workspaces");
        start.Environment["Providers__LanguageModels__primary-llm__Adapter"] = "OpenAICompatible";
        start.Environment["Providers__LanguageModels__primary-llm__BaseUrl"] = "https://openrouter.ai/api/v1/";
        start.Environment["Providers__LanguageModels__primary-llm__DefaultModel"] = "deepseek/deepseek-v4.1-flash";
        start.Environment["Providers__LanguageModels__primary-llm__ReasoningEffort"] = "medium";
        start.Environment["Providers__LanguageModels__primary-llm__Vision"] = "false";
        start.Environment["Providers__LanguageModels__primary-llm__Tools"] = "true";
        start.Environment["Providers__ModelCatalog__DefaultKey"] = "deepseek-v41-flash";
        start.Environment["Providers__ModelCatalog__Models__0__Key"] = "deepseek-v41-flash";
        start.Environment["Providers__ModelCatalog__Models__0__DisplayName"] = "DeepSeek V4.1 Flash";
        start.Environment["Providers__ModelCatalog__Models__0__ProviderAlias"] = "primary-llm";
        start.Environment["Providers__ModelCatalog__Models__0__ModelId"] = "deepseek/deepseek-v4.1-flash";
        start.Environment["Providers__ModelCatalog__Models__0__Tools"] = "true";
        start.Environment["Providers__ModelCatalog__Models__0__Vision"] = "false";
        start.Environment["Providers__ModelCatalog__Models__0__StructuredOutput"] = "false";
        start.Environment["Providers__ModelCatalog__Models__0__Reasoning"] = "true";
        start.Environment["Providers__ModelCatalog__Models__0__SupportedReasoningEfforts__0"] = "low";
        start.Environment["Providers__ModelCatalog__Models__0__SupportedReasoningEfforts__1"] = "medium";
        start.Environment["Providers__ModelCatalog__Models__0__SupportedReasoningEfforts__2"] = "high";
        start.Environment["Providers__ModelCatalog__Models__0__DefaultReasoningEffort"] = "medium";
        start.Environment["Providers__ModelCatalog__Models__1__Key"] = "gpt-4o-mini-2024-07-18";
        start.Environment["Providers__ModelCatalog__Models__1__DisplayName"] = "GPT-4o mini 2024-07-18";
        start.Environment["Providers__ModelCatalog__Models__1__ProviderAlias"] = "primary-llm";
        start.Environment["Providers__ModelCatalog__Models__1__ModelId"] = "openai/gpt-4o-mini-2024-07-18";
        start.Environment["Providers__ModelCatalog__Models__1__Tools"] = "true";
        start.Environment["Providers__ModelCatalog__Models__1__Vision"] = "true";
        start.Environment["Providers__ModelCatalog__Models__1__StructuredOutput"] = "true";
        start.Environment["Providers__ModelCatalog__Models__1__Reasoning"] = "false";
        start.Environment["Providers__ModelCatalog__Models__2__Key"] = "openrouter-free";
        start.Environment["Providers__ModelCatalog__Models__2__DisplayName"] = "OpenRouter Free";
        start.Environment["Providers__ModelCatalog__Models__2__ProviderAlias"] = "primary-llm";
        start.Environment["Providers__ModelCatalog__Models__2__ModelId"] = "openrouter/free";
        start.Environment["Providers__ModelCatalog__Models__2__Tools"] = "true";
        start.Environment["Providers__ModelCatalog__Models__2__Vision"] = "false";
        start.Environment["Providers__ModelCatalog__Models__2__StructuredOutput"] = "false";
        start.Environment["Providers__ModelCatalog__Models__2__Reasoning"] = "false";
        start.Environment["Providers__ModelCatalog__Models__2__CostCategory"] = "free";
        start.Environment["Providers__ModelCatalog__Models__3__Key"] = "gpt-4.1";
        start.Environment["Providers__ModelCatalog__Models__3__DisplayName"] = "GPT-4.1";
        start.Environment["Providers__ModelCatalog__Models__3__ProviderAlias"] = "primary-llm";
        start.Environment["Providers__ModelCatalog__Models__3__ModelId"] = "openai/gpt-4.1";
        start.Environment["Providers__ModelCatalog__Models__3__Tools"] = "true";
        start.Environment["Providers__ModelCatalog__Models__3__Vision"] = "true";
        start.Environment["Providers__ModelCatalog__Models__3__StructuredOutput"] = "true";
        start.Environment["Providers__ModelCatalog__Models__3__Reasoning"] = "false";
        start.Environment["Providers__ModelCatalog__Models__4__Key"] = "gpt-5.6-luna";
        start.Environment["Providers__ModelCatalog__Models__4__DisplayName"] = "GPT-5.6 Luna";
        start.Environment["Providers__ModelCatalog__Models__4__ProviderAlias"] = "primary-llm";
        start.Environment["Providers__ModelCatalog__Models__4__ModelId"] = "openai/gpt-5.6-luna";
        start.Environment["Providers__ModelCatalog__Models__4__Tools"] = "true";
        start.Environment["Providers__ModelCatalog__Models__4__Vision"] = "true";
        start.Environment["Providers__ModelCatalog__Models__4__StructuredOutput"] = "true";
        start.Environment["Providers__ModelCatalog__Models__4__Reasoning"] = "true";
        start.Environment["Providers__ModelCatalog__Models__4__SupportedReasoningEfforts__0"] = "max";
        start.Environment["Providers__ModelCatalog__Models__4__SupportedReasoningEfforts__1"] = "xhigh";
        start.Environment["Providers__ModelCatalog__Models__4__SupportedReasoningEfforts__2"] = "high";
        start.Environment["Providers__ModelCatalog__Models__4__SupportedReasoningEfforts__3"] = "medium";
        start.Environment["Providers__ModelCatalog__Models__4__SupportedReasoningEfforts__4"] = "low";
        start.Environment["Providers__ModelCatalog__Models__4__SupportedReasoningEfforts__5"] = "none";
        start.Environment["Providers__ModelCatalog__Models__4__DefaultReasoningEffort"] = "low";
        start.Environment["OPENROUTER_API_KEY"] = "test-key-not-for-live-calls";
        _process = await ProcessHostLauncher.StartApiAsync(start, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                var health = await client.GetAsync($"{BaseAddress}/health").ConfigureAwait(false);
                if (health.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception) when (attempt < 39)
            {
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        throw new TimeoutException("Real profile /health did not become ready.");
    }

    public Task DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5_000);
        }

        _process?.Dispose();
        return Task.CompletedTask;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
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

[CollectionDefinition("dotnet-host", DisableParallelization = true)]
public sealed class DotnetHostCollection : ICollectionFixture<RealComposeHostFixture>;

[Collection("dotnet-host")]
public sealed class RealComposeHostTests(RealComposeHostFixture fixture)
{
    [Fact]
    public async Task Health_reports_real_and_voice_follows_resolvable_synthetic_speech_paths()
    {
        using var client = new HttpClient { BaseAddress = new Uri(fixture.BaseAddress) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            OwnerCapabilityHeaders.Name,
            IssueOwnerCapability(client.BaseAddress!.ToString()));
        var health = await client.GetFromJsonAsync<HealthResponse>("/health");
        Assert.Equal("healthy", health!.Status);
        Assert.Equal("Real", health.Profile);
        Assert.Equal(1, health.ProtocolVersion);

        var agents = await client.GetFromJsonAsync<AgentListResponse>("/api/v1/agents");
        Assert.NotNull(agents);
        Assert.Contains(agents!.Agents, agent => agent.Id == "examiner" && agent.VoiceAvailable);

        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdView = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        Assert.Equal("deepseek-v41-flash", createdView!.Model!.CatalogKey);
        Assert.Equal("deepseek/deepseek-v4.1-flash", createdView.Model.ModelId);
        Assert.Equal("medium", createdView.Model.ReasoningEffort);

        var models = await client.GetFromJsonAsync<ModelCatalogResponse>("/api/v2/models");
        Assert.Equal("deepseek-v41-flash", models!.DefaultKey);
        Assert.Contains(models.Models, model => model.Key == "deepseek-v41-flash" && model.Reasoning);
        Assert.Contains(models.Models, model => model.Key == "gpt-4o-mini-2024-07-18" && !model.Reasoning);
        Assert.Contains(models.Models, model => model.Key == "openrouter-free" && !model.Reasoning);
        Assert.Contains(models.Models, model => model.Key == "gpt-4.1" && model.StructuredOutput && !model.Reasoning);
        var luna = Assert.Single(models.Models, model => model.Key == "gpt-5.6-luna");
        Assert.True(luna.Tools);
        Assert.True(luna.Vision);
        Assert.True(luna.StructuredOutput);
        Assert.True(luna.Reasoning);
        Assert.Equal(["max", "xhigh", "high", "medium", "low", "none"], luna.SupportedReasoningEfforts);
        Assert.Equal("low", luna.DefaultReasoningEffort);
        Assert.DoesNotContain(models.Models, model => model.Key == "scripted-alpha");

        var mini = await client.PostAsJsonAsync(
            "/api/v2/sessions",
            new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text", Model: new SessionModelChoiceRequest("gpt-4o-mini-2024-07-18")));
        Assert.Equal(HttpStatusCode.Created, mini.StatusCode);
        var miniView = await mini.Content.ReadFromJsonAsync<SessionViewResponse>();
        Assert.Equal("gpt-4o-mini-2024-07-18", miniView!.Model!.CatalogKey);
        Assert.Equal("openai/gpt-4o-mini-2024-07-18", miniView.Model.ModelId);
        Assert.Null(miniView.Model.ReasoningEffort);

        var free = await client.PostAsJsonAsync(
            "/api/v2/sessions",
            new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text", Model: new SessionModelChoiceRequest("openrouter-free")));
        Assert.Equal(HttpStatusCode.Created, free.StatusCode);
        var freeView = await free.Content.ReadFromJsonAsync<SessionViewResponse>();
        Assert.Equal("openrouter-free", freeView!.Model!.CatalogKey);
        Assert.Equal("openrouter/free", freeView.Model.ModelId);

        var lunaSession = await client.PostAsJsonAsync(
            "/api/v2/sessions",
            new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text", Model: new SessionModelChoiceRequest("gpt-5.6-luna", "low")));
        Assert.Equal(HttpStatusCode.Created, lunaSession.StatusCode);
        var lunaView = await lunaSession.Content.ReadFromJsonAsync<SessionViewResponse>();
        Assert.Equal("gpt-5.6-luna", lunaView!.Model!.CatalogKey);
        Assert.Equal("openai/gpt-5.6-luna", lunaView.Model.ModelId);
        Assert.Equal("low", lunaView.Model.ReasoningEffort);

        var voice = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "voice"));
        Assert.Equal(HttpStatusCode.Created, voice.StatusCode);
    }

    private static string IssueOwnerCapability(string baseAddress)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseAddress) };
        var issued = http.PostAsync("/api/v1/local/owner-capability", null).GetAwaiter().GetResult();
        issued.EnsureSuccessStatusCode();
        return issued.Content.ReadFromJsonAsync<OwnerCapabilityResponse>().GetAwaiter().GetResult()!.Token;
    }
}
