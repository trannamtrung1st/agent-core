using AgentCore.Infrastructure.Http;
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
            string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase) ? new SyntheticHarnessWebFetcher(new PublicWebFetcher(provider.GetRequiredService<IPublicWebTransport>(), provider.GetService<ILogger<PublicWebFetcher>>(), provider.GetRequiredService<IDiagnosticIdSource>()))
                : new PublicWebFetcher(provider.GetRequiredService<IPublicWebTransport>(), provider.GetService<ILogger<PublicWebFetcher>>(), provider.GetRequiredService<IDiagnosticIdSource>()));
        services.TryAddSingleton<IHttpRequestClient>(provider =>
            new HttpRequestClient(provider.GetRequiredService<IPublicWebTransport>(), provider.GetService<ILogger<HttpRequestClient>>(), provider.GetRequiredService<IDiagnosticIdSource>()));
        services.AddHttpClient(BraveWebSearchProvider.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(PublicWebLimits.TotalTimeoutSeconds);
        }).WithErrorDiagnostics();

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
