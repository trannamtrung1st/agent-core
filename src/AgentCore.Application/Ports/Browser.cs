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

public sealed record BrowserControlState(string? Value = null, bool? Checked = null, string? SelectedText = null);

public sealed record BrowserElement(
    string Ref,
    string Role,
    string Name,
    IReadOnlyList<string> Actions,
    BrowserControlState? State = null)
{
    public BrowserElement(string Ref, string Role, string Name)
        : this(Ref, Role, Name, [])
    {
    }
}

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

public sealed record BrowserOperationResult(
    string? ErrorCode,
    BrowserObservation? Observation,
    IReadOnlyList<string>? AllowedActions = null);

public sealed record BrowserNavigateRequest(Guid SessionId, Uri Url);

public sealed record BrowserUpload(string FileName, string MediaType, ReadOnlyMemory<byte> Content);

public sealed record BrowserActRequest(
    Guid SessionId,
    string Operation,
    string Ref,
    string? Value,
    BrowserUpload? Upload = null);

public interface IBrowserSessionLease
{
    ValueTask ReleaseAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public interface IBrowserProfileBinding
{
    void BindSession(Guid sessionId, Guid? agentInstanceId);
}

public interface IBrowserContextUse
{
    ValueTask<IAsyncDisposable> EnterUnattendedAsync(
        Guid agentInstanceId,
        IReadOnlyList<string> origins,
        CancellationToken cancellationToken = default);

    void AdoptUnattendedFlow(Guid agentInstanceId);
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

    ValueTask ResetPersistentProfileAsync(Guid agentInstanceId, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

public sealed record BrowserCloseResult(string Status);
