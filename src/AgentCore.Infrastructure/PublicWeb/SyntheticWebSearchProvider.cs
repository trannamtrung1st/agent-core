using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.PublicWeb;

public sealed class SyntheticWebSearchProvider : IWebSearchProvider
{
    public bool IsAvailable => true;

    public ValueTask<WebSearchResult> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = request.Query.Trim();
        var limit = Math.Clamp(request.Limit, 1, PublicWebLimits.MaxSearchResults);
        var results = new List<WebSearchItem>(limit);
        for (var i = 0; i < limit; i++)
        {
            results.Add(new WebSearchItem(
                $"Synthetic search result {i + 1}",
                $"https://example.test/search?q={Uri.EscapeDataString(query)}&n={i + 1}",
                $"Deterministic fixture snippet for \"{query}\" (item {i + 1})."));
        }

        return ValueTask.FromResult(new WebSearchResult(results, false));
    }
}
