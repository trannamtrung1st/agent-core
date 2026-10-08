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
    BrowserControlState? State = null,
    bool? Visible = null)
{
    public BrowserElement(string Ref, string Role, string Name)
        : this(Ref, Role, Name, [])
    {
    }
}

public sealed record BrowserSnapshot(
    string Url,
    string Title,
    string Content,
    bool ContentTruncated,
    IReadOnlyList<BrowserElement> Targets,
    BrowserInterventionKind Intervention = BrowserInterventionKind.None,
    bool? Settled = null,
    string? SnapshotId = null,
    string? TabRef = null,
    IReadOnlyList<BrowserTargetBox>? Boxes = null,
    string? Scope = null,
    IReadOnlyList<BrowserFrameInfo>? Frames = null, bool HasPasswordField = false);

public sealed record BrowserPageInfo(string PageId, string Url, bool Active);

public sealed record BrowserFrameInfo(string Ref, string Url, string Name);

public sealed record BrowserTargetBox(string Ref, float X, float Y, float Width, float Height);

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

public sealed record BrowserResult(
    string? ErrorCode,
    BrowserSnapshot? Observation = null,
    IReadOnlyList<string>? AllowedActions = null,
    IReadOnlyList<BrowserDownload>? Downloads = null,
    string? DataJson = null, byte[]? Bytes = null, string? ContentType = null,
    string? FileName = null, string? Status = null,
    int RedactionCount = 0, int Width = 0, int Height = 0, IReadOnlyList<BrowserPageInfo>? Pages = null);

/// <summary>Resolves only after validating a live existing-password field and exact current origin, under the browser gate.</summary>
public interface IBrowserPasswordSink
{
    ValueTask<BrowserResult> FillCredentialAsync(Guid sessionId, string reference,
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
    bool IsAvailable { get; }
    BrowserHostPolicy HostPolicy { get; }
    ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken cancellationToken = default);
}

public interface IBrowserProfileReset
{
    ValueTask ResetPersistentProfileAsync(Guid agentInstanceId, CancellationToken cancellationToken = default);
}

public enum BrowserOperation
{
    Navigate, Snapshot, Find, Click, Hover, Drag, Drop, Type, FillForm, SelectOption,
    PressKey, Upload, FillCredential, WaitFor, Tabs, Dialog, Resize, Close, Screenshot, ConsoleMessages,
    NetworkRequests, NetworkRequest, Route, Routes, Unroute, NetworkState, Cookies,
    LocalStorage, SessionStorage, Verify, GenerateLocator, Mouse, Highlight, EmulateMedia,
    GetConfig, SetGeolocation, Scroll
}

public sealed record BrowserTargetQuery(
    string? Role = null, string? Name = null, string? Text = null, string? Label = null,
    string? Placeholder = null, string? AltText = null, string? Title = null,
    string? TestId = null, bool Exact = true, string? ScopeRef = null, string? FrameRef = null,
    int Limit = 10, int Offset = 0, bool? Visible = null);

public sealed record BrowserFormField(string Ref, string? Value = null, bool? Checked = null);
public sealed record BrowserUpload(string FileName, string MediaType, ReadOnlyMemory<byte> Content);

/// <summary>Bounded neutral values. Authorization, secret resolution and Artifact bytes remain Core-owned.</summary>
public sealed record BrowserOptionsData
{
    public string? Operation { get; init; }
    public string? Url { get; init; }
    public string? Ref { get; init; }
    public string? TargetRef { get; init; }
    public string? TabRef { get; init; }
    public string? FrameRef { get; init; }
    public BrowserTargetQuery? Query { get; init; }
    public string? Text { get; init; }
    public string? Value { get; init; }
    public string? Key { get; init; }
    public string? Name { get; init; }
    public string? Condition { get; init; }
    public string? State { get; init; }
    public string? Button { get; init; }
    public int? ClickCount { get; init; }
    public IReadOnlyList<string>? Modifiers { get; init; }
    public bool? Submit { get; init; }
    public bool? Slowly { get; init; }
    public IReadOnlyList<BrowserFormField>? Fields { get; init; }
    public IReadOnlyList<string>? Values { get; init; }
    public IReadOnlyList<BrowserUpload>? Uploads { get; init; }
    public int? Depth { get; init; }
    public bool? Boxes { get; init; }
    public int? TimeoutMs { get; init; }
    public string? PromptText { get; init; }
    public string? Format { get; init; }
    public bool? FullPage { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? Limit { get; init; }
    public string? Level { get; init; }
    public string? RequestRef { get; init; }
    public string? RuleRef { get; init; }
    public string? Action { get; init; }
    public string? Body { get; init; }
    public int? Status { get; init; }
    public bool? Online { get; init; }
    public string? MimeType { get; init; }
    public float? X { get; init; }
    public float? Y { get; init; }
    public float? TargetX { get; init; }
    public float? TargetY { get; init; }
    public float? DeltaX { get; init; }
    public float? DeltaY { get; init; }
    public bool MediaSpecified { get; init; }
    public bool ColorSchemeSpecified { get; init; }
    public bool ReducedMotionSpecified { get; init; }
    public bool ForcedColorsSpecified { get; init; }
    public bool ContrastSpecified { get; init; }
    public string? Media { get; init; }
    public string? ColorScheme { get; init; }
    public string? ReducedMotion { get; init; }
    public string? ForcedColors { get; init; }
    public string? Contrast { get; init; }
    public string? Origin { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? Accuracy { get; init; }
}

public sealed record BrowserRequest(Guid SessionId, BrowserOperation Operation, BrowserOptionsData Options);

public enum BrowserFeature { Navigate, Snapshot, Find, Click, Hover, Drag, Drop, Type, FillForm, SelectOption, PressKey, Upload, FillCredential, Wait, Tabs, Dialog, Resize, Close, Screenshot, Console, NetworkInspect, NetworkControl, Storage, Testing, VisionMouse, Scroll, Highlight, Media, Configuration, Geolocation }

public sealed record BrowserProviderDescriptor(string ProviderId, string DisplayName, IReadOnlySet<BrowserFeature> SupportedFeatures)
{
    public string Engine { get; init; } = "unknown";
    public bool Supports(BrowserFeature feature) => SupportedFeatures.Contains(feature);
}
