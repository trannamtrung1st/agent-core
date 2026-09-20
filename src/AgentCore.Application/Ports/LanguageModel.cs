namespace AgentCore.Application.Ports;

public enum ProviderErrorCode
{
    Authentication, RateLimited, Timeout, Cancelled, InvalidRequest,
    InvalidResponse, Unavailable, UnsupportedCapability, Unknown
}

public sealed record ProviderFailure(
    ProviderErrorCode Code,
    string SafeMessage,
    TimeSpan? RetryAfter = null);

public enum ModelRole { System, User, Assistant, Tool }

public enum ModelStopReason { Completed, LengthLimit, ContentFiltered, ToolCalls }

public abstract record ModelContentPart;

public sealed record ModelTextContent(string Text) : ModelContentPart;

public sealed record ModelImageContent(string ContentType, byte[] Bytes, string FileName) : ModelContentPart;

public sealed record ModelToolCall(string Id, string Name, string ArgumentsJson);

public sealed record ModelToolDefinition(string Name, string Description, string ParametersJson);

public sealed record ModelMessage(
    ModelRole Role,
    string Text,
    IReadOnlyList<ModelContentPart>? Parts = null,
    string? ToolCallId = null,
    string? Name = null,
    IReadOnlyList<ModelToolCall>? ToolCalls = null);

public sealed record ModelCapabilities(
    bool StreamingText,
    bool Cancellation,
    bool Vision = false,
    bool Tools = false,
    bool StructuredOutput = false);

/// <summary>
/// Provider-neutral request that the assistant response should follow the semantic envelope.
/// <see cref="SpeechWillBeUsed"/> is assigned by SessionRuntime at conversational cutover (P2B-5), not here.
/// </summary>
public sealed record ModelResponseContract(bool SpeechWillBeUsed);

public sealed record ModelRequest(
    Guid ResponseId,
    IReadOnlyList<ModelMessage> Messages,
    int MaxOutputTokens = 512,
    double? Temperature = null,
    IReadOnlyList<ModelToolDefinition>? Tools = null,
    string? ReasoningEffort = null,
    ModelResponseContract? ResponseContract = null);

public abstract record ModelGenerationEvent;

public sealed record ModelTextDelta(string Text) : ModelGenerationEvent;

public enum ModelSpeechMode
{
    Same,
    Custom,
    None
}

public sealed record ModelSpeechProjection(ModelSpeechMode Mode, string? Text);

public enum ModelResponseBlockKind
{
    Markdown,
    AttachmentReference,
    ArtifactReference,
    Unknown
}

public sealed record ModelResponseBlock(
    ModelResponseBlockKind Kind,
    string? Text = null,
    string? AttachmentId = null,
    string? ArtifactId = null);

public sealed record ModelSemanticResponse(
    string DisplayText,
    ModelSpeechProjection Speech,
    IReadOnlyList<ModelResponseBlock> Blocks);

/// <summary>
/// Incremental visible conversational text for a request that carries <see cref="ModelResponseContract"/>.
/// Non-envelope requests keep <see cref="ModelTextDelta"/>.
/// </summary>
public sealed record ModelDisplayDelta(string Text) : ModelGenerationEvent;

/// <summary>
/// Completed provider-neutral assistant envelope for a contract request.
/// </summary>
public sealed record ModelSemanticResponseReady(ModelSemanticResponse Response) : ModelGenerationEvent;

/// <summary>
/// Provider reasoning channel; must never become display, speech, history, or TTS input.
/// </summary>
public sealed record ModelReasoningDelta(string Text) : ModelGenerationEvent;

public sealed record ModelToolCallEvent(ModelToolCall Call) : ModelGenerationEvent;

public sealed record ModelCompleted(
    ModelStopReason Reason,
    int? InputTokens = null,
    int? OutputTokens = null) : ModelGenerationEvent;

public sealed record ModelFailed(ProviderFailure Failure) : ModelGenerationEvent;

public interface ILanguageModel
{
    ModelCapabilities Capabilities { get; }

    IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
        ModelRequest request,
        CancellationToken cancellationToken = default);
}
