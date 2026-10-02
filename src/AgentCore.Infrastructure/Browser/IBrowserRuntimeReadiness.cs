namespace AgentCore.Infrastructure.Browser;

/// <summary>
/// Infrastructure proof that the configured browser launch target is ready (Playwright Chromium or a probed Channel).
/// Application offers <c>browser.*</c> only through the configuration gate, which requires this signal.
/// </summary>
public interface IBrowserRuntimeReadiness
{
    bool IsRuntimeReady { get; }
}
