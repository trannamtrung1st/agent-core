using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.PublicWeb;

internal sealed record PublicWebOutboundRequest(
    string Method,
    Uri Uri,
    IReadOnlyList<HttpRequestHeader> Headers,
    byte[] Body,
    int MaxResponseBytes = PublicWebLimits.MaxBodyBytes)
{
    internal Func<CancellationToken,ValueTask>? AuthorizeDispatch { get; init; }
}

internal sealed record PublicWebTransportResponse(
    int StatusCode,
    Uri? RedirectLocation,
    string? ContentType,
    byte[] Body,
    string? ContentDisposition = null,
    string? ContentEncoding = null);

internal interface IPublicWebTransport
{
    ValueTask<PublicWebTransportResponse> GetAsync(Uri uri, CancellationToken cancellationToken);

    ValueTask<PublicWebTransportResponse> SendAsync(
        PublicWebOutboundRequest request,
        CancellationToken cancellationToken);
}
