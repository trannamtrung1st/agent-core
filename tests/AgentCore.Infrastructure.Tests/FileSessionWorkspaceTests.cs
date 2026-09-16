using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Tests;

public sealed class FileSessionWorkspaceTests
{
    [Fact]
    public async Task Isolation_denies_traversal_symlink_secrets_and_other_sessions()
    {
        using var dir = new TempDir();
        var session = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot, attachments, maxWritableBytes: 64);
        var definition = Examiner();

        await workspace.EnsureAsync(session, definition);
        await Assert.ThrowsAsync<AgentCoreException>(() =>
            workspace.ReadAsync(session, definition, "/workspace/working/../../secret.txt").AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() =>
            workspace.WriteAsync(session, $"/workspace/working/{other:N}/x.txt", "no"u8.ToArray()).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() =>
            workspace.WriteAsync(session, "/workspace/working/.env", "SECRET=1"u8.ToArray()).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() =>
            workspace.WriteAsync(session, "/workspace/state/CancellationTokenSource.bin", "x"u8.ToArray()).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() =>
            workspace.WriteAsync(session, "/agent/definition.json", "{}"u8.ToArray()).AsTask());

        await workspace.WriteAsync(session, "/workspace/working/a.txt", new byte[40]);
        var quota = await Assert.ThrowsAsync<AgentCoreException>(() =>
            workspace.WriteAsync(session, "/workspace/working/b.txt", new byte[40]).AsTask());
        Assert.Equal("WorkspaceQuotaExceeded", quota.Code);

        var outside = Path.Combine(dir.Root, "outside.txt");
        await File.WriteAllTextAsync(outside, "repo");
        var link = Path.Combine(dir.WorkspaceRoot, session.ToString("N"), "workspace", "working", "escape.txt");
        File.CreateSymbolicLink(link, outside);
        var escaped = await Assert.ThrowsAsync<AgentCoreException>(() =>
            workspace.ReadAsync(session, definition, "/workspace/working/escape.txt").AsTask());
        Assert.Equal("Forbidden", escaped.Code);

        await workspace.DeleteSessionAsync(session);
        var late = await Assert.ThrowsAsync<AgentCoreException>(() =>
            workspace.WriteAsync(session, "/workspace/working/late.txt", "x"u8.ToArray()).AsTask());
        Assert.Equal("NotFound", late.Code);
    }

    [Fact]
    public async Task Template_copies_working_files_without_secrets_or_knowledge()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.TemplateRoot, "notes", "knowledge"));
        Directory.CreateDirectory(Path.Combine(dir.TemplateRoot, "notes", "harness"));
        await File.WriteAllTextAsync(Path.Combine(dir.TemplateRoot, "notes", "welcome.txt"), "hello");
        await File.WriteAllTextAsync(Path.Combine(dir.TemplateRoot, "notes", ".env"), "SECRET=1");
        await File.WriteAllTextAsync(Path.Combine(dir.TemplateRoot, "notes", "knowledge", "body.md"), "corpus");
        await File.WriteAllTextAsync(Path.Combine(dir.TemplateRoot, "notes", "harness", "pack.txt"), "pack");

        var session = Guid.CreateVersion7();
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot);
        var definition = Examiner() with
        {
            Environment = new RoleEnvironment(Workspace: new WorkspaceTemplatePolicy("notes"))
        };
        await workspace.EnsureAsync(session, definition);
        var welcome = await workspace.ReadAsync(session, definition, "/workspace/working/welcome.txt");
        Assert.Equal("hello", Encoding.UTF8.GetString(welcome.Bytes));
        await Assert.ThrowsAsync<AgentCoreException>(() =>
            workspace.ReadAsync(session, definition, "/workspace/working/.env").AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() =>
            workspace.ReadAsync(session, definition, "/workspace/working/knowledge/body.md").AsTask());

        var agent = await workspace.ReadAsync(session, definition, "/agent/definition.json");
        Assert.Contains("examiner", Encoding.UTF8.GetString(agent.Bytes), StringComparison.Ordinal);
        await Assert.ThrowsAsync<AgentCoreException>(() =>
            workspace.WriteAsync(session, "/attachments/readme.txt", "no"u8.ToArray()).AsTask());
    }

    [Fact]
    public async Task Concurrent_writes_respect_quota()
    {
        using var dir = new TempDir();
        var session = Guid.CreateVersion7();
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot, maxWritableBytes: 50);
        var definition = Examiner();
        await workspace.EnsureAsync(session, definition);
        var first = workspace.WriteAsync(session, "/workspace/working/one.txt", new byte[40]);
        var second = workspace.WriteAsync(session, "/workspace/working/two.txt", new byte[40]);
        var results = await Task.WhenAll(Capture(first), Capture(second));
        Assert.Contains(results, result => result is null);
        Assert.Contains(results, result => result is { Code: "WorkspaceQuotaExceeded" });
    }

    private static async Task<AgentCoreException?> Capture(ValueTask task)
    {
        try
        {
            await task.ConfigureAwait(false);
            return null;
        }
        catch (AgentCoreException ex)
        {
            return ex;
        }
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
            Root = Path.Combine(Path.GetTempPath(), "agent-core-ws-iso", Guid.NewGuid().ToString("N"));
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
