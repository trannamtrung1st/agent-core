namespace AgentCore.Application.Ports;

public enum ProviderErrorCode
{
    Authentication, RateLimited, Timeout, Cancelled, InvalidRequest,
    Unavailable, UnsupportedCapability, Unknown
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
    bool Tools = false);

public sealed record ModelRequest(
    Guid ResponseId,
    IReadOnlyList<ModelMessage> Messages,
    int MaxOutputTokens = 512,
    double? Temperature = null,
    IReadOnlyList<ModelToolDefinition>? Tools = null);

public abstract record ModelGenerationEvent;

public sealed record ModelTextDelta(string Text) : ModelGenerationEvent;

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
