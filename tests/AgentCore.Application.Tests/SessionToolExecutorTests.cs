using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Application.Tests;

public sealed class SessionToolExecutorTests
{
    [Fact]
    public async Task Host_paths_and_session_mutation_arguments_are_denied()
    {
        var workspace = new RecordingWorkspace();
        var executor = new SessionToolExecutor(workspace: workspace);
        var sessionId = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");
        var host = await executor.ExecuteAsync(
            Support(),
            sessionId,
            new ModelToolCall("c1", ToolCatalog.WorkspaceWrite, """{"path":"/etc/passwd","content":"x"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("forbidden", host, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(workspace.Writes);

        var windows = await executor.ExecuteAsync(
            Support(),
            sessionId,
            new ModelToolCall("c2", ToolCatalog.WorkspaceWrite, """{"path":"C:\\Windows\\system32","content":"x"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("forbidden", windows, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(workspace.Writes);

        var mutate = await executor.ExecuteAsync(
            Support(),
            sessionId,
            new ModelToolCall("c3", ToolCatalog.KnowledgeRetrieve, """{"identity":"support-order-policy","mutateSession":true}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("forbidden", mutate, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Process_and_malformed_payloads_are_denied()
    {
        var executor = new SessionToolExecutor();
        var process = await executor.ExecuteAsync(
            Support(),
            Guid.NewGuid(),
            new ModelToolCall("c1", "process", """{"cmd":"ls"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("forbidden", process, StringComparison.OrdinalIgnoreCase);

        var malformed = await executor.ExecuteAsync(
            Support(),
            Guid.NewGuid(),
            new ModelToolCall("c2", ToolCatalog.KnowledgeRetrieve, "{"),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("invalid", malformed, StringComparison.OrdinalIgnoreCase);

        var sandbox = new RecordingSandbox();
        var withSandbox = new SessionToolExecutor(sandbox: sandbox);
        foreach (var name in new[] { "process", "shell", "bash", "cmd", "powershell", "exec" })
        {
            var denied = await withSandbox.ExecuteAsync(
                Support(),
                Guid.NewGuid(),
                new ModelToolCall("c3", name, """{"cmd":"ls"}"""),
                ToolLimits.MaxOutputBytes);
            Assert.Contains("forbidden", denied, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Null(sandbox.Last);
    }

    [Fact]
    public async Task Generated_artifact_create_ignores_fabricated_source_attachment()
    {
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var executor = new SessionToolExecutor(artifacts: artifacts);
        var sessionId = Guid.NewGuid();
        var fabricated = Guid.NewGuid();
        var result = await executor.ExecuteAsync(
            Artifacts(),
            sessionId,
            new ModelToolCall(
                "c1",
                ToolCatalog.ArtifactsCreate,
                $$"""{"displayName":"note.md","content":"hello","sourceAttachmentId":"{{fabricated:D}}"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("\"artifactId\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain(fabricated.ToString("D"), result, StringComparison.Ordinal);
        var listed = await artifacts.ListAsync(sessionId);
        Assert.Single(listed);
        Assert.Null(listed[0].SourceAttachmentId);
    }

    [Fact]
    public async Task Sandbox_run_requires_allowlist_and_uses_the_executor()
    {
        var sandbox = new RecordingSandbox();
        var executor = new SessionToolExecutor(sandbox: sandbox);
        var forbidden = await executor.ExecuteAsync(
            Support(),
            Guid.NewGuid(),
            new ModelToolCall("c1", ToolCatalog.SandboxRun, """{"verb":"echo","arguments":["hi"]}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("forbidden", forbidden, StringComparison.OrdinalIgnoreCase);
        Assert.Null(sandbox.Last);

        var allowed = await executor.ExecuteAsync(
            Sandboxed(),
            Guid.NewGuid(),
            new ModelToolCall("c2", ToolCatalog.SandboxRun, """{"verb":"echo","arguments":["hi"]}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("\"ok\":true", allowed, StringComparison.Ordinal);
        Assert.NotNull(sandbox.Last);
        Assert.Equal("echo", sandbox.Last!.Verb);

        var missing = new SessionToolExecutor();
        var unavailable = await missing.ExecuteAsync(
            Sandboxed(),
            Guid.NewGuid(),
            new ModelToolCall("c3", ToolCatalog.SandboxRun, """{"verb":"echo"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("unavailable", unavailable, StringComparison.OrdinalIgnoreCase);
    }

    private static AgentDefinition Support() => new(
        1,
        "customer-support",
        1,
        new AgentIdentity("Sam", "Support", "Help.", "Warm"),
        ["Help"],
        "Help.",
        new BehaviorPolicy("answerNewTurn", true, true),
        new ConversationPolicy("concise", true, "en", 256),
        new InitiativePolicy(true, 10000, 30000, 1, ["longSilence"]),
        new VoiceConfiguration(false, "default", 1.0),
        new ProviderPreferences("primary-llm", null, null),
        new Dictionary<string, string>(),
        new RoleEnvironment(
            ToolAllowlist:
            [
                ToolCatalog.KnowledgeRetrieve,
                ToolCatalog.WorkspaceWrite
            ]));

    private static AgentDefinition Sandboxed() => Support() with
    {
        Environment = new RoleEnvironment(ToolAllowlist: [ToolCatalog.SandboxRun])
    };

    private static AgentDefinition Artifacts() => Support() with
    {
        Environment = new RoleEnvironment(ToolAllowlist: [ToolCatalog.ArtifactsCreate])
    };

    private sealed class RecordingSandbox : ISandboxExecutor
    {
        public SandboxRequest? Last { get; private set; }

        public ValueTask<SandboxResult> RunAsync(SandboxRequest request, CancellationToken cancellationToken = default)
        {
            Last = request;
            return ValueTask.FromResult(new SandboxResult(true, 0, "hi", null, "ok"));
        }
    }

    private sealed class RecordingWorkspace : ISessionWorkspace
    {
        public List<string> Writes { get; } = [];

        public ValueTask EnsureAsync(Guid sessionId, AgentDefinition definition, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<WorkspaceNode>> ListAsync(
            Guid sessionId,
            AgentDefinition definition,
            string prefix,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<WorkspaceNode>>([]);

        public ValueTask<WorkspaceContent> ReadAsync(
            Guid sessionId,
            AgentDefinition definition,
            string logicalPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask WriteAsync(
            Guid sessionId,
            string logicalPath,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default)
        {
            Writes.Add(logicalPath);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
