using AgentCore.Infrastructure.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AgentCore.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Infrastructure.Email;

internal static class EmailProviderRegistration
{
    public static void AddEmail(IServiceCollection services, string profile)
    {
        services.AddLogging();
        services.AddHttpClient(GmailEmailProvider.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        }).WithErrorDiagnostics().RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 4 });

        services.AddHttpClient(GoogleEmailOAuthService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20)).WithErrorDiagnostics().RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 4 });
        services.TryAddSingleton<GoogleEmailOAuthOptions>(p => p.GetService<IConfiguration>()?.GetSection("GoogleEmailOAuth").Get<GoogleEmailOAuthOptions>() ?? new());
        services.TryAddSingleton<GoogleEmailOAuthService>(p => new(p.GetRequiredService<ICredentialStore>(), p.GetRequiredService<ICredentialProtector>(),
            p.GetRequiredService<IHttpClientFactory>(), p.GetRequiredService<GoogleEmailOAuthOptions>(), p.GetRequiredService<TimeProvider>(),
            string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase), p.GetRequiredService<ILogger<GoogleEmailOAuthService>>(), p.GetRequiredService<IDiagnosticIdSource>()));
        services.TryAddSingleton<IEmailOAuthService>(p => p.GetRequiredService<GoogleEmailOAuthService>());
        services.TryAddSingleton<EmailTransportOptions>(p => p.GetService<Microsoft.Extensions.Configuration.IConfiguration>()?
            .GetSection("Email").Get<EmailTransportOptions>() ?? new());
        services.TryAddSingleton<AccountEmailProviderFactory>(p => new AccountEmailProviderFactory(
            p.GetRequiredService<ICredentialStore>(), p.GetRequiredService<IAgentCredentialBindingStore>(),
            p.GetRequiredService<ICredentialProtector>(), p.GetRequiredService<IAgentInstanceStore>(),
            p.GetRequiredService<EmailTransportOptions>(), p.GetRequiredService<TimeProvider>(),
            string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase), p.GetRequiredService<GoogleEmailOAuthService>(), p.GetRequiredService<IHttpClientFactory>(), p.GetRequiredService<ILoggerFactory>(), p.GetRequiredService<IDiagnosticIdSource>()));
        services.TryAddSingleton<IEmailProviderFactory>(p => p.GetRequiredService<AccountEmailProviderFactory>());
        services.TryAddSingleton<AgentCore.Application.Email.EmailMonitoringOptions>(p => p.GetService<IConfiguration>()?.GetSection("EmailMonitoring").Get<AgentCore.Application.Email.EmailMonitoringOptions>() ?? new());
        services.TryAddSingleton<IEmailChangeFeed>(p => new AccountEmailChangeFeed(p.GetRequiredService<ICredentialStore>(), p.GetRequiredService<ICredentialProtector>(),
            p.GetRequiredService<EmailTransportOptions>(), p.GetRequiredService<GoogleEmailOAuthService>(), p.GetRequiredService<IHttpClientFactory>(),
            p.GetRequiredService<TimeProvider>(), p.GetRequiredService<AccountEmailProviderFactory>(), string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase)));
        services.TryAddSingleton<AgentCore.Application.Email.EmailMonitoringService>();
        services.TryAddSingleton<AgentCore.Application.Email.EmailAccountService>();
    }
}
