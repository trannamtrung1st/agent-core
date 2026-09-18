using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Tests;

[Collection(DockerSandboxCollection.Name)]
public sealed class DockerSandboxExecutorTests
{
    [Fact]
    public void Normalize_rejects_host_network_and_shell_metacharacters()
    {
        Assert.False(SandboxCommand.TryNormalize("cat", ["/etc/passwd"], out _, out _));
        Assert.False(SandboxCommand.TryNormalize("cat", ["/workspace/working/../secret"], out _, out _));
        Assert.False(SandboxCommand.TryNormalize("wget", ["http://example"], out _, out _));
        Assert.False(SandboxCommand.TryNormalize("nc", ["127.0.0.1", "1"], out _, out _));
        Assert.False(SandboxCommand.TryNormalize("ping", ["127.0.0.1"], out _, out _));
        Assert.False(SandboxCommand.TryNormalize("sh", ["-c", "echo hi"], out _, out _));
        Assert.False(SandboxCommand.TryNormalize("echo", ["$(id)"], out _, out _));
        Assert.False(SandboxCommand.TryNormalize("sleep", ["99"], out _, out _));
        Assert.True(SandboxCommand.TryNormalize("echo", ["hello"], out var argv, out _));
        Assert.Equal(["echo", "hello"], argv);
    }

    [DockerSandboxFact]
    public async Task Echo_applies_isolation_limits_and_removes_the_container()
    {
        using var dir = new TempDir();
        var session = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot);
        var executor = new DockerSandboxExecutor(workspace, dockerPath: DockerSandboxProbe.Path ?? "docker");
        var result = await executor.RunAsync(new SandboxRequest(session, runId, Examiner(), "echo", ["sandbox-ok"]));
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("sandbox-ok", result.Output, StringComparison.Ordinal);
        Assert.Empty(Leftover(runId));

        using var host = JsonDocument.Parse(executor.LastHostConfigJson ?? "{}");
        var config = host.RootElement;
        Assert.Equal("none", config.GetProperty("NetworkMode").GetString());
        Assert.Equal(SandboxLimits.MemoryBytes, config.GetProperty("Memory").GetInt64());
        Assert.Equal(SandboxLimits.PidsLimit, config.GetProperty("PidsLimit").GetInt64());
        Assert.Equal(SandboxLimits.NanoCpus, config.GetProperty("NanoCpus").GetInt64());
        Assert.True(config.GetProperty("ReadonlyRootfs").GetBoolean());
        Assert.False(config.GetProperty("Privileged").GetBoolean());
        Assert.Contains(
            config.GetProperty("CapDrop").EnumerateArray().Select(item => item.GetString()),
            item => string.Equals(item, "ALL", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            config.GetProperty("SecurityOpt").EnumerateArray().Select(item => item.GetString() ?? ""),
            item => item.Contains("no-new-privileges", StringComparison.OrdinalIgnoreCase));

        using var mounts = JsonDocument.Parse(executor.LastMountsJson ?? "[]");
        var entries = mounts.RootElement.EnumerateArray().ToArray();
        Assert.Single(entries);
        Assert.Equal("/workspace/working", entries[0].GetProperty("Destination").GetString());
        Assert.Equal(
            workspace.PhysicalWorkingDirectory(session),
            entries[0].GetProperty("Source").GetString());
    }

    [DockerSandboxFact]
    public async Task Host_and_other_session_files_are_not_readable()
    {
        using var dir = new TempDir();
        var hostSecret = Path.Combine(dir.Root, "host-secret.txt");
        await File.WriteAllTextAsync(hostSecret, "HOST_SECRET");
        var sessionA = Guid.CreateVersion7();
        var sessionB = Guid.CreateVersion7();
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot);
        await workspace.EnsureAsync(sessionA, Examiner());
        await workspace.WriteAsync(sessionA, "/workspace/working/secret.txt", "SESSION_A"u8.ToArray());
        await workspace.EnsureAsync(sessionB, Examiner());

        var executor = new DockerSandboxExecutor(workspace, dockerPath: DockerSandboxProbe.Path ?? "docker");
        var denied = await executor.RunAsync(
            new SandboxRequest(sessionA, Guid.CreateVersion7(), Examiner(), "cat", [hostSecret.Replace('\\', '/')]));
        Assert.False(denied.Succeeded);
        Assert.Contains("limited to /workspace/working", denied.SafeMessage, StringComparison.OrdinalIgnoreCase);

        var fromB = await executor.RunAsync(
            new SandboxRequest(sessionB, Guid.CreateVersion7(), Examiner(), "cat", ["/workspace/working/secret.txt"]));
        Assert.False(fromB.Succeeded);

        var fromA = await executor.RunAsync(
            new SandboxRequest(sessionA, Guid.CreateVersion7(), Examiner(), "cat", ["/workspace/working/secret.txt"]));
        Assert.True(fromA.Succeeded);
        Assert.Contains("SESSION_A", fromA.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("HOST_SECRET", fromA.Output, StringComparison.Ordinal);
    }

