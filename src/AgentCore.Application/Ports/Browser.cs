using System.Text.Json.Serialization;

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
    public BrowserOperationalLimits Limits { get; init; } = BrowserOperationalLimits.Default;
    public IReadOnlyList<string> TargetOrigins => NavigationOrigins;

    public IReadOnlyList<string> EffectiveInteractionOrigins => InteractionOrigins ?? NavigationOrigins;

    public IReadOnlyList<string> EffectiveResourceOrigins => ResourceOrigins ?? [];
}

public sealed record BrowserControlState(string? Value = null, bool? Checked = null, string? SelectedText = null);

public sealed record BrowserElement(
    BrowserTarget Target,
    string Role,
    string Name,
    IReadOnlyList<string> Actions,
    BrowserControlState? State = null,
    bool? Visible = null)
{
    public BrowserElement(BrowserTarget Target, string Role, string Name)
        : this(Target, Role, Name, [])
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
    BrowserTarget? Scope = null,
    IReadOnlyList<BrowserFrameInfo>? Frames = null, bool HasPasswordField = false, string? FrameRef = null);

public sealed record BrowserPageInfo(string PageId, string Url, bool Active);

public sealed record BrowserFrameInfo(string Ref, string Url, string Name);

public sealed record BrowserTargetBox(BrowserTarget Target, float X, float Y, float Width, float Height);

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
    int RedactionCount = 0, int Width = 0, int Height = 0, IReadOnlyList<BrowserPageInfo>? Pages = null,
    bool EffectAttempted = false, bool EffectConfirmedBySdk = false, bool ApplicationOutcomeVerified = false);

