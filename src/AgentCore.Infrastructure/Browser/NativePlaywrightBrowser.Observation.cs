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
        RemoveRefs(sessionId);
        BeginCall(session);
        session.Page = await session.Context.NewPageAsync();
        RememberPage(session, session.Page); session.Generation++;
        if (IsAllowed(session, url))
            try { await session.Page.GotoAsync(url, new() { Timeout = 1000, WaitUntil = WaitUntilState.DOMContentLoaded }); }
            catch (PlaywrightException) { }
    }

    private async Task<BrowserSnapshot> ObserveAsync(SessionBrowser session, Guid sessionId,
        ILocator scope, int depth, string? scopeRef, CancellationToken ct, bool boxes = false)
    {
        if (CaptureProbe?.Invoke() is { } failure) throw failure;
        var native = await scope.AriaSnapshotAsync(new() { Depth = depth, Timeout = TimeoutMs() }).WaitAsync(ct);
        var secrets = await CollectSecretsAsync(session, ct);
        // Redaction precedes clipping: a partial protected value must never escape.
        var safe = Redact(native, secrets, session.ProtectedValues);
        var frames = new List<BrowserFrameInfo>();
        foreach (var frame in session.Page.Frames.Where(f => f != session.Page.MainFrame && Allows(session, f.Url, true)).Take(20))
        {
            var reference = session.Frames.FirstOrDefault(p => p.Value == frame).Key;
            if (reference is null) { reference = "fr_" + Guid.NewGuid().ToString("N"); session.Frames[reference] = frame; }
            frames.Add(new(reference, SafeBrowserUrl(frame.Url, secrets, session.ProtectedValues), Redact(frame.Name, secrets, session.ProtectedValues)));
        }
        return new(SafeBrowserUrl(session.Page.Url, secrets, session.ProtectedValues),
            Clip(Redact(await session.Page.TitleAsync().WaitAsync(ct), secrets, session.ProtectedValues), BrowserToolLimits.MaxTitleLength),
            ToolJsonResults.ClipUtf8Prefix(safe, BrowserToolLimits.MaxSnapshotBytes),
            depth < 32 || System.Text.Encoding.UTF8.GetByteCount(safe) > BrowserToolLimits.MaxSnapshotBytes, [], await ClassifyInterventionAsync(session.Page, ct),
            SnapshotId: "snap_" + Guid.NewGuid().ToString("N"), TabRef: FindPageId(session),
            Boxes: boxes && scopeRef is not null && await scope.BoundingBoxAsync().WaitAsync(ct) is { } box
                ? [new(scopeRef, box.X, box.Y, box.Width, box.Height)] : [],
            Scope: scopeRef, Frames: frames, HasPasswordField: await session.Page.Locator("input[type=password]:visible").CountAsync().WaitAsync(ct) > 0);
    }

    private async Task<BrowserResult> FindAsync(SessionBrowser session, Guid sessionId, BrowserTargetQuery? query, CancellationToken ct)
    {
        if (!BrowserToolArguments.ValidQuery(query) || query!.Limit is < 1 or > 20 || query.Offset is < 0 or > 4096) return new("invalid");
        var primary = new[] { query.Role, query.Text, query.Label, query.Placeholder, query.AltText, query.Title, query.TestId };
        if (primary.Count(x => x is not null) != 1 || query.Name is not null && query.Role is null) return new("invalid");
        ILocator root = session.Page.Locator("body");
        if (query.FrameRef is { } frameRef)
        {
            var frame = FindPageFrame(session, frameRef);
            if (frame is null) return new("stale_reference");
            if (!Allows(session, frame.Url, true)) return new("target_denied");
            root = frame.Locator("body");
        }
        if (query.ScopeRef is { } scopeRef)
        {
            var error = ReferenceError(scopeRef, sessionId, out var live);
            if (error is not null) return new(error);
            var count = await live!.Handle.CountAsync().WaitAsync(ct);
            if (count != 1) return new(count == 0 ? "target_missing" : "ambiguous_target");
            var origin = await live.Handle.EvaluateAsync<string>("el=>el.ownerDocument.location.href").WaitAsync(ct);
            if (!Allows(session, origin, true)) return new("target_denied");
            root = live.Handle;
        }
        ILocator locator;
        if (query.Role is { } role)
        {
            if (!Enum.TryParse<AriaRole>(role, true, out var nativeRole)) return new("invalid");
            locator = root.GetByRole(nativeRole, new() { Name = query.Name, Exact = query.Exact });
        }
        else if (query.Text is { } text) locator = root.GetByText(text, new() { Exact = query.Exact });
        else if (query.Label is { } label) locator = root.GetByLabel(label, new() { Exact = query.Exact });
        else if (query.Placeholder is { } placeholder) locator = root.GetByPlaceholder(placeholder, new() { Exact = query.Exact });
        else if (query.AltText is { } alt) locator = root.GetByAltText(alt, new() { Exact = query.Exact });
        else if (query.Title is { } title) locator = root.GetByTitle(title, new() { Exact = query.Exact });
        else locator = root.GetByTestId(query.TestId!);
        if (query.Visible is { } visible) locator = locator.Filter(new() { Visible = visible });
        var total = await locator.CountAsync().WaitAsync(ct);
        if (total == 0) return new("not_found");
        var secrets = await CollectSecretsAsync(session, ct);
        // Ordinals are used only for bounded observational samples, never executable authority.
        if (total > 1)
        {
            var samples = new List<object>();
            for (var i = query.Offset; i < Math.Min(total, query.Offset + query.Limit); i++)
            {
                var candidate = locator.Nth(i);
                var metadata = await candidate.EvaluateAsync<string>(DescribeElement).WaitAsync(ct);
                if (metadata == "null") continue;
                using var description = JsonDocument.Parse(metadata);
                samples.Add(new { role = query.Role ?? description.RootElement.GetProperty("role").GetString(),
                    name = Clip(Redact(description.RootElement.GetProperty("name").GetString(), secrets, session.ProtectedValues), BrowserToolLimits.MaxAccessibleNameLength),
                    visible = await candidate.IsVisibleAsync().WaitAsync(ct), informationalOnly = true });
            }
            return new("ambiguous_target", DataJson: JsonSerializer.Serialize(new
            { status = "ambiguous_target", matchCount = total, returnedCount = samples.Count,
                offset = query.Offset, nextOffset = Math.Min(total, query.Offset + query.Limit), hasMore = query.Offset + query.Limit < total,
                matches = samples, guidance = "These samples confer no target authority. Narrow the role/name or find a unique region and use scopeRef." }));
        }
        if (query.Offset != 0) return new("not_found");
        var described = await locator.EvaluateAsync<string>(DescribeElement).WaitAsync(ct);
        if (described == "null") return new("forbidden");
        using var doc = JsonDocument.Parse(described);
        var actions = doc.RootElement.GetProperty("actions").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var name = Redact(query.Name ?? query.Text ?? query.Label ?? query.Placeholder ?? query.AltText ?? query.Title ?? query.TestId ??
            doc.RootElement.GetProperty("name").GetString(), secrets, session.ProtectedValues);
        // The registry is bounded per owned Session and retains native semantic Locators only.
        foreach (var pair in _refs.Where(p => p.Value.SessionId == sessionId && (p.Value.ExpiresAt < _time.GetUtcNow() || p.Value.Generation != session.Generation)))
            _refs.TryRemove(pair.Key, out _);
        if (_refs.Count(p => p.Value.SessionId == sessionId) >= 256) return new("reference_limit");
        var token = MintToken();
        _refs[token] = new(sessionId, session.Generation, locator, actions, _time.GetUtcNow().AddMinutes(10));
        var element = new BrowserElement(token, query.Role ?? doc.RootElement.GetProperty("role").GetString()!,
            Clip(name, BrowserToolLimits.MaxAccessibleNameLength), actions,
            ReadControlState(described, name, secrets, session.ProtectedValues));
        return new(null, DataJson: JsonSerializer.Serialize(new
        {
            status = "ok", tabRef = FindPageId(session), matches = new[] { new { @ref = element.Ref, role = element.Role, name = element.Name, actions = element.Actions, state = element.State is null ? null : new { value = element.State.Value, @checked = element.State.Checked, selectedText = element.State.SelectedText } } },
            matchCount = 1, returnedCount = 1, hasMore = false, truncated = false, untrustedBrowserContent = true
        }));
    }

    private static IFrame? FindPageFrame(SessionBrowser session, string reference) =>
        session.Frames.TryGetValue(reference, out var frame) && session.Page.Frames.Contains(frame) ? frame : null;
}
