using SkiaSharp;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Attachments;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Application.Tests;

// Actual image/charset/strict UTF-8/multipage PDF reads are qualified by the
// both-store TenantSessionAccessTests.AttachmentReads model-loop matrix; current
// exact Office refusal and knowledge JSON/citation budgets are in KnowledgeReadBudgets.
public sealed class SessionToolExecutorTests
{
    [Fact]
    public async Task Host_paths_and_session_mutation_arguments_are_denied()
    {
        var workspace = new RecordingWorkspace();
        var executor = new SessionToolExecutor();
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
        Assert.ThrowsAny<JsonException>(() => TenantWorkspaceReadContract.Parse(ToolCatalog.KnowledgeRetrieve, "{"));
        Assert.Contains("forbidden", malformed, StringComparison.OrdinalIgnoreCase);

        var withSandbox = new SessionToolExecutor();
        foreach (var name in new[] { "process", "shell", "bash", "cmd", "powershell", "exec" })
        {
            var denied = await ExecuteTextAsync(
                withSandbox,
                Support(),
                Guid.NewGuid(),
                new ModelToolCall("c3", name, """{"cmd":"ls"}"""));
            Assert.Contains("forbidden", denied, StringComparison.OrdinalIgnoreCase);
        }

    }

    [Fact]
    public async Task Artifact_parser_refuses_fabricated_source_and_unowned_executor_cannot_publish()
    {
        var fabricated = Guid.NewGuid();
        var arguments = $$"""{"displayName":"note.md","content":"hello","sourceAttachmentId":"{{fabricated:D}}"}""";
        Assert.Throws<ArgumentException>(() => TenantArtifactToolContract.Parse(arguments));
        var result = await ExecuteTextAsync(new SessionToolExecutor(), Artifacts(), Guid.NewGuid(),
            new ModelToolCall("c1", ToolCatalog.ArtifactsCreate, arguments));
        Assert.Contains("forbidden", result, StringComparison.Ordinal);
        Assert.DoesNotContain(fabricated.ToString("D"), result, StringComparison.Ordinal);
        Assert.DoesNotContain("artifactId", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retired_sandbox_run_never_regains_execution_even_when_historically_allowlisted()
    {
        var executor = new SessionToolExecutor();
        var forbidden = await ExecuteTextAsync(
            executor,
            Support(),
            Guid.NewGuid(),
            new ModelToolCall("c1", "sandbox.run", """{"verb":"echo","arguments":["hi"]}"""));
        Assert.Contains("forbidden", forbidden, StringComparison.OrdinalIgnoreCase);

        var allowed = await ExecuteTextAsync(
            executor,
            Sandboxed(),
            Guid.NewGuid(),
            new ModelToolCall("c2", "sandbox.run", """{"verb":"echo","arguments":["hi"]}"""));
        Assert.Contains("forbidden", allowed, StringComparison.OrdinalIgnoreCase);

        var missing = new SessionToolExecutor();
        var unavailable = await ExecuteTextAsync(
            missing,
            Sandboxed(),
            Guid.NewGuid(),
            new ModelToolCall("c3", "sandbox.run", """{"verb":"echo"}"""));
        Assert.Contains("forbidden", unavailable, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Image_metadata_is_bounded_to_remaining_output_bytes()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var executor = new SessionToolExecutor();
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
        using var image = new SKBitmap(2, 2);
        using var buffer = new MemoryStream();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        encoded.SaveTo(buffer);
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
        ExecutionPolicy = new DefinitionExecutionPolicy(
            ToolAllowlist: [ToolCatalog.KnowledgeRetrieve],
            KnowledgeSources:
            [
                new KnowledgeSourceRef("compliance-retention", "Simulated retention policy", "compliance-retention@demo")
            ])
    };

    private static AgentDefinition Support() => new(
        2,
        "customer-support",
        1,
        new AgentIdentity("Sam", "Support", "Help.", "Warm"),
        ["Help"],
        "Help.",
        new BehaviorPolicy("answerNewTurn", true, true),
        new ConversationPolicy("concise", true, "en", 256),
        new InitiativePolicy(true, 10000, 30000, 1, ["longSilence"]),
        new VoiceConfiguration(false, "default", 1.0),
        new ProviderPreferences(null, null),
        new Dictionary<string, string>(),
        new DefinitionExecutionPolicy(
            ToolAllowlist:
            [
                ToolCatalog.KnowledgeRetrieve,
                ToolCatalog.WorkspaceWrite
            ]));

    private static AgentDefinition Sandboxed() => Support() with
    {
        ExecutionPolicy = new DefinitionExecutionPolicy(ToolAllowlist: ["sandbox.run"])
    };

    private static AgentDefinition Artifacts() => Support() with
    {
        ExecutionPolicy = new DefinitionExecutionPolicy(ToolAllowlist: [ToolCatalog.ArtifactsCreate])
    };

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

        public ValueTask CopyAcrossScopesAsync(Guid sessionId, string source, string destination, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<WorkspacePatchResult> PatchTextAsync(
            Guid sessionId,
            AgentDefinition definition,
            string logicalPath,
            IReadOnlyList<WorkspaceTextEdit> edits,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask MoveAsync(
            Guid sessionId,
            string sourceLogicalPath,
            string destinationLogicalPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }
}
