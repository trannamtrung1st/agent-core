using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Application.Tests;

public sealed class SessionCaptureRecoveryTests
{
    [Fact]
    public async Task Recovery_restores_image_bytes_only_from_the_actual_owning_session()
    {
        var sessionId = Guid.NewGuid();
        var store = new InMemoryArtifactStore(TimeProvider.System);
        var bytes = new byte[] { 137, 80, 78, 71, 1, 2, 3 };
        var artifact = await store.CreateAsync(sessionId, "capture.png", "image/png", bytes, null, null);
        var source = Capture(artifact.ArtifactId);
        var restored = await SessionCaptureRehydration.ApplyAsync(sessionId, [source], store, default);
        var image = Assert.Single(Assert.Single(restored).Parts!.OfType<ModelImageContent>());
        Assert.Equal(bytes, image.Bytes.ToArray());
        Assert.Equal(source.Text, restored[0].Text);
        var foreign = await SessionCaptureRehydration.ApplyAsync(Guid.NewGuid(), [source], store, default);
        Assert.Equal(SessionCaptureRehydration.Unavailable, foreign[0].Text);
        Assert.Null(foreign[0].Parts);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("oversized")]
    [InlineData("wrong-session")]
    public async Task Recovery_replaces_unavailable_or_corrupt_capture_with_explicit_evidence_failure(string failure)
    {
        var sessionId = Guid.NewGuid(); var artifactId = Guid.NewGuid(); var bytes = new byte[] { 1, 2, 3 };
        var artifact = new ArtifactRecord(artifactId, failure == "wrong-session" ? Guid.NewGuid() : sessionId,
            "capture.png", "image/png", failure == "size" ? 4 : failure == "oversized" ? BrowserToolLimits.MaxDownloadBytes + 1 : bytes.Length,
            failure == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)), null, null, DateTimeOffset.UtcNow);
        var restored = await SessionCaptureRehydration.ApplyAsync(sessionId, [Capture(artifactId)],
            new Captures(artifact, bytes, failure == "missing"), default);
        Assert.Equal(SessionCaptureRehydration.Unavailable, restored[0].Text);
        Assert.Null(restored[0].Parts);
    }

    private static ModelMessage Capture(Guid id) => new(ModelRole.Tool, JsonSerializer.Serialize(new { artifactId = id }),
        ToolCallId: "capture-1", Name: ToolCatalog.BrowserScreenshot);

    private sealed class Captures(ArtifactRecord artifact, byte[] bytes, bool missing) : IArtifactStore
    {
        public ValueTask<ArtifactPage> ListPageAsync(Guid sessionId, Guid? before, int limit, CancellationToken ct = default, Guid? agentRunId = null) => new(new ArtifactPage([artifact], null, false));
        public bool Exists(Guid sessionId, Guid artifactId) => artifact.SessionId == sessionId && artifact.ArtifactId == artifactId;
        public ValueTask<ArtifactRecord?> GetAsync(Guid sessionId, Guid artifactId, CancellationToken ct = default) =>
            ValueTask.FromResult<ArtifactRecord?>(Exists(sessionId, artifactId) ? artifact : null);
        public ValueTask<Stream> OpenContentAsync(Guid sessionId, Guid artifactId, CancellationToken ct = default) =>
            missing ? ValueTask.FromException<Stream>(new IOException("Missing capture.")) : ValueTask.FromResult<Stream>(new MemoryStream(bytes));
        public ValueTask<IReadOnlyList<ArtifactRecord>> ListAsync(Guid sessionId, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<ArtifactRecord>>([artifact]);
        public ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<ArtifactRecord> CreateAsync(Guid sessionId, string displayName, string contentType, ReadOnlyMemory<byte> content,
            Guid? sourceAttachmentId, string? workspaceLogicalPath, CancellationToken ct = default, Guid? agentRunId = null) => throw new NotSupportedException();
    }
}
