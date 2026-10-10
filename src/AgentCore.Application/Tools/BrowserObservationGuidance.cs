namespace AgentCore.Application.Tools;

/// <summary>One contextual browser strategy, derived from eligible authority rather than global instructions.</summary>
public static class BrowserObservationGuidance
{
    public static string Render(IEnumerable<string> eligibleTools, bool vision, bool screenshotAvailable = true)
    {
        var tools = eligibleTools.ToHashSet(StringComparer.Ordinal);
        if (!tools.Any(name => name.StartsWith("browser.", StringComparison.Ordinal))) return "";
        if (!screenshotAvailable) { tools.Remove(ToolCatalog.BrowserScreenshot); tools.Remove(ToolCatalog.BrowserVisionMouse); }
        var visual = vision && tools.Contains(ToolCatalog.BrowserScreenshot)
            ? " For unfamiliar complex dashboards, hierarchy/layout relationships, icon-only controls, charts or unexpected overlays, inspect an authorized screenshot proactively when it adds useful context; do not wait for repeated failures. Combine its snapshotId/tabRef and image with semantic evidence, then prefer a direct semantic target. Reuse a recent valid image for understanding unchanged state; do not capture after every action or alternate observations indefinitely. Capture failures/limits call for semantic recovery, not an unrestricted retry loop."
            : " Use semantic observation for this execution. Without authorized image delivery and model vision, screenshots are artifacts only; never claim visual understanding.";
        var mouse = vision && tools.Contains(ToolCatalog.BrowserScreenshot) && tools.Contains(ToolCatalog.BrowserVisionMouse)
            ? " Coordinates are a permitted fallback only when semantic targeting is insufficient: use observed viewport pixels and the fresh viewport screenshot snapshotId, never guessed positions. Navigation, tab/viewport/layout changes or an interaction invalidate coordinate evidence; refresh it before another coordinate action. A screenshot grants no new interaction authority."
            : " Use semantic interaction; visual coordinates are unavailable.";
        return "Browser workflow: semantic-first observation and native semantic targeting suffice for labeled forms, navigation and tables. "
            + BrowserToolArguments.TargetGuidance + " Focused find is optional for deep/ambiguous content; within scopes repeated controls. Render virtualized content before targeting it."
            + (screenshotAvailable ? "" : " Host policy disables screenshots; use semantic observation and do not request captures.") + visual + mouse
            + " Try only a small number of justified semantic alternatives; repeated target_missing calls require a change in evidence or strategy, not equivalent guesses. target_missing does not imply a native dialog: recommend browser.dialog only after dialog_pending evidence."
            + " Act once, reobserve and verify the application outcome independently with browser.verify or fresh state; SDK success alone is insufficient. Stop on policy/provider denial, do not replay uncertain effects. Discover missing eligible tools with capabilities.load. Justify dialog decisions; close proves context closure only. If the user independently requested browser closure, attempt it even if logout fails; honor conditional closure and preservation instructions. Report logout and closure separately. Finish the direct user reply with chat.respond.";
    }
}
