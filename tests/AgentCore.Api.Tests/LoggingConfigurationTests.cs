using Microsoft.Extensions.Configuration;

namespace AgentCore.Api.Tests;

public sealed class LoggingConfigurationTests
{
    private const string SqlCommandCategory = "Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command";

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void Shipped_appsettings_keep_ef_sql_command_logs_at_warning(string environment)
    {
        var configuration = LoadApiConfiguration(environment);
        Assert.Equal("Warning", configuration[SqlCommandCategory]);
    }

    private static IConfiguration LoadApiConfiguration(string environment)
    {
        var contentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "AgentCore.Api"));
        return new ConfigurationBuilder()
            .SetBasePath(contentRoot)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }
}
