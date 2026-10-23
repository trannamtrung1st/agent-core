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
        var options = configuration?.GetSection("GoogleDrive").Get<GoogleDriveOptions>() ?? new();
        if (configuration?["AGENTCORE_DRIVE_ENABLED"] is { Length: > 0 } external)
        {
            if (!bool.TryParse(external, out var enabled)) throw new InvalidOperationException("AGENTCORE_DRIVE_ENABLED must be true or false.");
            options.Enabled = enabled;
        }
        return options;
    }
    internal static void AddDrive(IServiceCollection services)
    {
        services.TryAddSingleton<GoogleDriveOptions>(sp => DriveHostOptions(sp.GetService<IConfiguration>()));
        services.TryAddSingleton<GoogleOAuthConfiguration>(sp => new(sp.GetService<IConfiguration>() ?? new ConfigurationBuilder().Build(), sp.GetService<Microsoft.Extensions.Logging.ILogger<GoogleOAuthConfiguration>>()));
        services.AddHttpClient(GoogleDriveProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30)).WithErrorDiagnostics().RemoveAllLoggers().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 4 });
    }
}
