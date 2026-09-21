namespace AgentCore.Infrastructure.PublicWeb;

internal sealed record PublicWebTransportResponse(
    int StatusCode,
    Uri? RedirectLocation,
    string? ContentType,
    byte[] Body);

internal interface IPublicWebTransport
{
    ValueTask<PublicWebTransportResponse> GetAsync(Uri uri, CancellationToken cancellationToken);
}
