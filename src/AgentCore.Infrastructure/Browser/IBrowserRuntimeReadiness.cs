namespace AgentCore.Infrastructure.Browser;

/// <summary>
/// Infrastructure proof that the Playwright Chromium executable can be resolved.
/// Application offers <c>browser.*</c> only through the configuration gate, which requires this signal.
/// </summary>
public interface IBrowserRuntimeReadiness
{
    bool IsRuntimeReady { get; }
}
