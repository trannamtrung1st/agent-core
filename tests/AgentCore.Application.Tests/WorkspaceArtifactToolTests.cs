using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Application.Tests;

public sealed class WorkspaceArtifactToolTests
{
    [Fact]
    public async Task Workspace_list_returns_logical_metadata_without_physical_paths()
    {
        using var dir = new TempDir();
        var session = Guid.CreateVersion7();
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot);
        var definition = WorkspaceTools();
        await workspace.EnsureAsync(session, definition);
        await workspace.WriteAsync(session, "/workspace/working/note.md", "hello"u8.ToArray());

        var executor = new SessionToolExecutor(workspace: workspace);
        var json = await ExecuteTextAsync(
            executor,
            definition,
            session,
            new ModelToolCall("c1", ToolCatalog.WorkspaceList, """{"path":"/workspace/working"}"""));
        using var doc = JsonDocument.Parse(json);
        var entries = doc.RootElement.GetProperty("entries");
        Assert.Contains(
            entries.EnumerateArray(),
            item => item.GetProperty("path").GetString() == "/workspace/working/note.md"
                && item.GetProperty("writable").GetBoolean());
        Assert.DoesNotContain("\\\\", json, StringComparison.Ordinal);
        Assert.DoesNotContain(dir.WorkspaceRoot, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Workspace_patch_applies_exact_once_and_rejects_stale_hash()
    {
        using var dir = new TempDir();
        var session = Guid.CreateVersion7();
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot);
        var definition = WorkspaceTools();
        await workspace.EnsureAsync(session, definition);
        const string path = "/workspace/working/report.md";
        await workspace.WriteAsync(session, path, "alpha beta gamma"u8.ToArray());
        var hash = Sha256Hex("alpha beta gamma");

        var executor = new SessionToolExecutor(workspace: workspace);
        var patched = await ExecuteTextAsync(
            executor,
            definition,
            session,
            new ModelToolCall(
                "c1",
                ToolCatalog.WorkspacePatch,
                $$"""{"path":"{{path}}","expectedSha256":"{{hash}}","edits":[{"oldText":"beta","newText":"BETA"}]}"""));
        Assert.Contains("\"editsApplied\":1", patched, StringComparison.Ordinal);
        var read = await workspace.ReadAsync(session, definition, path);
        Assert.Equal("alpha BETA gamma", Encoding.UTF8.GetString(read.Bytes));

        var stale = await ExecuteTextAsync(
            executor,
            definition,
            session,
            new ModelToolCall(
                "c2",
                ToolCatalog.WorkspacePatch,
                $$"""{"path":"{{path}}","expectedSha256":"{{hash}}","edits":[{"oldText":"BETA","newText":"x"}]}"""));
        Assert.Contains("Conflict", stale, StringComparison.OrdinalIgnoreCase);
        read = await workspace.ReadAsync(session, definition, path);
        Assert.Equal("alpha BETA gamma", Encoding.UTF8.GetString(read.Bytes));
    }

    [Fact]
    public async Task Artifacts_create_from_workspace_preserves_provenance_without_model_bytes()
    {
        using var dir = new TempDir();
        var session = Guid.CreateVersion7();
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot);
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var definition = WorkspaceTools();
        await workspace.EnsureAsync(session, definition);
        const string path = "/workspace/working/out.md";
        await workspace.WriteAsync(session, path, "# Title"u8.ToArray());

        var executor = new SessionToolExecutor(workspace: workspace, artifacts: artifacts);
        var result = await ExecuteTextAsync(
            executor,
            definition,
            session,
            new ModelToolCall(
                "c1",
                ToolCatalog.ArtifactsCreateFromWorkspace,
                """{"path":"/workspace/working/out.md","displayName":"out.md","contentType":"text/markdown"}"""));
        using var doc = JsonDocument.Parse(result);
        var artifactId = doc.RootElement.GetProperty("artifactId").GetGuid();
        var listed = await artifacts.ListAsync(session);
        var record = Assert.Single(listed);
        Assert.Equal(artifactId, record.ArtifactId);
        Assert.Equal(path, record.WorkspaceLogicalPath);
        Assert.Equal("text/markdown", record.ContentType);

        var verify = await ExecuteTextAsync(
            executor,
            definition,
            session,
            new ModelToolCall("c2", ToolCatalog.ArtifactsVerify, $$"""{"artifactId":"{{artifactId:D}}"}"""));
        Assert.Contains(record.Sha256Hex, verify, StringComparison.Ordinal);
        Assert.Contains(path, verify, StringComparison.Ordinal);
        Assert.Contains("createdAt", verify, StringComparison.Ordinal);
    }

    private static async Task<string> ExecuteTextAsync(
        SessionToolExecutor executor,
        AgentDefinition definition,
        Guid sessionId,
        ModelToolCall call) =>
        (await executor.ExecuteAsync(definition, sessionId, call, ToolLimits.MaxOutputBytes)).Text;

    private static AgentDefinition WorkspaceTools() => new(
        1,
        "workspace-tools",
        1,
        new AgentIdentity("W", "Workspace", "Test.", "Neutral"),
        ["Test"],
        "Test",
        new BehaviorPolicy("answerNewTurn", true, true),
        new ConversationPolicy("concise", true, "en", 256),
        new InitiativePolicy(false, 10000, 30000, 1, []),
        new VoiceConfiguration(false, "default", 1.0),
        new ProviderPreferences("primary-llm", null, null),
        new Dictionary<string, string>(),
        new RoleEnvironment(ToolAllowlist:
        [
            ToolCatalog.WorkspaceList,
            ToolCatalog.WorkspaceRead,
            ToolCatalog.WorkspaceWrite,
            ToolCatalog.WorkspacePatch,
            ToolCatalog.ArtifactsVerify,
            ToolCatalog.ArtifactsCreateFromWorkspace
        ]));

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "agent-core-ws-tools", Guid.NewGuid().ToString("N"));
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
