using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class NativePlaywrightBrowser
{
    private async Task FencePageAsync(SessionBrowser session, Guid sessionId)
    {
        var previous = session.Page;
        var url = previous.Url;
        await previous.CloseAsync(); ForgetPage(session, previous);
        session.Dialog = null; session.PendingAction = null;
        BeginCall(session);
        session.Page = await session.Context.NewPageAsync();
        RememberPage(session, session.Page); AdvanceGeneration(session);
        if (IsAllowed(session, url))
            try { await session.Page.GotoAsync(url, new() { Timeout = 1000, WaitUntil = WaitUntilState.DOMContentLoaded }); }
            catch (PlaywrightException) { }
    }

    private async Task<BrowserSnapshot> ObserveAsync(SessionBrowser session, Guid sessionId,
        ILocator scope, int depth, BrowserTarget? target, CancellationToken ct, bool boxes = false)
    {
        if (CaptureProbe?.Invoke() is { } failure) throw failure;
        var native = await scope.AriaSnapshotAsync(new() { Depth = depth, Timeout = TimeoutMs() }).WaitAsync(ct);
        var secrets = await CollectSecretsAsync(session, ct);
        // Redaction precedes clipping: a partial protected value must never escape.
        var safe = Redact(native, secrets, session.ProtectedValues);
        var frames = new List<BrowserFrameInfo>();
        foreach (var frame in session.Page.Frames.Where(f => f != session.Page.MainFrame && Allows(session, f.Url, true)).Take(20))
        {
            var reference = session.Frames.FirstOrDefault(p => p.Value.Frame == frame && p.Value.Generation == session.Generation).Key;
            if (reference is null) { reference = "fr_" + Guid.NewGuid().ToString("N"); session.Frames[reference] = new(frame, session.Generation); }
            frames.Add(new(reference, SafeBrowserUrl(frame.Url, secrets, session.ProtectedValues), Redact(frame.Name, secrets, session.ProtectedValues)));
        }
        return new(SafeBrowserUrl(session.Page.Url, secrets, session.ProtectedValues),
            Clip(Redact(await session.Page.TitleAsync().WaitAsync(ct), secrets, session.ProtectedValues), BrowserToolLimits.MaxTitleLength),
            ToolJsonResults.ClipUtf8Prefix(safe, BrowserToolLimits.MaxSnapshotBytes),
            depth < 32 || System.Text.Encoding.UTF8.GetByteCount(safe) > BrowserToolLimits.MaxSnapshotBytes, [], await ClassifyInterventionAsync(session.Page, ct),
            SnapshotId: "snap_" + Guid.NewGuid().ToString("N"), TabRef: FindPageId(session),
            Boxes: boxes && target is not null && await scope.BoundingBoxAsync().WaitAsync(ct) is { } box
                ? [new(target, box.X, box.Y, box.Width, box.Height)] : [],
            Scope: target, Frames: frames, HasPasswordField: await session.Page.Locator("input[type=password]:visible").CountAsync().WaitAsync(ct) > 0);
    }

    private async Task<BrowserResult> FindAsync(SessionBrowser session, BrowserFind query, CancellationToken ct)
    {
        if (!BrowserToolArguments.ValidTarget(query.Target) || query.Limit is < 1 or > 20 || query.Offset is < 0 or > 4096) return new("invalid_target");
        var locator = await BuildTargetAsync(session, query.Target, ct);
        var total = await locator.CountAsync().WaitAsync(ct);
        if (total == 0 || query.Offset >= total) return new("not_found");
        var secrets = await CollectSecretsAsync(session, ct);
        var samples = new List<object>();
        // Ordinals sample observations only; direct actions always use a strict unique semantic Locator.
        for (var i = query.Offset; i < Math.Min(total, query.Offset + query.Limit); i++)
        {
            var candidate = locator.Nth(i);
            var metadata = await candidate.EvaluateAsync<string>(DescribeElement).WaitAsync(ct);
            if (metadata == "null") continue;
            var url = await candidate.EvaluateAsync<string>("el => el.ownerDocument.location.href").WaitAsync(ct);
            if (!Allows(session, url, true)) throw new BrowserTargetDeniedException();
            using var description = JsonDocument.Parse(metadata);
            var state = ReadControlState(metadata, "", secrets, session.ProtectedValues);
            samples.Add(new { target = query.Target, role = description.RootElement.GetProperty("role").GetString(),
                name = Clip(Redact(description.RootElement.GetProperty("name").GetString(), secrets, session.ProtectedValues), BrowserToolLimits.MaxAccessibleNameLength),
                actions = description.RootElement.GetProperty("actions").EnumerateArray().Select(a => a.GetString()).ToArray(),
                state = state is null ? null : new { value = state.Value, @checked = state.Checked, selectedText = state.SelectedText },
                visible = await candidate.IsVisibleAsync().WaitAsync(ct), informationalOnly = total != 1 });
        }
        if (samples.Count == 0) return new("forbidden");
        return new(total == 1 ? null : "ambiguous_target", DataJson: JsonSerializer.Serialize(new
        {
            status = total == 1 ? "ok" : "ambiguous_target", tabRef = FindPageId(session), matches = samples,
            matchCount = total, returnedCount = samples.Count, offset = query.Offset,
            nextOffset = Math.Min(total, query.Offset + query.Limit), hasMore = query.Offset + query.Limit < total,
            guidance = "Act directly using target semantics; narrow duplicates with within or hasText. Samples confer no positional authority.",
            untrustedBrowserContent = true
        }, JsonSerializerOptions.Web));
    }

    private static IFrame? FindPageFrame(SessionBrowser session, string reference) =>
        session.Frames.TryGetValue(reference, out var binding) && binding.Generation == session.Generation
            && session.Page.Frames.Contains(binding.Frame) ? binding.Frame : null;
}
