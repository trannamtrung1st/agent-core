using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Attachments;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Persistence;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace AgentCore.Application.Tests;

public sealed class SessionToolExecutorTests
{
    [Fact]
    public async Task Attachments_read_returns_image_metadata_without_utf8_garbage()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var executor = new SessionToolExecutor(attachments: attachments, processor: processor);
        var sessionId = Guid.NewGuid();
        var uploaded = await attachments.UploadPendingAsync(
            sessionId,
            "photo.png",
            "image/png",
            new MemoryStream(PngBytes()),
            false);
        var typed = await executor.ExecuteAsync(
            Support(),
            sessionId,
            new ModelToolCall("c1", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{uploaded.AttachmentId:D}}"}"""),
            ToolLimits.MaxOutputBytes);
        Assert.Contains("\"kind\":\"image\"", typed.Text, StringComparison.Ordinal);
        Assert.Contains("contentProvided", typed.Text, StringComparison.Ordinal);
        Assert.Contains("image/png", typed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\uFFFD", typed.Text, StringComparison.Ordinal);
        var image = Assert.IsType<ModelImageContent>(Assert.Single(typed.Parts!));
        Assert.Equal("image/png", image.ContentType);
        Assert.NotEmpty(image.Bytes);
        Assert.DoesNotContain(Convert.ToBase64String(image.Bytes), typed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_paths_and_session_mutation_arguments_are_denied()
    {
        var workspace = new RecordingWorkspace();
        var executor = new SessionToolExecutor(workspace: workspace);
        var sessionId = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");
        var host = await ExecuteTextAsync(
            executor,
            Support(),
            sessionId,
            new ModelToolCall("c1", ToolCatalog.WorkspaceWrite, """{"path":"/etc/passwd","content":"x"}"""));
        Assert.Contains("forbidden", host, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(workspace.Writes);

        var windows = await ExecuteTextAsync(
            executor,
            Support(),
            sessionId,
            new ModelToolCall("c2", ToolCatalog.WorkspaceWrite, """{"path":"C:\\Windows\\system32","content":"x"}"""));
        Assert.Contains("forbidden", windows, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(workspace.Writes);

        var mutate = await ExecuteTextAsync(
            executor,
            Support(),
            sessionId,
            new ModelToolCall("c3", ToolCatalog.KnowledgeRetrieve, """{"identity":"support-order-policy","mutateSession":true}"""));
        Assert.Contains("forbidden", mutate, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Process_and_malformed_payloads_are_denied()
    {
        var executor = new SessionToolExecutor();
        var process = await ExecuteTextAsync(
            executor,
            Support(),
            Guid.NewGuid(),
            new ModelToolCall("c1", "process", """{"cmd":"ls"}"""));
        Assert.Contains("forbidden", process, StringComparison.OrdinalIgnoreCase);

        var malformed = await ExecuteTextAsync(
            executor,
            Support(),
            Guid.NewGuid(),
            new ModelToolCall("c2", ToolCatalog.KnowledgeRetrieve, "{"));
        Assert.Contains("invalid", malformed, StringComparison.OrdinalIgnoreCase);

        var sandbox = new RecordingSandbox();
        var withSandbox = new SessionToolExecutor(sandbox: sandbox);
        foreach (var name in new[] { "process", "shell", "bash", "cmd", "powershell", "exec" })
        {
            var denied = await ExecuteTextAsync(
                withSandbox,
                Support(),
                Guid.NewGuid(),
                new ModelToolCall("c3", name, """{"cmd":"ls"}"""));
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
        var result = await ExecuteTextAsync(
            executor,
            Artifacts(),
            sessionId,
            new ModelToolCall(
                "c1",
                ToolCatalog.ArtifactsCreate,
                $$"""{"displayName":"note.md","content":"hello","sourceAttachmentId":"{{fabricated:D}}"}"""));
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
        var forbidden = await ExecuteTextAsync(
            executor,
            Support(),
            Guid.NewGuid(),
            new ModelToolCall("c1", ToolCatalog.SandboxRun, """{"verb":"echo","arguments":["hi"]}"""));
        Assert.Contains("forbidden", forbidden, StringComparison.OrdinalIgnoreCase);
        Assert.Null(sandbox.Last);

        var allowed = await ExecuteTextAsync(
            executor,
            Sandboxed(),
            Guid.NewGuid(),
            new ModelToolCall("c2", ToolCatalog.SandboxRun, """{"verb":"echo","arguments":["hi"]}"""));
        Assert.Contains("\"ok\":true", allowed, StringComparison.Ordinal);
        Assert.NotNull(sandbox.Last);
        Assert.Equal("echo", sandbox.Last!.Verb);

        var missing = new SessionToolExecutor();
        var unavailable = await ExecuteTextAsync(
            missing,
            Sandboxed(),
            Guid.NewGuid(),
            new ModelToolCall("c3", ToolCatalog.SandboxRun, """{"verb":"echo"}"""));
        Assert.Contains("unavailable", unavailable, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Attachments_read_accepts_text_plain_with_charset_parameter()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var executor = new SessionToolExecutor(attachments: attachments);
        var sessionId = Guid.NewGuid();
        var uploaded = await attachments.UploadPendingAsync(
            sessionId,
            "note.txt",
            "text/plain; charset=utf-8",
            new MemoryStream("Retention policy excerpt."u8.ToArray()),
            false);
        var result = await ExecuteTextAsync(
            executor,
            Support(),
            sessionId,
            new ModelToolCall("c1", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{uploaded.AttachmentId:D}}"}"""));
        using var json = JsonDocument.Parse(result);
        Assert.Equal("Retention policy excerpt.", json.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Attachments_read_falls_back_to_base64_for_invalid_utf8()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var executor = new SessionToolExecutor(attachments: attachments);
        var sessionId = Guid.NewGuid();
        var bytes = new byte[] { 0xFF, 0xFE, 0xFD };
        var uploaded = await attachments.UploadPendingAsync(
            sessionId,
            "note.bin",
            "text/plain",
            new MemoryStream(bytes),
            false);
        var result = await ExecuteTextAsync(
            executor,
            Support(),
            sessionId,
            new ModelToolCall("c1", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{uploaded.AttachmentId:D}}"}"""));
        using var json = JsonDocument.Parse(result);
        Assert.Equal(Convert.ToBase64String(bytes), json.RootElement.GetProperty("content").GetString());
        Assert.DoesNotContain("\uFFFD", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Knowledge_retrieve_returns_valid_json_when_output_is_truncated()
    {
        var knowledge = new RoleKnowledgeService(new FileApprovedKnowledgeCatalog(FindAgents()), TimeProvider.System);
        var executor = new SessionToolExecutor(knowledge);
        var result = await ExecuteTextAsync(
            executor,
            Compliance(),
            Guid.NewGuid(),
            new ModelToolCall("c1", ToolCatalog.KnowledgeRetrieve, """{"identity":"compliance-retention"}"""),
            220);
        using var json = JsonDocument.Parse(result);
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("content").GetString()));
        Assert.Equal("compliance-retention@demo", json.RootElement.GetProperty("citation").GetString());
    }

    [Fact]
    public async Task Image_metadata_is_bounded_to_remaining_output_bytes()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var executor = new SessionToolExecutor(attachments: attachments, processor: processor);
        var sessionId = Guid.NewGuid();
        var uploaded = await attachments.UploadPendingAsync(
            sessionId,
            "photo.png",
            "image/png",
            new MemoryStream(PngBytes()),
            false);
        var typed = await executor.ExecuteAsync(
            Support(),
            sessionId,
            new ModelToolCall("c1", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{uploaded.AttachmentId:D}}"}"""),
            48);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(typed.Text) <= 48);
        Assert.Null(typed.Parts);
        using var json = JsonDocument.Parse(typed.Text);
        Assert.True(
            (json.RootElement.TryGetProperty("error", out var error) && error.GetString() == "output_limit")
            || json.RootElement.TryGetProperty("truncated", out _));
    }

    [Fact]
    public async Task Attachments_read_reextracts_pdf_text_on_later_turns()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var executor = new SessionToolExecutor(attachments: attachments, processor: processor);
        var sessionId = Guid.NewGuid();
        var uploaded = await attachments.UploadPendingAsync(
            sessionId,
            "doc.pdf",
            "application/pdf",
            new MemoryStream(PdfTwoPages()),
            false);
        var result = await ExecuteTextAsync(
            executor,
            Support(),
            sessionId,
            new ModelToolCall("c1", ToolCatalog.AttachmentsRead, $$"""{"attachmentId":"{{uploaded.AttachmentId:D}}"}"""));
        using var json = JsonDocument.Parse(result);
        Assert.Equal("pdf", json.RootElement.GetProperty("kind").GetString());
        Assert.Contains("Alpha page", json.RootElement.GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains("Beta page", json.RootElement.GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    private static async Task<string> ExecuteTextAsync(
        SessionToolExecutor executor,
        AgentDefinition definition,
        Guid sessionId,
        ModelToolCall call,
        int remainingOutputBytes = ToolLimits.MaxOutputBytes) =>
        (await executor.ExecuteAsync(definition, sessionId, call, remainingOutputBytes)).Text;

    private static byte[] PdfTwoPages()
    {
        var pdf = """
%PDF-1.1
1 0 obj<< /Type /Catalog /Pages 2 0 R >>endobj
2 0 obj<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>endobj
3 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources<< /Font<< /F1 5 0 R >> >> >>endobj
4 0 obj<< /Length 44 >>stream
BT /F1 12 Tf 10 100 Td (Alpha page) Tj ET
endstream
endobj
5 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>endobj
6 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 7 0 R /Resources<< /Font<< /F1 5 0 R >> >> >>endobj
7 0 obj<< /Length 43 >>stream
BT /F1 12 Tf 10 100 Td (Beta page) Tj ET
endstream
endobj
trailer<< /Root 1 0 R >>
%%EOF
""";
        return Encoding.ASCII.GetBytes(pdf.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static byte[] PngBytes()
    {
        using var image = new Image<Rgba32>(2, 2, new Rgba32(10, 20, 30));
        using var buffer = new MemoryStream();
        image.SaveAsPng(buffer);
        return buffer.ToArray();
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents directory was not found.");
    }

    private static AgentDefinition Compliance() => Support() with
    {
        Id = "compliance",
        Environment = new RoleEnvironment(
            ToolAllowlist: [ToolCatalog.KnowledgeRetrieve],
            KnowledgeSources:
            [
                new KnowledgeSourceRef("compliance-retention", "Simulated retention policy", "compliance-retention@demo")
            ])
    };

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

        public ValueTask<WorkspacePatchResult> PatchTextAsync(
            Guid sessionId,
            AgentDefinition definition,
            string logicalPath,
            string expectedSha256Hex,
            IReadOnlyList<WorkspaceTextEdit> edits,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
