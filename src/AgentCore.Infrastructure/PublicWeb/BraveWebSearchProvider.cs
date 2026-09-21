using System.Net.Http.Headers;
using System.Text.Json;
using AgentCore.Application.Ports;
using Microsoft.Extensions.Configuration;

namespace AgentCore.Infrastructure.PublicWeb;

public sealed class BraveWebSearchProvider(IConfiguration configuration, IHttpClientFactory httpClientFactory)
    : IWebSearchProvider
{
    public const string HttpClientName = "BraveWebSearch";

    public bool IsAvailable => !string.IsNullOrWhiteSpace(ApiKey);

    private string? ApiKey => configuration["BRAVE_SEARCH_API_KEY"] ?? configuration["BraveSearch:ApiKey"];

    public async ValueTask<WebSearchResult> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("Brave web search is not configured.");
        }

        var limit = Math.Clamp(request.Limit, 1, PublicWebLimits.MaxSearchResults);
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(request.Query)}&count={limit}");
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpRequest.Headers.Add("X-Subscription-Token", ApiKey);
        using var response = await client.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var results = new List<WebSearchItem>();
        if (document.RootElement.TryGetProperty("web", out var web)
            && web.TryGetProperty("results", out var items)
            && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var title = item.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? "" : "";
                var url = item.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? "" : "";
                var snippet = item.TryGetProperty("description", out var snippetElement)
                    ? snippetElement.GetString() ?? ""
                    : "";
                if (!string.IsNullOrWhiteSpace(url))
                {
                    results.Add(new WebSearchItem(title, url, snippet));
                }
            }
        }

        return new WebSearchResult(results, false);
    }
}
