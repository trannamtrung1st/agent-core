using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class NativePlaywrightBrowser
{
    private async Task<ILocator> BuildTargetAsync(SessionBrowser session, BrowserTarget target, CancellationToken ct)
    {
        if (!BrowserToolArguments.ValidTarget(target)) throw new BrowserTargetException("invalid_target");
        ILocator root = session.Page.Locator("body");
        if (target.FrameRef is { } frameRef)
        {
            var frame = FindPageFrame(session, frameRef);
            if (frame is null) throw new BrowserTargetException("stale_frame");
            if (!Allows(session, frame.Url, true)) throw new BrowserTargetDeniedException();
            root = frame.Locator("body");
        }
        if (target.Within is { } scope)
        {
            root = Semantic(root, scope.By, scope.Value, scope.Name, scope.Exact, scope.Visible, scope.HasText);
            var count = await root.CountAsync().WaitAsync(ct);
            if (count != 1) throw new BrowserTargetException(count == 0 ? "target_missing" : "ambiguous_target");
        }
        return Semantic(root, target.By, target.Value, target.Name, target.Exact, target.Visible, target.HasText);
    }

    private static ILocator Semantic(ILocator root, string by, string value, string? name, bool exact, bool? visible, string? hasText)
    {
        ILocator locator = by switch
        {
            "role" when Enum.TryParse<AriaRole>(value, true, out var role) && Enum.IsDefined(role)
                && !int.TryParse(value, out _) => root.GetByRole(role, new() { Name = name, Exact = exact }),
            "text" => root.GetByText(value, new() { Exact = exact }),
            "label" => root.GetByLabel(value, new() { Exact = exact }),
            "placeholder" => root.GetByPlaceholder(value, new() { Exact = exact }),
            "altText" => root.GetByAltText(value, new() { Exact = exact }),
            "title" => root.GetByTitle(value, new() { Exact = exact }),
            "testId" => root.GetByTestId(value),
            _ => throw new BrowserTargetException("invalid_target")
        };
        if (visible is { } shown) locator = locator.Filter(new() { Visible = shown });
        if (hasText is not null) locator = locator.Filter(new() { HasText = hasText });
        return locator;
    }

    private async Task<ILocator> ResolveTargetAsync(SessionBrowser session, BrowserTarget? target, bool mutation,
        CancellationToken ct, bool allowMissing = false, bool requireAction = false)
    {
        if (target is null) throw new BrowserTargetException("invalid_target");
        if (session.Page.IsClosed || !IsAllowed(session, session.Page.Url)) throw new BrowserTargetDeniedException();
        var locator = await BuildTargetAsync(session, target, ct);
        var count = await locator.CountAsync().WaitAsync(ct);
        if (count == 0 && !allowMissing) throw new BrowserTargetException("target_missing");
        if (count > 1) throw new BrowserTargetException("ambiguous_target");
        if (count == 0) return locator;
        var frameUrl = await locator.EvaluateAsync<string>("el => el.ownerDocument.location.href").WaitAsync(ct);
        if (mutation ? !BrowserTargetPolicy.EvaluateAct(_policy.InteractionMode, frameUrl,
                LeaseOrigins(session) ?? _policy.EffectiveInteractionOrigins, _policy.PolicyMode).Allowed
            : !Allows(session, frameUrl, true)) throw new BrowserTargetDeniedException();
        var description = await locator.EvaluateAsync<string>(DescribeElement).WaitAsync(ct);
        if (description == "null") throw new BrowserTargetException("forbidden");
        if (requireAction)
        {
            using var parsed = System.Text.Json.JsonDocument.Parse(description);
            if (parsed.RootElement.GetProperty("actions").GetArrayLength() == 0)
                throw new BrowserTargetException("non_actionable_target");
        }
        return locator;
    }
}
