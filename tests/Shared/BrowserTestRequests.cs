using AgentCore.Application.Ports;
namespace AgentCore.Tests.Shared;

// Fixture-only constructors for the canonical operation-specific commands.
public static class BrowserTestRequests
{
    public static BrowserRequest Navigate(Guid SessionId, Uri? Url, string Operation = "goto") => new(SessionId, new BrowserNavigate(Url?.AbsoluteUri, Operation));
    public static BrowserRequest Inspect(Guid id, CancellationToken ct = default) => new(id, new BrowserObserve());
    public static BrowserRequest Inspect(Guid id, BrowserWaitFor wait, CancellationToken ct = default) => new(id, wait);
    public static BrowserRequest Close(Guid id, CancellationToken ct = default) => new(id, new BrowserClose());
    public static BrowserRequest Screenshot(Guid SessionId, string Format = "png", bool FullPage = false, BrowserTarget? Target = null) => new(SessionId, new BrowserScreenshot(Format, FullPage, Target));
    public static BrowserRequest Tabs(Guid SessionId, string Operation, string? PageId = null) => new(SessionId, new BrowserTabs(Operation, PageId));
    public static BrowserRequest Interaction(Guid SessionId, BrowserOperation Operation, BrowserTarget? Target, string? Value,
        BrowserUpload? Upload = null, string? Direction = null, int Delta = 0, BrowserTarget? Destination = null,
        IReadOnlyList<BrowserUpload>? Uploads = null, int? ClickCount = null, bool? Checked = null) => new(SessionId, Operation switch
        {
            BrowserOperation.Click => new BrowserClick(Target!, ClickCount: ClickCount ?? 1),
            BrowserOperation.Hover => new BrowserHover(Target!),
            BrowserOperation.Type => new BrowserTypeText(Target!, Value!),
            BrowserOperation.SelectOption => new BrowserSelectOption(Target!, [Value!]),
            BrowserOperation.PressKey => new BrowserPressKey(Value!, Target),
            BrowserOperation.FillForm => new BrowserFillForm([new(Target!, Checked: Checked)]),
            BrowserOperation.Upload => new BrowserUploadCommand(Target!, Uploads: Uploads ?? (Upload is null ? [] : [Upload])),
            BrowserOperation.Scroll => new BrowserScroll(Direction == "up" ? -Delta : Delta, Target: Target),
            BrowserOperation.Drag => new BrowserDrag(Target!, Destination!),
            BrowserOperation.Drop => new BrowserDrop(Target!, Value),
            _ => throw new ArgumentException("Unsupported fixture action.")
        });
}
