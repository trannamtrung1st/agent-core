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

public sealed record BrowserSnapshot(
    string Url,
    string Title,
    string VisibleText,
    bool TextTruncated,
    IReadOnlyList<BrowserElement> Elements,
    BrowserInterventionKind Intervention = BrowserInterventionKind.None,
    bool? Settled = null,
    string? SnapshotId = null,
    string? TabRef = null,
    string? Content = null,
    IReadOnlyList<BrowserTargetBox>? Boxes = null);

public sealed record BrowserTargetBox(string Ref, float X, float Y, float Width, float Height);

/// <summary>
/// Provider-neutral observe wait. <paramref name="WaitFor"/> is <c>stable</c>, <c>navigation</c>, or <c>role</c>.
/// <paramref name="TimeoutMs"/> is optional and already bounded by Core.
/// </summary>
public sealed record BrowserWaitOptions(string WaitFor, int? TimeoutMs, string? Role = null, string? Name = null);

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
    BrowserSnapshot? Observation,
    IReadOnlyList<string>? AllowedActions = null,
    IReadOnlyList<BrowserDownload>? Downloads = null);

public sealed record BrowserNavigateRequest(Guid SessionId, Uri? Url, string Operation = "goto");

public sealed record BrowserUpload(string FileName, string MediaType, ReadOnlyMemory<byte> Content);

public sealed record BrowserInteractionRequest(
    Guid SessionId,
    string Operation,
    string Ref,
    string? Value,
    BrowserUpload? Upload = null,
    string? Direction = null,
    int Delta = 0,
    string? TargetRef = null,
    IReadOnlyList<BrowserUpload>? Uploads = null);

/// <summary>Resolves only after validating a live existing-password field and exact current origin, under the browser gate.</summary>
public interface IBrowserPasswordSink
{
    ValueTask<BrowserOperationResult> FillCredentialAsync(Guid sessionId, string reference,
        Func<string, CancellationToken, ValueTask<string>> resolve, CancellationToken ct = default);
}

public interface IBrowserLease
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

public interface IBrowser
{
    BrowserProviderDescriptor Provider { get; }

    ValueTask<BrowserCommandResult> ExecuteAsync(BrowserCommand command, CancellationToken cancellationToken = default) => new(new BrowserCommandResult("unsupported_operation"));

    bool IsAvailable { get; }

    BrowserHostPolicy HostPolicy { get; }

    ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default);

    ValueTask<BrowserOperationResult> NavigateAsync(
        BrowserNavigateRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<BrowserOperationResult> SnapshotAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);

    ValueTask<BrowserOperationResult> SnapshotAsync(
        Guid sessionId,
        BrowserWaitOptions options,
        CancellationToken cancellationToken = default) =>
        SnapshotAsync(sessionId, cancellationToken);

    ValueTask<BrowserOperationResult> InteractAsync(
        BrowserInteractionRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<BrowserCloseResult> CloseAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        new(new BrowserCloseResult("provider_unavailable"));

    ValueTask ResetPersistentProfileAsync(Guid agentInstanceId, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    ValueTask<BrowserTabsResult> TabsAsync(
        BrowserTabsRequest request,
        CancellationToken cancellationToken = default) =>
        new(new BrowserTabsResult("provider_unavailable", []));

    ValueTask<BrowserScreenshotResult> CaptureViewportAsync(
        BrowserScreenshotRequest request,
        CancellationToken cancellationToken = default) =>
        new(new BrowserScreenshotResult("provider_unavailable", null, 0));
}

public sealed record BrowserTabsRequest(Guid SessionId, string Operation, string? PageId = null);

public sealed record BrowserPageInfo(string PageId, string Url, bool Active);

public sealed record BrowserTabsResult(
    string? ErrorCode,
    IReadOnlyList<BrowserPageInfo> Pages,
    BrowserSnapshot? Observation = null);

public sealed record BrowserScreenshotRequest(Guid SessionId, string Format = "png", bool FullPage = false, string? TargetRef = null);

public sealed record BrowserScreenshotResult(
    string? ErrorCode,
    byte[]? Png,
    int RedactionCount,
    int Width = 0,
    int Height = 0,
    string ContentType = "image/png");

public sealed record BrowserCloseResult(string Status);

public enum BrowserFeature { Navigate, Snapshot, Find, Click, Hover, Drag, Drop, Type, FillForm, SelectOption, PressKey, Upload, FillCredential, Wait, Tabs, Dialog, Resize, Close, Screenshot, Console, NetworkInspect, NetworkControl, Storage, StorageState, Testing, VisionMouse, Pdf, Trace, Highlight, Media, Video, Evaluate }

public sealed record BrowserProviderDescriptor(string ProviderId, string DisplayName, IReadOnlySet<BrowserFeature> SupportedFeatures)
{
    public bool Supports(BrowserFeature feature) => SupportedFeatures.Contains(feature);
}

public sealed record BrowserCommand(Guid SessionId, string Tool, System.Text.Json.JsonElement Arguments);
public sealed record BrowserCommandResult(string? ErrorCode, BrowserSnapshot? Snapshot = null, string? DataJson = null, byte[]? Bytes = null, string? ContentType = null, string? FileName = null, IReadOnlyList<BrowserDownload>? Downloads = null);
