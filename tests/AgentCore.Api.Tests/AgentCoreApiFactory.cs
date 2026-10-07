using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.TestHost;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Api.Tests;

public class AgentCoreApiFactory : WebApplicationFactory<Program>
{
    protected virtual IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var repo = FindRepoRoot();
        builder.UseContentRoot(Path.Combine(repo, "src", "AgentCore.Api"));
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["AgentCore:Profile"] = "Synthetic",
                ["Browser:FixturePort"] = "0",
                ["Browser:Headless"] = "true",
                ["AgentCore:DetachGracePeriodSeconds"] = "0",
                ["AgentCore:AgentDirectory"] = Path.Combine(repo, "agents"),
                ["Providers:Speech:Recognition:Adapter"] = "Synthetic",
                ["Providers:Speech:Synthesis:Adapter"] = "Synthetic",
                ["Persistence:WorkspaceRoot"] = Path.Combine(Path.GetTempPath(), "agent-core-ws", Guid.NewGuid().ToString("N")),
                ["Persistence:TemplateRoot"] = Path.Combine(repo, "agents", "templates"),
                ["Persistence:ArtifactRoot"] = Path.Combine(Path.GetTempPath(), "agent-core-art", Guid.NewGuid().ToString("N"))
            };
            foreach (var pair in ExtraConfiguration)
            {
                values[pair.Key] = pair.Value;
            }

            config.AddInMemoryCollection(values);
        });
        // Minimal-host test configuration is applied after early application option binding.
        builder.ConfigureTestServices(services =>
        {
            var options = (PersistenceOptions)services.Single(d => d.ServiceType == typeof(PersistenceOptions)).ImplementationInstance!;
            options.WorkspaceRoot = Path.Combine(Path.GetTempPath(), "agent-core-api-isolated", Guid.NewGuid().ToString("N"), "workspaces");
            options.CredentialProtectionKeyRoot = Path.Combine(Path.GetTempPath(), "agent-core-api-isolated", Guid.NewGuid().ToString("N"), "credential-keys");
            if (ExtraConfiguration.TryGetValue("Persistence:WorkspaceRoot", out var root) && root is not null)
                options.WorkspaceRoot = root;
        });
        TestHttpDefaults.UseLoopbackCaller(builder);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AgentCore.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException();
    }
}
