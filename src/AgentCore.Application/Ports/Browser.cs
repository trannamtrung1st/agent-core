namespace AgentCore.Application.Ports;

public enum BrowserInteractionMode
{
    ReadNavigation,
    InteractiveDemo
}

public enum BrowserPolicyMode
{
    Restricted,
    OpenWeb
}

public enum BrowserProfileMode
{
    EphemeralSession,
    PersistentAgent
}

public sealed record BrowserHostPolicy(
    bool Enabled,
    bool Headless,
    BrowserInteractionMode InteractionMode,
    IReadOnlyList<string> NavigationOrigins,
    IReadOnlyList<string>? InteractionOrigins = null,
    IReadOnlyList<string>? ResourceOrigins = null,
    BrowserPolicyMode PolicyMode = BrowserPolicyMode.Restricted,
    BrowserProfileMode ProfileMode = BrowserProfileMode.EphemeralSession)
{
    public IReadOnlyList<string> TargetOrigins => NavigationOrigins;

    public IReadOnlyList<string> EffectiveInteractionOrigins => InteractionOrigins ?? NavigationOrigins;

    public IReadOnlyList<string> EffectiveResourceOrigins => ResourceOrigins ?? [];
}

public sealed record BrowserElement(string Ref, string Role, string Name);

public sealed record BrowserObservation(
    string Url,
    string Title,
    string VisibleText,
    bool TextTruncated,
    IReadOnlyList<BrowserElement> Elements,
    BrowserInterventionKind Intervention = BrowserInterventionKind.None);

public enum BrowserInterventionKind
{
    None,
    AuthenticationRequired,
    AccountRegistrationRequired,
    HumanVerificationRequired
}

public sealed record BrowserOperationResult(string? ErrorCode, BrowserObservation? Observation);

public sealed record BrowserNavigateRequest(Guid SessionId, Uri Url);

public sealed record BrowserActRequest(Guid SessionId, string Operation, string Ref, string? Value);

public interface IBrowserSessionLease
{
    ValueTask ReleaseAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public interface IBrowserProfileBinding
{
    void BindSession(Guid sessionId, Guid? agentInstanceId);
}

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

    ValueTask<BrowserCloseResult> CloseAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        new(new BrowserCloseResult("provider_unavailable"));
}

public sealed record BrowserCloseResult(string Status);
