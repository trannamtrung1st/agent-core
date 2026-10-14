using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Browser;

namespace AgentCore.Infrastructure.Tools;

public sealed class ToolConfigurationGate(
    IWebSearchProvider? webSearch,
    IPublicWebFetcher? publicWebFetcher,
    IEmailProvider? emailProvider,
    IBrowser? browserSession = null,
    bool browserEnabled = false, IEmailProviderFactory? emailFactory = null, IGoogleDriveProviderFactory? driveFactory = null) : IToolConfigurationGate
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
            _ when toolName.StartsWith("drive.", StringComparison.Ordinal) => driveFactory is { Enabled: true, Configured: true },
            ToolCatalog.WebSearch => webSearch?.IsAvailable == true,
            ToolCatalog.WebFetch => publicWebFetcher is not null,
            ToolCatalog.WebDownload => publicWebFetcher?.SupportsDownloads == true,
            ToolCatalog.EmailAccounts or ToolCatalog.EmailRequestAccount or ToolCatalog.EmailSearch or ToolCatalog.EmailRead or ToolCatalog.EmailCreateDraft
                => emailFactory?.IsEnabled ?? emailProvider?.IsAvailable == true,
            ToolCatalog.EmailSend => emailFactory?.IsSendEnabled ?? emailProvider?.IsAvailable == true,
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
