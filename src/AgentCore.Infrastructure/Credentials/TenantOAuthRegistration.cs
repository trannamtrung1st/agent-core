using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Drive;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
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
        services.TryAddSingleton<ITenantCredentialOAuthProvider>(p => p.GetRequiredService<TenantGoogleOAuthProvider>());
        services.TryAddSingleton<ITenantCredentialOAuthVerifier>(p => p.GetRequiredService<TenantGoogleOAuthProvider>());
    }
}
