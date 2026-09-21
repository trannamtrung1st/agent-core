using AgentCore.Application.Ports;
using Microsoft.Extensions.Configuration;
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
            new PublicWebFetcher(provider.GetRequiredService<IPublicWebTransport>()));
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
