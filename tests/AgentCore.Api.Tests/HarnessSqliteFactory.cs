using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

internal sealed class HarnessSqliteFactory(string root) : AgentCoreApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddDbContextFactory<AgentCoreDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "synthetic.db")}"));
            TestSqliteIdentity.Configure(services, $"Data Source={Path.Combine(root, "synthetic.db")}", Path.Combine(root, "identity-keys"));
            services.RemoveAll<IAgentInstanceStore>();
            services.AddSingleton<IAgentInstanceStore>(provider =>
            {
                var contexts = provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>();
                using var db = contexts.CreateDbContext();
                db.Database.Migrate();
                return new SqliteAgentInstanceStore(contexts, provider.GetRequiredService<IIdGenerator>());
            });
            services.RemoveAll<IDefinitionResourceContentStore>();
            services.AddSingleton<IDefinitionResourceContentStore>(new FileDefinitionResourceContentStore(Path.Combine(root, "resources")));
            services.RemoveAll<IAgentDefinitionAdminStore>();
            services.AddSingleton<IAgentDefinitionAdminStore, SqliteAgentDefinitionAdminStore>();
            services.RemoveAll<IAgentDefinitionResourceAdminStore>();
            services.AddSingleton<IAgentDefinitionResourceAdminStore, SqliteAgentDefinitionResourceAdminStore>();
            services.RemoveAll<IDefinitionDraftEvaluationStore>();
            services.AddSingleton<IDefinitionDraftEvaluationStore, SqliteDefinitionDraftEvaluationStore>();
            services.RemoveAll<IAdminEventStore>();
            services.AddSingleton<IAdminEventStore, SqliteAdminEventStore>();
        });
    }

    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?>
    {
        ["Persistence:Provider"] = "Sqlite", ["Persistence:ConnectionString"] = $"Data Source={Path.Combine(root, "synthetic.db")}",
        ["Persistence:DefinitionResourceRoot"] = Path.Combine(root, "resources"),
        ["Persistence:WorkspaceRoot"] = Path.Combine(root, "workspaces"), ["Persistence:AttachmentRoot"] = Path.Combine(root, "attachments"),
        ["Persistence:ArtifactRoot"] = Path.Combine(root, "artifacts")
    };
}
