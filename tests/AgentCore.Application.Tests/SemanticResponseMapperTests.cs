using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class SemanticResponseMapperTests
{
    [Fact]
    public void Assigns_deterministic_block_ids_and_undelivered_display()
    {
        var envelope = SemanticResponseMapper.ToEnvelope(
            new ModelSemanticResponse(
                "Shown",
                new ModelSpeechProjection(ModelSpeechMode.Same, null),
                [
                    new ModelResponseBlock(ModelResponseBlockKind.Markdown, "**Hi**"),
                    new ModelResponseBlock(ModelResponseBlockKind.AttachmentReference, AttachmentId: "notes.txt")
                ]),
            Guid.NewGuid(),
            new FixtureArtifactReferenceAuthorizer());
        Assert.Equal(["b1", "b2"], envelope.Blocks.Select(block => block.BlockId));
        Assert.All(envelope.Blocks, block => Assert.False(block.DisplayDelivered));
    }

    [Fact]
    public void Unauthorized_artifact_and_path_attachment_fall_back()
    {
        var envelope = SemanticResponseMapper.ToEnvelope(
            new ModelSemanticResponse(
                "See",
                new ModelSpeechProjection(ModelSpeechMode.None, null),
                [
                    new ModelResponseBlock(ModelResponseBlockKind.ArtifactReference, ArtifactId: "secret-id"),
                    new ModelResponseBlock(ModelResponseBlockKind.AttachmentReference, AttachmentId: "../x")
                ]),
            Guid.NewGuid(),
            new FixtureArtifactReferenceAuthorizer());
        Assert.Equal(ResponseSpeechMode.None, envelope.SpeechMode);
        Assert.All(envelope.Blocks, block => Assert.Equal(ResponseBlockKind.Unknown, block.Kind));
        Assert.Equal(ResponseEnvelopeParser.UnauthorizedArtifactFallback, envelope.Blocks[0].FallbackText);
        Assert.Equal(ResponseEnvelopeParser.UnsupportedFallback, envelope.Blocks[1].FallbackText);
        Assert.Null(envelope.Blocks[0].ArtifactId);
    }

    [Fact]
    public void Authorizes_fixture_artifact()
    {
        var envelope = SemanticResponseMapper.ToEnvelope(
            new ModelSemanticResponse(
                "See",
                new ModelSpeechProjection(ModelSpeechMode.Custom, "Spoken"),
                [
                    new ModelResponseBlock(
                        ModelResponseBlockKind.ArtifactReference,
                        ArtifactId: FixtureArtifactReferenceAuthorizer.AuthorizedId)
                ]),
            Guid.NewGuid(),
            new FixtureArtifactReferenceAuthorizer());
        Assert.Equal(ResponseSpeechMode.Custom, envelope.SpeechMode);
        Assert.Equal("Spoken", envelope.SpeechText);
        Assert.Equal(ResponseBlockKind.ArtifactReference, envelope.Blocks[0].Kind);
        Assert.Equal(FixtureArtifactReferenceAuthorizer.AuthorizedId, envelope.Blocks[0].ArtifactId);
        Assert.False(envelope.Blocks[0].DisplayDelivered);
    }
}
