using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.PublicWeb;

/// <summary>One credential-free deterministic public source for the Synthetic Chat learning journey.</summary>
internal sealed class SyntheticHarnessWebFetcher(IPublicWebFetcher inner) : IPublicWebFetcher
{
    public ValueTask<PublicWebFetchResult> FetchAsync(PublicWebFetchRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return request.Url.AbsoluteUri == "https://example.test/p97/order-policy"
            ? ValueTask.FromResult(new PublicWebFetchResult(request.Url.AbsoluteUri, "text/plain",
                "Check payment, shipping and fraud notes before acting on an order.", false, null, null))
            : inner.FetchAsync(request, cancellationToken);
    }
}
