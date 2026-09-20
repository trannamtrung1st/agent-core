using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Models;

public static class SemanticResponseMapper
{
    public static ResponseEnvelope ToEnvelope(
        ModelSemanticResponse semantic,
        Guid sessionId,
        IArtifactReferenceAuthorizer artifacts,
        Func<string, bool>? attachmentAllowed = null)
    {
        ArgumentNullException.ThrowIfNull(semantic);
        ArgumentNullException.ThrowIfNull(artifacts);
        var blocks = new List<ResponseBlock>();
        var ordinal = 0;
        foreach (var block in semantic.Blocks ?? [])
        {
            ordinal++;
            var blockId = "b" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            blocks.Add(MapBlock(blockId, block, sessionId, artifacts, attachmentAllowed));
        }

        var speech = semantic.Speech ?? new ModelSpeechProjection(ModelSpeechMode.Same, null);
        var mode = speech.Mode switch
        {
            ModelSpeechMode.Custom => ResponseSpeechMode.Custom,
            ModelSpeechMode.None => ResponseSpeechMode.None,
            _ => ResponseSpeechMode.Same
        };
        var display = semantic.DisplayText ?? string.Empty;
        return ResponseEnvelope.Create(
            display,
            new ResponseSpeech(mode, mode == ResponseSpeechMode.Custom ? speech.Text : null),
            blocks);
    }

    private static ResponseBlock MapBlock(
        string blockId,
        ModelResponseBlock block,
        Guid sessionId,
        IArtifactReferenceAuthorizer artifacts,
        Func<string, bool>? attachmentAllowed)
    {
        switch (block.Kind)
        {
            case ModelResponseBlockKind.Markdown:
                var markdown = block.Text ?? string.Empty;
                return new ResponseBlock(blockId, ResponseBlockKind.Markdown, markdown, markdown, null, null, false);
            case ModelResponseBlockKind.AttachmentReference:
                var attachmentId = block.AttachmentId ?? block.Text;
                if (string.IsNullOrWhiteSpace(attachmentId) || LooksLikePath(attachmentId) || attachmentAllowed?.Invoke(attachmentId) == false)
                {
                    return Fallback(blockId, ResponseEnvelopeParser.UnsupportedFallback);
                }

                return new ResponseBlock(
                    blockId,
                    ResponseBlockKind.AttachmentReference,
                    attachmentId,
                    attachmentId,
                    attachmentId,
                    null,
                    false);
            case ModelResponseBlockKind.ArtifactReference:
                var artifactId = block.ArtifactId ?? block.Text;
                if (string.IsNullOrWhiteSpace(artifactId) || !artifacts.IsAuthorized(sessionId, artifactId))
                {
                    return Fallback(blockId, ResponseEnvelopeParser.UnauthorizedArtifactFallback);
                }

                return new ResponseBlock(
                    blockId,
                    ResponseBlockKind.ArtifactReference,
                    artifactId,
                    artifactId,
                    null,
                    artifactId,
                    false);
            default:
                return Fallback(blockId, ResponseEnvelopeParser.UnsupportedFallback);
        }
    }

    private static ResponseBlock Fallback(string blockId, string fallback) =>
        new(blockId, ResponseBlockKind.Unknown, string.Empty, fallback, null, null, false);

    private static bool LooksLikePath(string value) =>
        value.Contains("..", StringComparison.Ordinal)
        || value.Contains('/', StringComparison.Ordinal)
        || value.Contains('\\', StringComparison.Ordinal);
}
