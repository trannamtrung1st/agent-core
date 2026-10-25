using SkiaSharp;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Attachments;
using AgentCore.Infrastructure.Persistence;

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
                "dca30000-0000-4000-8000-000000000001",
                "dca20000-0000-4000-8000-000000000001",
                "dca30000-0000-4000-8000-000000000001",
                ModelSelectionSource.TenantDefault,
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
                    "dca30000-0000-4000-8000-000000000001",
                    "dca20000-0000-4000-8000-000000000001",
                    "dca30000-0000-4000-8000-000000000001",
                    ModelSelectionSource.TenantDefault,
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
                    "dca20000-0000-4000-8000-000000000001",
                    "deepseek/deepseek-v4.1-flash",
                    ModelSelectionSource.TenantDefault,
                    "medium"),
                results));

        validator.ValidateProcessedImages(
            new SessionModelSelection(
                "gpt-4o-mini-2024-07-18",
                "dca20000-0000-4000-8000-000000000001",
                "openai/gpt-4o-mini-2024-07-18",
                ModelSelectionSource.User,
                null),
            results);
    }

    private static byte[] PngBytes()
    {
        using var image = new SKBitmap(2, 2);
        using var buffer = new MemoryStream();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
        encoded.SaveTo(buffer);
        return buffer.ToArray();
    }
}
