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
    bool SupportsDownloads => false;

    ValueTask<PublicWebDownloadResult> DownloadAsync(
        PublicWebDownloadRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new PublicWebDownloadResult(request.Url.ToString(), null, null, [], "unavailable", "Public file downloads are not configured."));

    ValueTask<PublicWebFetchResult> FetchAsync(
        PublicWebFetchRequest request,
        CancellationToken cancellationToken = default);
}

// Normalized file bytes travel only to the owned Workspace, never to model tool output.
public sealed record PublicWebDownloadRequest(Uri Url);
public sealed record PublicWebDownloadResult(string FinalUrl, string? ContentType, string? SuggestedFileName,
    byte[] Bytes, string? ErrorCode, string? ErrorMessage);

public sealed record HttpRequestHeader(string Name, string Value);

public sealed record HttpToolRequest(
    string Method,
    Uri Url,
    IReadOnlyList<HttpRequestHeader> Headers,
    byte[] Body,
    bool FollowRedirects);

public sealed record HttpToolResponse(
    int StatusCode,
    string FinalUrl,
    string? ContentType,
    string Body,
    bool Truncated,
    bool Untrusted,
    string? RedirectLocation,
    string? ErrorCode,
    string? ErrorMessage);

public interface IHttpRequestClient
{
    ValueTask<HttpToolResponse> SendAsync(
        HttpToolRequest request,
        CancellationToken cancellationToken = default);
}
