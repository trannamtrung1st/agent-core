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

public enum ModelRole { System, User, Assistant }

public enum ModelStopReason { Completed, LengthLimit, ContentFiltered }

public sealed record ModelMessage(ModelRole Role, string Text);

public sealed record ModelCapabilities(bool StreamingText, bool Cancellation);

public sealed record ModelRequest(
    Guid ResponseId,
    IReadOnlyList<ModelMessage> Messages,
    int MaxOutputTokens = 512,
    double? Temperature = null);

public abstract record ModelGenerationEvent;

public sealed record ModelTextDelta(string Text) : ModelGenerationEvent;

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
