using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AgentCore.Api.Tests;

public sealed class KestrelHostFixture : IAsyncLifetime
{
    private Process? _process;

    public string BaseAddress { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var root = FindRepoRoot();
        var port = GetFreePort();
        BaseAddress = $"http://127.0.0.1:{port}";
        var start = new ProcessStartInfo("dotnet", $"run --project \"{Path.Combine(root, "src", "AgentCore.Api", "AgentCore.Api.csproj")}\" --no-launch-profile --urls {BaseAddress}")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["AgentCore__Profile"] = "Synthetic";
        start.Environment["AgentCore__MaxActiveSessions"] = "1";
        start.Environment["AgentCore__AgentDirectory"] = Path.Combine(root, "agents");
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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        timeout.Token.Register(() => ready.TrySetException(new TimeoutException("API did not start.")));
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

        throw new InvalidOperationException("Health endpoint never became ready.");
    }

    public async Task DisposeAsync()
    {
        if (_process is null)
        {
            return;
        }

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }

        _process.Dispose();
    }

    public async Task RunJsAsync(string scenario)
    {
        var root = FindRepoRoot();
        var jsDir = Path.Combine(root, "tests", "realtime-js");
        var start = new ProcessStartInfo("node", $"client.mjs {scenario}")
        {
            WorkingDirectory = jsDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.Environment["BASE_URL"] = BaseAddress;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("node failed to start.");
        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"scenario {scenario} failed ({process.ExitCode}): {stdout}{stderr}");
        }
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

[CollectionDefinition("kestrel")]
public sealed class KestrelCollection : ICollectionFixture<KestrelHostFixture>;

[Collection("kestrel")]
public sealed class SignalRMessagePackTests(KestrelHostFixture host)
{
    [Theory]
    [InlineData("text-roundtrip")]
    [InlineData("protocol-version")]
    [InlineData("client-correlation")]
    [InlineData("second-connection")]
    [InlineData("stale-sequence")]
    [InlineData("capacity")]
    [InlineData("exact-retry")]
    [InlineData("eventid-reuse")]
    [InlineData("missing-attachment")]
    [InlineData("method-type-mismatch")]
    [InlineData("command-gap")]
    [InlineData("fatal-close")]
    [InlineData("response-received")]
    [InlineData("audio-session-mismatch")]
    [InlineData("speech-boundary-order")]
    [InlineData("older-retry")]
    [InlineData("reconnect-retry")]
    [InlineData("stale-attachment")]
    [InlineData("oversized-audio")]
    [InlineData("playback-invalid")]
    [InlineData("parallel-controls")]
    [InlineData("receipt-backwards")]
    [InlineData("dual-attach")]
    [InlineData("user-text-unknown-behavior")]
    [InlineData("user-text-behavior-retry")]
    [InlineData("user-text-queue")]
    [InlineData("user-text-interrupt-live")]
    [InlineData("user-text-omit-interrupt-live")]
    [InlineData("user-text-interrupt-after-queue")]
    [InlineData("cancel-response-unknown")]
    [InlineData("cancel-response-idempotent")]
    [InlineData("cancel-response-stale")]
    [InlineData("cancel-response-active")]
    public Task JavaScript_messagepack_scenarios(string scenario) => host.RunJsAsync(scenario);
}
