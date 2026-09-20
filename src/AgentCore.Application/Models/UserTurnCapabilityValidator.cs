using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Models;

public interface IUserTurnCapabilityValidator
{
    ValueTask ValidateAsync(
        Guid sessionId,
        SessionModelSelection? selection,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default);

    void ValidateProcessedImages(
        SessionModelSelection? selection,
        IReadOnlyList<AttachmentProcessResult> results);
}

public sealed class UserTurnCapabilityValidator(IAttachmentStore attachments, IModelCatalog catalog)
    : IUserTurnCapabilityValidator
{
    public async ValueTask ValidateAsync(
        Guid sessionId,
        SessionModelSelection? selection,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default)
    {
        if (attachmentIds.Count == 0)
        {
            return;
        }

        await attachments.ValidateBindableAsync(sessionId, attachmentIds, cancellationToken).ConfigureAwait(false);
        foreach (var id in attachmentIds)
        {
            var record = await attachments.GetAsync(sessionId, id, cancellationToken).ConfigureAwait(false);
            if (record is not null && AttachmentMedia.IsImage(record.ContentType))
            {
                EnsureVisionSupported(selection);
                return;
            }
        }
    }

    public void ValidateProcessedImages(
        SessionModelSelection? selection,
        IReadOnlyList<AttachmentProcessResult> results)
    {
        if (!results.Any(item =>
                item.Kind == AttachmentProcessKind.Image
                && item.StrippedImage is { Length: > 0 }))
        {
            return;
        }

        EnsureVisionSupported(selection);
    }

    private void EnsureVisionSupported(SessionModelSelection? selection)
    {
        if (ResolveDescriptor(selection).Vision)
        {
            return;
        }

        throw AgentCoreErrors.ModelCapabilityUnsupported();
    }

    private ModelDescriptor ResolveDescriptor(SessionModelSelection? selection)
    {
        var key = selection?.CatalogKey;
        if (!string.IsNullOrWhiteSpace(key))
        {
            var match = catalog.Get(key);
            if (match is not null)
            {
                return match;
            }
        }

        return catalog.Default;
    }
}
