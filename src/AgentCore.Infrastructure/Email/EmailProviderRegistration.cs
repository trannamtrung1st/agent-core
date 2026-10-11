using Microsoft.Extensions.Configuration;
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

        services.TryAddSingleton<EmailTransportOptions>(p => p.GetService<Microsoft.Extensions.Configuration.IConfiguration>()?
            .GetSection("Email").Get<EmailTransportOptions>() ?? new());
        services.TryAddSingleton<IEmailProviderFactory>(p => new AccountEmailProviderFactory(
            p.GetRequiredService<ICredentialStore>(), p.GetRequiredService<IAgentCredentialBindingStore>(),
            p.GetRequiredService<ICredentialProtector>(), p.GetRequiredService<IAgentInstanceStore>(),
            p.GetRequiredService<EmailTransportOptions>(), p.GetRequiredService<TimeProvider>(),
            string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase)));
        services.TryAddSingleton<AgentCore.Application.Email.EmailAccountService>();
    }
}
