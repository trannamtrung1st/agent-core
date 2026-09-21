using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Tools;

public sealed class ToolConfigurationGate(
    IWebSearchProvider? webSearch,
    IPublicWebFetcher? publicWebFetcher) : IToolConfigurationGate
{
    public bool IsConfigured(string toolName) =>
        toolName switch
        {
            ToolCatalog.WebSearch => webSearch?.IsAvailable == true,
            ToolCatalog.WebFetch => publicWebFetcher is not null,
            _ => true
        };
}