    [DockerSandboxFact]
    public async Task Cancel_kills_and_reaps_the_container()
    {
        using var dir = new TempDir();
        var session = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot);
        var executor = new DockerSandboxExecutor(workspace, dockerPath: DockerSandboxProbe.Path ?? "docker");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.RunAsync(new SandboxRequest(session, runId, Examiner(), "sleep", ["5"]), cts.Token).AsTask());
        Assert.Empty(Leftover(runId));
    }

    [DockerSandboxFact]
    public async Task Export_creates_a_session_scoped_artifact()
    {
        using var dir = new TempDir();
        var session = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot);
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var executor = new DockerSandboxExecutor(workspace, artifacts, dockerPath: DockerSandboxProbe.Path ?? "docker");
        await workspace.EnsureAsync(session, Examiner());
        await workspace.WriteAsync(session, "/workspace/working/note.txt", "exported"u8.ToArray());

        var result = await executor.RunAsync(
            new SandboxRequest(
                session,
                Guid.CreateVersion7(),
                Examiner(),
                "echo",
                ["ok"],
                "/workspace/working/note.txt"));
        Assert.True(result.Succeeded);
        Assert.NotNull(result.ArtifactId);
        Assert.True(artifacts.Exists(session, result.ArtifactId.Value));
        Assert.False(artifacts.Exists(other, result.ArtifactId.Value));
        await using var stream = await artifacts.OpenContentAsync(session, result.ArtifactId.Value);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal("exported", await reader.ReadToEndAsync());

        var denied = await executor.RunAsync(
            new SandboxRequest(
                session,
                Guid.CreateVersion7(),
                Examiner(),
                "echo",
                ["ok"],
                "/workspace/artifacts/note.txt"));
        Assert.False(denied.Succeeded);
    }

    [DockerSandboxFact]
    public async Task Failed_command_still_removes_the_container()
    {
        using var dir = new TempDir();
        var session = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot);
        var executor = new DockerSandboxExecutor(workspace, dockerPath: DockerSandboxProbe.Path ?? "docker");
        await workspace.EnsureAsync(session, Examiner());
        var result = await executor.RunAsync(
            new SandboxRequest(session, runId, Examiner(), "cat", ["/workspace/working/missing.txt"]));
        Assert.False(result.Succeeded);
        Assert.Empty(Leftover(runId));
    }

    [DockerSandboxFact]
    public async Task Cat_output_is_bounded_to_max_bytes()
    {
        using var dir = new TempDir();
        var session = Guid.CreateVersion7();
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot);
        var executor = new DockerSandboxExecutor(workspace, dockerPath: DockerSandboxProbe.Path ?? "docker");
        await workspace.EnsureAsync(session, Examiner());
        await workspace.WriteAsync(session, "/workspace/working/large.txt", Encoding.UTF8.GetBytes(new string('a', 200_000)));

        var result = await executor.RunAsync(
            new SandboxRequest(session, Guid.CreateVersion7(), Examiner(), "cat", ["/workspace/working/large.txt"]));
        Assert.True(result.Succeeded);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(result.Output), 0, SandboxLimits.MaxOutputBytes);
        Assert.True(result.Truncated);
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
    }

    private static string[] Leftover(Guid runId)
    {
        var name = $"acsbx-{runId:N}";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = DockerSandboxProbe.Path ?? "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add("ps");
        process.StartInfo.ArgumentList.Add("-aq");
        process.StartInfo.ArgumentList.Add("--filter");
        process.StartInfo.ArgumentList.Add($"name={name}");
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    private static AgentDefinition Examiner() => new(
        1,
        "examiner",
        1,
        new AgentIdentity("Alex", "Speaking examiner", "Practice a speaking examination.", "Calm, formal and patient"),
        ["Conduct a realistic practice speaking examination"],
        "You are Alex, a practice examiner.",
        new BehaviorPolicy("acknowledgeThenContinue", true, true),
        new ConversationPolicy("concise", true, "en", 256),
        new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
        new VoiceConfiguration(true, "default", 1.0),
        new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
        new Dictionary<string, string> { ["scenario"] = "practice-exam" });

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "agent-core-sbx", Guid.NewGuid().ToString("N"));
            WorkspaceRoot = Path.Combine(Root, "workspaces");
            TemplateRoot = Path.Combine(Root, "templates");
            Directory.CreateDirectory(WorkspaceRoot);
            Directory.CreateDirectory(TemplateRoot);
        }

        public string Root { get; }
        public string WorkspaceRoot { get; }
        public string TemplateRoot { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}

public sealed class DockerSandboxFixture;

[CollectionDefinition(DockerSandboxCollection.Name, DisableParallelization = true)]
public sealed class DockerSandboxCollection : ICollectionFixture<DockerSandboxFixture>
{
    public const string Name = "docker-sandbox";
}

public sealed class DockerSandboxFactAttribute : FactAttribute
{
    public DockerSandboxFactAttribute()
    {
        if (!DockerSandboxProbe.Ready)
        {
            Skip = DockerSandboxProbe.Reason;
        }
    }
}

internal static class DockerSandboxProbe
{
    static DockerSandboxProbe()
    {
        try
        {
            Path = Resolve();
            if (Path is null)
            {
                Reason = "Docker CLI is not available.";
                return;
            }

            if (Run(Path, "version") != 0)
            {
                Reason = "Docker CLI is not available.";
                return;
            }

            if (Run(Path, "image", "inspect", SandboxLimits.Image) != 0)
            {
                Reason = $"{SandboxLimits.Image} is not present.";
                return;
            }

            Ready = true;
            Reason = "";
        }
        catch (Exception ex)
        {
            Reason = $"Docker CLI is not available ({ex.GetType().Name}).";
        }
    }

    public static bool Ready { get; }
    public static string Reason { get; } = "Docker CLI is not available.";
    public static string? Path { get; }

    private static string? Resolve()
    {
        foreach (var candidate in new[]
                 {
                     "docker",
                     "/usr/local/bin/docker",
                     "/opt/homebrew/bin/docker",
                     "/usr/bin/docker"
                 })
        {
            try
            {
                if (Run(candidate, "version") == 0)
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

    private static int Run(string file, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = file,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        process.WaitForExit();
        return process.ExitCode;
    }
}
