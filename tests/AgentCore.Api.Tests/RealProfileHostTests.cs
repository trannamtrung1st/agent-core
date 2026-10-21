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
        start.Environment["Providers__LanguageModels__primary-llm__ReasoningEffort"] = "low";
        start.Environment["Providers__LanguageModels__primary-llm__Vision"] = "false";
        start.Environment["Providers__LanguageModels__primary-llm__Tools"] = "true";
        start.Environment["Providers__ModelCatalog__DefaultKey"] = "deepseek-v41-flash";
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