/// <summary>Resolves only after validating a live existing-password field and exact current origin, under the browser gate.</summary>
public interface IBrowserPasswordSink
{
    ValueTask<BrowserResult> FillCredentialAsync(Guid sessionId, BrowserTarget target,
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
        IReadOnlyList<string>? origins,
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

/// <summary>Literal native semantic targeting. One bounded scope; no selectors, scripts or element refs.</summary>
public sealed record BrowserScope([property: JsonPropertyName("by")] string By, [property: JsonPropertyName("value")] string Value, [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null, [property: JsonPropertyName("exact")] bool Exact = true,
    [property: JsonPropertyName("visible"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Visible = null, [property: JsonPropertyName("hasText"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HasText = null);
public sealed record BrowserTarget([property: JsonPropertyName("by")] string By, [property: JsonPropertyName("value")] string Value, [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null, [property: JsonPropertyName("exact")] bool Exact = true,
    [property: JsonPropertyName("visible"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Visible = null, [property: JsonPropertyName("hasText"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HasText = null, [property: JsonPropertyName("within"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BrowserScope? Within = null, [property: JsonPropertyName("frameRef"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FrameRef = null);
public sealed record BrowserFormField([property: JsonPropertyName("target")] BrowserTarget Target, [property: JsonPropertyName("value"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Value = null, [property: JsonPropertyName("checked"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Checked = null);
public sealed record BrowserUpload(string FileName, string MediaType, ReadOnlyMemory<byte> Content);

/// <summary>Closed neutral command family; every operation carries only its own payload.</summary>
public abstract record BrowserCommand
{
    public abstract BrowserOperation Kind { get; }
    private protected BrowserCommand() { }
}

public sealed record BrowserNavigate(string? Url = null, string Operation = "goto") : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Navigate;
}
public sealed record BrowserObserve(BrowserTarget? Target = null, int Depth = 32, bool Boxes = false, string? FrameRef = null) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Snapshot;
}
public sealed record BrowserFind(BrowserTarget Target, int Limit = 10, int Offset = 0) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Find;
}
public sealed record BrowserClick(BrowserTarget Target, string Button = "left", int ClickCount = 1, IReadOnlyList<string>? Modifiers = null) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Click;
}
public sealed record BrowserHover(BrowserTarget Target) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Hover;
}
public sealed record BrowserDrag(BrowserTarget Target, BrowserTarget Destination) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Drag;
}
public sealed record BrowserDrop(BrowserTarget Target, string? Text = null, string? MimeType = null, string? ArtifactId = null) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Drop;
}
public sealed record BrowserTypeText(BrowserTarget Target, string Text, bool Submit = false, bool Slowly = false) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Type;
}
public sealed record BrowserFillForm(IReadOnlyList<BrowserFormField> Fields) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.FillForm;
}
public sealed record BrowserSelectOption(BrowserTarget Target, IReadOnlyList<string> Values) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.SelectOption;
}
public sealed record BrowserPressKey(string Key, BrowserTarget? Target = null) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.PressKey;
}
public sealed record BrowserUploadCommand(BrowserTarget Target, IReadOnlyList<string>? ArtifactIds = null, IReadOnlyList<BrowserUpload>? Uploads = null) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Upload;
}
public sealed record BrowserFillCredential(BrowserTarget Target, string CredentialRef) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.FillCredential;
}
public sealed record BrowserWaitFor(string Condition, BrowserTarget? Target = null, string? Text = null, string? State = null, string? Url = null, int TimeoutMs = 2500) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.WaitFor;
}
public sealed record BrowserTabs(string Operation, string? TabRef = null, string? Url = null) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Tabs;
}
public sealed record BrowserDialog(string Operation, string? PromptText = null) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Dialog;
}
public sealed record BrowserResize(int Width, int Height) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Resize;
}
public sealed record BrowserClose() : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Close;
}
public sealed record BrowserScreenshot(string Format = "png", bool FullPage = false, BrowserTarget? Target = null) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Screenshot;
}
public sealed record BrowserConsoleMessages(string Level = "all", int Limit = 20) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.ConsoleMessages;
}
public sealed record BrowserNetworkRequests(int Limit = 20) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.NetworkRequests;
}
public sealed record BrowserNetworkRequest(string RequestRef) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.NetworkRequest;
}
public sealed record BrowserRoute(string Url, string Action, string Body = "", int Status = 200) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Route;
}
public sealed record BrowserRoutes() : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Routes;
}
public sealed record BrowserUnroute(string RuleRef) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Unroute;
}
public sealed record BrowserNetworkState(bool Online) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.NetworkState;
}
public sealed record BrowserCookies(string Operation, string? Name = null) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Cookies;
}
public sealed record BrowserLocalStorage(string Operation, string? Key = null, string? Value = null) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.LocalStorage;
}
public sealed record BrowserSessionStorage(string Operation, string? Key = null, string? Value = null) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.SessionStorage;
}
public sealed record BrowserVerify(string Condition, BrowserTarget? Target = null, string? Text = null, string? Value = null) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Verify;
}
public sealed record BrowserGenerateLocator(BrowserTarget Target) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.GenerateLocator;
}
public sealed record BrowserMouse(string Operation, float X, float Y, float? TargetX = null, float? TargetY = null, float? DeltaX = null, float? DeltaY = null) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Mouse;
}
public sealed record BrowserHighlight(BrowserTarget? Target = null, string Operation = "show") : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Highlight;
}
public sealed record BrowserEmulateMedia(string? Media = null, string? ColorScheme = null, string? ReducedMotion = null, string? ForcedColors = null, string? Contrast = null) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.EmulateMedia;
    public bool MediaSpecified { get; init; }
    public bool ColorSchemeSpecified { get; init; }
    public bool ReducedMotionSpecified { get; init; }
    public bool ForcedColorsSpecified { get; init; }
    public bool ContrastSpecified { get; init; }
}
public sealed record BrowserGetConfig() : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.GetConfig;
}
public sealed record BrowserSetGeolocation(string Operation, string Origin, double? Latitude = null, double? Longitude = null, double? Accuracy = null) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.SetGeolocation;
}
public sealed record BrowserScroll(float DeltaY, float DeltaX = 0, BrowserTarget? Target = null) : BrowserCommand
{
    public override BrowserOperation Kind => BrowserOperation.Scroll;
}

public sealed record BrowserRequest(Guid SessionId, BrowserCommand Command)
{
    public BrowserOperation Operation => Command.Kind;
}

public enum BrowserFeature { Navigate, Snapshot, Find, Click, Hover, Drag, Drop, Type, FillForm, SelectOption, PressKey, Upload, FillCredential, Wait, Tabs, Dialog, Resize, Close, Screenshot, Console, NetworkInspect, NetworkControl, Storage, Testing, VisionMouse, Scroll, Highlight, Media, Configuration, Geolocation }

public sealed record BrowserProviderDescriptor(string ProviderId, string DisplayName, IReadOnlySet<BrowserFeature> SupportedFeatures)
{
    public string Engine { get; init; } = "unknown";
    public bool Supports(BrowserFeature feature) => SupportedFeatures.Contains(feature);
}
