using AgentCore.Application.Ports;
namespace AgentCore.Tests.Shared;

// Typed request builders used by adapter fixtures; no alternate provider or model schema.
public static class BrowserTestRequests
{
    public static BrowserRequest Navigate(Guid SessionId, Uri? Url, string Operation = "goto") =>
        new(SessionId, BrowserOperation.Navigate, new() { Url = Url?.AbsoluteUri, Operation = Operation });
    public static BrowserRequest Inspect(Guid id, CancellationToken ct = default) =>
        new(id, BrowserOperation.Snapshot, new());
    public static BrowserRequest Inspect(Guid id, BrowserOptionsData options, CancellationToken ct = default) =>
        new(id, BrowserOperation.WaitFor, options);
    public static BrowserRequest Close(Guid id, CancellationToken ct = default) => new(id, BrowserOperation.Close, new());
    public static BrowserRequest Screenshot(Guid SessionId, string Format = "png", bool FullPage = false, string? TargetRef = null) =>
        new(SessionId, BrowserOperation.Screenshot, new() { Format = Format, FullPage = FullPage, TargetRef = TargetRef });
    public static BrowserRequest Tabs(Guid SessionId, string Operation, string? PageId = null) =>
        new(SessionId, BrowserOperation.Tabs, new() { Operation = Operation, TabRef = PageId });
    public static BrowserRequest Interaction(Guid SessionId, BrowserOperation Operation, string Ref, string? Value,
        BrowserUpload? Upload = null, string? Direction = null, int Delta = 0, string? TargetRef = null,
        IReadOnlyList<BrowserUpload>? Uploads = null, int? ClickCount = null, bool? Checked = null) =>
        new(SessionId, Operation, new()
        {
            Ref = string.IsNullOrEmpty(Ref) ? null : Ref, TargetRef = TargetRef, Text = Value, Key = Operation == BrowserOperation.PressKey ? Value : null,
            Values = Operation == BrowserOperation.SelectOption ? [Value!] : null,
            Fields = Operation == BrowserOperation.FillForm ? [new(Ref, Checked: Checked)] : null,
            Uploads = Uploads ?? (Upload is null ? null : [Upload]), ClickCount = ClickCount,
            DeltaY = Operation == BrowserOperation.Scroll ? Direction == "up" ? -Delta : Delta : null
        });
}
