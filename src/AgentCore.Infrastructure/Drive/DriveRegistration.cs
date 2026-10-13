using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace AgentCore.Infrastructure.Drive;

internal static class DriveRegistration
{
    internal static void AddDrive(IServiceCollection services, string profile)
    {
        services.TryAddSingleton<GoogleDriveOptions>(sp => sp.GetService<IConfiguration>()?.GetSection("GoogleDrive").Get<GoogleDriveOptions>() ?? new());
        services.TryAddSingleton<GoogleDriveOAuthOptions>(sp => sp.GetService<IConfiguration>()?.GetSection("GoogleDriveOAuth").Get<GoogleDriveOAuthOptions>() ?? new());
        services.AddHttpClient(GoogleDriveOAuthService.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20)).WithErrorDiagnostics().RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 4 });
        services.AddHttpClient(GoogleDriveProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30)).WithErrorDiagnostics().RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 4 });
        var synthetic = string.Equals(profile, "Synthetic", StringComparison.OrdinalIgnoreCase);
        services.TryAddSingleton<GoogleDriveOAuthService>(sp => new(sp.GetRequiredService<ICredentialStore>(), sp.GetRequiredService<ICredentialProtector>(), sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<GoogleDriveOptions>(), sp.GetRequiredService<GoogleDriveOAuthOptions>(), sp.GetRequiredService<TimeProvider>(), synthetic));
        services.TryAddSingleton<IGoogleDriveOAuthService>(sp => sp.GetRequiredService<GoogleDriveOAuthService>());
        services.TryAddSingleton<IGoogleDriveProviderFactory>(sp => new GoogleDriveProviderFactory(sp.GetRequiredService<GoogleDriveOptions>(), sp.GetRequiredService<GoogleDriveOAuthService>(), sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IDriveOperationStore>(), sp.GetRequiredService<ISyntheticDriveStore>(), sp.GetRequiredService<ICredentialProtector>(), sp.GetRequiredService<IIdGenerator>(), sp.GetRequiredService<TimeProvider>(), synthetic));
    }
}
