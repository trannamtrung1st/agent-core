using AgentCore.Infrastructure.Credentials;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace AgentCore.Infrastructure.Drive;

internal static class DriveRegistration
{
    private static GoogleDriveOptions DriveHostOptions(IConfiguration? configuration)
    {
        if (configuration is not null) Configuration.ConfigurationCutover.Validate(configuration);
        return configuration?.GetSection("GoogleDrive").Get<GoogleDriveOptions>() ?? new();
    }
    internal static void AddDrive(IServiceCollection services)
    {
        services.TryAddSingleton<GoogleDriveOptions>(sp => DriveHostOptions(sp.GetService<IConfiguration>()));
        services.TryAddSingleton<GoogleOAuthConfiguration>(sp => new(sp.GetService<IConfiguration>() ?? new ConfigurationBuilder().Build()));
        services.AddHttpClient(GoogleDriveProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30)).WithErrorDiagnostics().RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 4 });
    }
}
