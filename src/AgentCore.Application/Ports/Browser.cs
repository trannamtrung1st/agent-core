namespace AgentCore.Application.Ports;

public enum BrowserInteractionMode
{
    ReadNavigation,
    InteractiveDemo
}

public sealed record BrowserHostPolicy(
    bool Enabled,
    bool Headless,
    BrowserInteractionMode InteractionMode,
    IReadOnlyList<string> TargetOrigins);

public sealed record BrowserElement(string Ref, string Role, string Name);

public sealed record BrowserObservation(
    string Url,
    string Title,
    string VisibleText,
    bool TextTruncated,
    IReadOnlyList<BrowserElement> Elements);

public sealed record BrowserOperationResult(string? ErrorCode, BrowserObservation? Observation);

public sealed record BrowserNavigateRequest(Guid SessionId, Uri Url);

public sealed record BrowserActRequest(Guid SessionId, string Operation, string Ref, string? Value);

public interface IBrowserSession
{
    bool IsAvailable { get; }

    BrowserHostPolicy HostPolicy { get; }

    ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default);

    ValueTask<BrowserOperationResult> NavigateAsync(
        BrowserNavigateRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<BrowserOperationResult> ObserveAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<BrowserOperationResult> ActAsync(
        BrowserActRequest request,
        CancellationToken cancellationToken = default);
}
