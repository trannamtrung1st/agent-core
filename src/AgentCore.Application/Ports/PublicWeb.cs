namespace AgentCore.Application.Ports;

public sealed record WebSearchRequest(string Query, int Limit);

public sealed record WebSearchItem(string Title, string Url, string Snippet);

public sealed record WebSearchResult(IReadOnlyList<WebSearchItem> Results, bool Truncated);

public interface IWebSearchProvider
{
    bool IsAvailable { get; }

    ValueTask<WebSearchResult> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken = default);
}

public sealed record PublicWebFetchRequest(Uri Url);

public sealed record PublicWebFetchResult(
    string FinalUrl,
    string? ContentType,
    string Text,
    bool Truncated,
    string? ErrorCode,
    string? ErrorMessage);

public interface IPublicWebFetcher
{
    ValueTask<PublicWebFetchResult> FetchAsync(
        PublicWebFetchRequest request,
        CancellationToken cancellationToken = default);
}
