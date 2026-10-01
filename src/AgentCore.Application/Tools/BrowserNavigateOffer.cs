using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

internal static class BrowserNavigateOffer
{
    public const string SentencePrefix = "Trusted browser start: ";

    public static IReadOnlyList<ModelToolDefinition> Apply(
        IReadOnlyList<ModelToolDefinition> tools,
        IBrowserSession? browser)
    {
        var start = HomeUrl(browser);
        if (start is null)
        {
            return tools;
        }

        var updated = new List<ModelToolDefinition>(tools.Count);
        foreach (var tool in tools)
        {
            if (!string.Equals(tool.Name, ToolCatalog.BrowserNavigate, StringComparison.Ordinal))
            {
                updated.Add(tool);
                continue;
            }

            updated.Add(tool with { Description = tool.Description + " " + SentencePrefix + start + "." });
        }

        return updated;
    }

    public static string? HomeUrl(IBrowserSession? browser)
    {
        if (browser is not { IsAvailable: true })
        {
            return null;
        }

        string? home = null;
        foreach (var origin in browser.HostPolicy.NavigationOrigins)
        {
            if (!BrowserTargetPolicy.IsLoopback(origin)
                || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            home ??= new UriBuilder(uri) { Path = "/", Query = "", Fragment = "" }.Uri.AbsoluteUri;
        }

        return home;
    }
}
