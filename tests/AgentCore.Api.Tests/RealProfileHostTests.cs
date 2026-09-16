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
        start.Environment["Providers__LanguageModels__primary-llm__Adapter"] = "OpenAICompatible";
        start.Environment["Providers__LanguageModels__primary-llm__BaseUrl"] = "https://openrouter.ai/api/v1/";
        start.Environment["Providers__LanguageModels__primary-llm__DefaultModel"] = "openai/gpt-4o-mini";
        start.Environment["OPENROUTER_API_KEY"] = "test-key-not-for-live-calls";
        _process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start API.");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _process.OutputDataReceived += (_, args) =>
        {
            if (args.Data?.Contains("Application started", StringComparison.OrdinalIgnoreCase) == true
                || args.Data?.Contains("Now listening", StringComparison.OrdinalIgnoreCase) == true)
            {
                ready.TrySetResult();
            }
        };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        timeout.Token.Register(() => ready.TrySetException(new TimeoutException("Real profile API did not start.")));
        await ready.Task.ConfigureAwait(false);
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

public sealed class RealComposeHostTests : IClassFixture<RealComposeHostFixture>
{
    private readonly RealComposeHostFixture _fixture;

    public RealComposeHostTests(RealComposeHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Health_reports_real_and_text_is_available_without_voice()
    {
        using var client = new HttpClient { BaseAddress = new Uri(_fixture.BaseAddress) };
        var health = await client.GetFromJsonAsync<HealthResponse>("/health");
        Assert.Equal("healthy", health!.Status);
        Assert.Equal("Real", health.Profile);
        Assert.Equal(1, health.ProtocolVersion);

        var agents = await client.GetFromJsonAsync<AgentListResponse>("/api/v1/agents");
        Assert.NotNull(agents);
        Assert.All(agents!.Agents, agent => Assert.False(agent.VoiceAvailable));

        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var voice = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "voice"));
        Assert.Equal(HttpStatusCode.Conflict, voice.StatusCode);
    }
}
