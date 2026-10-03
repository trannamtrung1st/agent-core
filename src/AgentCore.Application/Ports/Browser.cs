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
    BrowserInterventionKind Intervention = BrowserInterventionKind.None,
    bool? Settled = null);

/// <summary>
/// Provider-neutral observe wait. <paramref name="WaitFor"/> is <c>stable</c>, <c>navigation</c>, or <c>role</c>.
/// <paramref name="TimeoutMs"/> is optional and already bounded by Core.
/// </summary>
public sealed record BrowserObserveOptions(string WaitFor, int? TimeoutMs, string? Role = null, string? Name = null);

public enum BrowserInterventionKind
{
    None,
    AuthenticationRequired,
    AccountRegistrationRequired,
    HumanVerificationRequired
}

/// <summary>
/// One browser download from the current operation. Bytes are present only for an accepted file
/// and stay out of tool JSON, checkpoints, and logs.
/// </summary>
public sealed record BrowserDownload(
    string? ErrorCode,
    string? FileName,
    string? ContentType,
    byte[]? Bytes);

public sealed record BrowserOperationResult(
    string? ErrorCode,
    BrowserObservation? Observation,
    IReadOnlyList<string>? AllowedActions = null,
    IReadOnlyList<BrowserDownload>? Downloads = null);

public sealed record BrowserNavigateRequest(Guid SessionId, Uri? Url, string Operation = "goto");

public sealed record BrowserUpload(string FileName, string MediaType, ReadOnlyMemory<byte> Content);

public sealed record BrowserActRequest(
    Guid SessionId,
    string Operation,
    string Ref,
    string? Value,
    BrowserUpload? Upload = null,
    string? Direction = null,
    int Delta = 0,
    string? TargetRef = null);

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

    ValueTask<BrowserOperationResult> ObserveAsync(
        Guid sessionId,
        BrowserObserveOptions options,
        CancellationToken cancellationToken = default) =>
        ObserveAsync(sessionId, cancellationToken);

    ValueTask<BrowserOperationResult> ActAsync(
        BrowserActRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<BrowserCloseResult> CloseAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        new(new BrowserCloseResult("provider_unavailable"));

    ValueTask ResetPersistentProfileAsync(Guid agentInstanceId, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    ValueTask<BrowserPagesResult> PagesAsync(
        BrowserPagesRequest request,
        CancellationToken cancellationToken = default) =>
        new(new BrowserPagesResult("provider_unavailable", []));

    ValueTask<BrowserCaptureResult> CaptureViewportAsync(
        BrowserCaptureRequest request,
        CancellationToken cancellationToken = default) =>
        new(new BrowserCaptureResult("provider_unavailable", null, 0));
}

public sealed record BrowserPagesRequest(Guid SessionId, string Operation, string? PageId = null);

public sealed record BrowserPageInfo(string PageId, string Url, bool Active);

public sealed record BrowserPagesResult(
    string? ErrorCode,
    IReadOnlyList<BrowserPageInfo> Pages,
    BrowserObservation? Observation = null);

public sealed record BrowserCaptureRequest(Guid SessionId);

public sealed record BrowserCaptureResult(
    string? ErrorCode,
    byte[]? Png,
    int RedactionCount,
    int Width = 0,
    int Height = 0);

public sealed record BrowserCloseResult(string Status);
