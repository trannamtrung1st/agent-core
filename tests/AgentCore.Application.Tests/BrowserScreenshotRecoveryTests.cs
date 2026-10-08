using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Application.Tests;

public sealed class BrowserScreenshotRecoveryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Screenshot_checkpoint_recovers_owned_artifact_only_for_a_vision_model(bool vision)
    {
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var session = Guid.NewGuid();
        byte[] bytes = [0x89, 0x50, 0x4e, 0x47, 1, 2, 3];
        var artifact = await artifacts.CreateAsync(session, "screenshot.png", "image/png", bytes, null, null);
        var messages = Restored(artifact.ArtifactId);
        Assert.Null(Assert.Single(messages).Parts);
        var tools = new SessionToolExecutor(artifacts: artifacts);
        var recovered = Assert.Single(await tools.RehydrateCapturesAsync(session, messages, vision, CancellationToken.None));
        Assert.Contains(artifact.ArtifactId.ToString(), recovered.Text);
        if (vision) Assert.Equal(bytes, Assert.Single(recovered.Parts!.OfType<ModelImageContent>()).Bytes.ToArray());
        else Assert.Null(recovered.Parts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Foreign_or_deleted_screenshot_returns_unavailable_without_image_bytes(bool foreign)
    {
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var session = Guid.NewGuid();
        var artifact = await artifacts.CreateAsync(session, "screenshot.png", "image/png", new byte[] { 1, 2, 3 }, null, null);
        if (!foreign) await artifacts.DeleteSessionAsync(session);
        var tools = new SessionToolExecutor(artifacts: artifacts);
        var recovered = Assert.Single(await tools.RehydrateCapturesAsync(foreign ? Guid.NewGuid() : session,
            Restored(artifact.ArtifactId), true, CancellationToken.None));
        Assert.Equal(SessionCaptureRehydration.Unavailable, recovered.Text);
        Assert.Null(recovered.Parts);
    }

    private static List<ModelMessage> Restored(Guid artifactId)
    {
        var payload = AgentRunToolCallCheckpoint.Write([new(ModelRole.Tool,
            JsonSerializer.Serialize(new { artifactId }), ToolCallId: "shot", Name: ToolCatalog.BrowserScreenshot)]);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(new AgentRunCheckpoint(payload, 1, 0, 1000), out var restored));
        return restored!.ToList();
    }
}
