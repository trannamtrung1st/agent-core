using AgentCore.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Infrastructure.Email;

internal static class EmailProviderRegistration
{
    public static void AddEmail(IServiceCollection services, string profile)
    {
        services.AddHttpClient(GmailEmailProvider.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        if (string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<IEmailProvider, SyntheticEmailProvider>();
        }
        else
        {
            services.TryAddSingleton<IEmailProvider, GmailEmailProvider>();
        }
    }
}
