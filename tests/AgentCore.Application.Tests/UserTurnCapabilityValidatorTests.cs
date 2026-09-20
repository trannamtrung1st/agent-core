using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Attachments;
using AgentCore.Infrastructure.Persistence;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace AgentCore.Application.Tests;

public sealed class UserTurnCapabilityValidatorTests
{
    [Fact]
    public async Task Text_only_turn_does_not_require_vision()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var session = Guid.NewGuid();
        var catalog = TestModelCatalogs.Synthetic();
        var validator = new UserTurnCapabilityValidator(attachments, catalog);
        var uploaded = await attachments.UploadPendingAsync(
            session,
            "notes.txt",
            "text/plain",
            new MemoryStream("hello"u8.ToArray()),
            false);

        await validator.ValidateAsync(
            session,
            new SessionModelSelection(
                "scripted-alpha",
                "primary-llm",
                "scripted-alpha",
                ModelSelectionSource.SystemDefault,
                "medium"),
            [uploaded.AttachmentId]);
    }

    [Fact]
    public async Task Image_turn_on_non_vision_model_is_rejected()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var session = Guid.NewGuid();
        var catalog = TestModelCatalogs.Synthetic();
        var validator = new UserTurnCapabilityValidator(attachments, catalog);
        var uploaded = await attachments.UploadPendingAsync(
            session,
            "photo.png",
            "image/png",
            new MemoryStream(PngBytes()),
            false);

        var ex = await Assert.ThrowsAsync<AgentCoreException>(() =>
            validator.ValidateAsync(
                session,
                new SessionModelSelection(
                    "scripted-alpha",
                    "primary-llm",
                    "scripted-alpha",
                    ModelSelectionSource.SystemDefault,
                    "medium"),
                [uploaded.AttachmentId]).AsTask());
        Assert.Equal("ModelCapabilityUnsupported", ex.Code);
        Assert.Equal(409, ex.StatusCode);
        Assert.False(ex.Fatal);
    }

    [Fact]
    public void Processed_image_results_require_vision()
    {
        var catalog = TestModelCatalogs.Real();
        var validator = new UserTurnCapabilityValidator(new InMemoryAttachmentStore(TimeProvider.System), catalog);
        var results = new[]
        {
            new AttachmentProcessResult(
                Guid.NewGuid(),
                AttachmentLimits.ProcessorVersion,
                AttachmentProcessKind.Image,
                "photo.png",
                "image/png",
                "",
                null,
                [1, 2, 3],
                null)
        };

        Assert.Throws<AgentCoreException>(() =>
            validator.ValidateProcessedImages(
                new SessionModelSelection(
                    "deepseek-v41-flash",
                    "primary-llm",
                    "deepseek/deepseek-v4.1-flash",
                    ModelSelectionSource.SystemDefault,
                    "medium"),
                results));

        validator.ValidateProcessedImages(
            new SessionModelSelection(
                "gpt-4o-mini-2024-07-18",
                "primary-llm",
                "openai/gpt-4o-mini-2024-07-18",
                ModelSelectionSource.User,
                null),
            results);
    }

    private static byte[] PngBytes()
    {
        using var image = new Image<Rgba32>(2, 2);
        using var buffer = new MemoryStream();
        image.Save(buffer, new PngEncoder());
        return buffer.ToArray();
    }
}
