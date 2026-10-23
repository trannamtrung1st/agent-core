using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Email;

namespace AgentCore.Infrastructure.Tools;

public sealed class ToolConfigurationGate(
    IWebSearchProvider? webSearch,
    IPublicWebFetcher? publicWebFetcher,
    IBrowser? browserSession = null,
    bool browserEnabled = false, EmailTransportOptions? emailOptions = null, bool synthetic = false, ITenantCredentialOAuthProvider? oauth = null, ISandboxReadiness? sandbox = null, IHttpRequestClient? http = null) : IToolConfigurationGate
{
    public bool IsExecutionConfigured(string toolName) => toolName == ToolCatalog.BrowserConfiguration
        ? browserSession is not null
        : ToolCatalog.IsBrowserTool(toolName)
        ? browserEnabled && browserSession is { IsAvailable: true }
            && browserSession is IBrowserRuntimeReadiness { IsRuntimeReady: true }
        : IsConfigured(toolName);

    public bool IsConfigured(string toolName) =>
        toolName switch
        {
            _ when toolName.StartsWith("drive.", StringComparison.Ordinal) => oauth?.Describe(AgentCore.Domain.Credentials.CredentialPurpose.GoogleDriveAccount) is { HostEnabled: true, Configured: true },
            ToolCatalog.SandboxStatus or ToolCatalog.SandboxProcessStatus or ToolCatalog.SandboxProfiles => true,
            _ when toolName.StartsWith("sandbox.", StringComparison.Ordinal) => sandbox?.IsAvailable == true,
            ToolCatalog.WebSearch => webSearch?.IsAvailable == true,
            ToolCatalog.HttpRequest => http?.SupportsTenantDispatchChecks == true,
            ToolCatalog.WebFetch => publicWebFetcher is not null,
            ToolCatalog.WebDownload => publicWebFetcher?.SupportsDownloads == true,
            ToolCatalog.EmailAccounts or ToolCatalog.EmailRequestAccount or ToolCatalog.EmailSearch or ToolCatalog.EmailRead or ToolCatalog.EmailCreateDraft
                => emailOptions?.Enabled == true,
            ToolCatalog.EmailSend => emailOptions is { Enabled: true } && (synthetic ? emailOptions.SendEnabled != false : emailOptions.EffectiveSendEnabled),
            ToolCatalog.BrowserConfiguration => browserSession?.Provider.Supports(BrowserFeature.Configuration) == true,
            _ when ToolCatalog.IsBrowserTool(toolName) =>
                browserEnabled
                && browserSession is { IsAvailable: true }
                && BrowserToolCatalog.TryGet(toolName, out var metadata)
                && browserSession.Provider.Supports(metadata.Feature)
                && (toolName is not (ToolCatalog.BrowserScreenshot or ToolCatalog.BrowserVisionMouse)
                    || browserSession.HostPolicy.ScreenshotAvailable)
                && browserSession is IBrowserRuntimeReadiness { IsRuntimeReady: true },
            _ => true
        };
}
