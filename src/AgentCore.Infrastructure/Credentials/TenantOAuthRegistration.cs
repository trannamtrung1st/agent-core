using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Drive;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using AgentCore.Infrastructure.Email;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Infrastructure.Credentials;

internal static class TenantOAuthRegistration
{
    internal static void Add(IServiceCollection services, string profile)
    {
        // Auth-code and revoke bodies must never enter HTTP logging/diagnostic handlers or retries.
        services.AddHttpClient(TenantGoogleOAuthProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20)).RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = 4 });
        services.TryAddSingleton<TenantOAuthProofProtector>(p => new(p.GetRequiredService<PersistenceOptions>().CredentialProtectionKeyRoot));
        services.TryAddSingleton<TenantGoogleOAuthProvider>(p => new TenantGoogleOAuthProvider(p.GetRequiredService<GoogleOAuthConfiguration>(),
            p.GetRequiredService<GoogleDriveOptions>(), p.GetRequiredService<TenantOAuthProofProtector>(), p.GetRequiredService<ICredentialProtector>(),
            p.GetRequiredService<IHttpClientFactory>(), string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase)));
        services.TryAddSingleton<TenantConnectedAccountReader>(p => new TenantConnectedAccountReader(p.GetRequiredService<TenantGoogleOAuthProvider>(),
            p.GetRequiredService<EmailTransportOptions>(), p.GetRequiredService<GoogleDriveOptions>(), p.GetRequiredService<IHttpClientFactory>(),
            p.GetRequiredService<ICredentialProtector>(), p.GetRequiredService<IDriveOperationStore>(), p.GetRequiredService<ISyntheticDriveStore>(),
            p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>(), string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase),
            p.GetRequiredService<ILoggerFactory>(), p.GetRequiredService<IDiagnosticIdSource>()));
        services.TryAddSingleton<ITenantConnectedAccountReader>(p => p.GetRequiredService<TenantConnectedAccountReader>());
        services.TryAddSingleton<ITenantConnectedAccountTransportFactory>(p => p.GetRequiredService<TenantConnectedAccountReader>());
        services.TryAddSingleton<ITenantCredentialOAuthProvider>(p => p.GetRequiredService<TenantGoogleOAuthProvider>());
        services.TryAddSingleton<ITenantCredentialOAuthVerifier>(p => p.GetRequiredService<TenantGoogleOAuthProvider>());
    }
}
