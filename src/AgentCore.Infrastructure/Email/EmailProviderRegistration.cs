using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AgentCore.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Infrastructure.Email;

internal static class EmailProviderRegistration
{
    public static void AddEmail(IServiceCollection services)
    {
        services.AddLogging();
        services.AddHttpClient(GmailEmailProvider.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        }).WithErrorDiagnostics().RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 4 });

        services.TryAddSingleton<GoogleOAuthConfiguration>(sp => new(sp.GetService<IConfiguration>() ?? new ConfigurationBuilder().Build()));
        services.TryAddSingleton<EmailTransportOptions>(p => p.GetService<Microsoft.Extensions.Configuration.IConfiguration>()?
            .GetSection("Email").Get<EmailTransportOptions>() ?? new());
        services.TryAddSingleton<AgentCore.Application.Email.EmailMonitoringOptions>(p => p.GetService<IConfiguration>()?.GetSection("EmailMonitoring").Get<AgentCore.Application.Email.EmailMonitoringOptions>() ?? new());
    }
}
