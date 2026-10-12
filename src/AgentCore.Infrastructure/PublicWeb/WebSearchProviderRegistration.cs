using AgentCore.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Infrastructure.PublicWeb;

internal static class WebSearchProviderRegistration
{
    public static void AddPublicWeb(this IServiceCollection services, string profile)
    {
        services.TryAddSingleton<IPublicWebDnsResolver, SystemPublicWebDnsResolver>();
        services.TryAddSingleton<IPublicWebTransport, SocketsPublicWebTransport>();
        services.TryAddSingleton<IPublicWebFetcher>(provider =>
            string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase) ? new SyntheticHarnessWebFetcher(new PublicWebFetcher(provider.GetRequiredService<IPublicWebTransport>(), provider.GetService<ILogger<PublicWebFetcher>>()))
                : new PublicWebFetcher(provider.GetRequiredService<IPublicWebTransport>(), provider.GetService<ILogger<PublicWebFetcher>>()));
        services.TryAddSingleton<IHttpRequestClient>(provider =>
            new HttpRequestClient(provider.GetRequiredService<IPublicWebTransport>(), provider.GetService<ILogger<HttpRequestClient>>()));
        services.AddHttpClient(BraveWebSearchProvider.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(PublicWebLimits.TotalTimeoutSeconds);
        });

        if (string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<IWebSearchProvider, SyntheticWebSearchProvider>();
        }
        else
        {
            services.TryAddSingleton<IWebSearchProvider, BraveWebSearchProvider>();
        }
    }
}
