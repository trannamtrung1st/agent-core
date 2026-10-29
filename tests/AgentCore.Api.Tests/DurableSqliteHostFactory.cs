using Microsoft.Extensions.DependencyInjection.Extensions;
using AgentCore.Application.Execution;
using System.Net.Http.Json;
using AgentCore.Api;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

internal class DurableSqliteHostFactory(string dbPath, bool runScheduler = true, ILanguageModel? languageModel = null) : AgentCoreTestHostFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var repo = FindRepoRoot();
        builder.UseContentRoot(Path.Combine(repo, "src", "AgentCore.Api"));
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentCore:Profile"] = "Synthetic",
                ["Browser:FixturePort"] = "0",
                ["Browser:Headless"] = "true",
                ["AgentCore:AgentDirectory"] = Path.Combine(repo, "agents"),
                ["Persistence:Provider"] = "Sqlite",
                ["Persistence:ConnectionString"] = $"Data Source={dbPath}"
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICoreEventStore>();
            services.AddSingleton<ICoreEventStore, SqliteCoreEventStore>();
            services.RemoveAll<IAgentRunStore>();
            services.AddSingleton<IAgentRunStore>(provider => new SqliteAgentRunStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                new SqliteMemoryStore(provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(), provider.GetRequiredService<TimeProvider>()),
                provider.GetRequiredService<AgentCore.Application.Ports.IDiagnosticIdSource>()));
            var persistence = new PersistenceOptions
            {
                Provider = "Sqlite",
                ConnectionString = $"Data Source={dbPath}",
                WorkspaceRoot = dbPath + ".workspaces"
            };
            foreach (var option in services.Where(item => item.ServiceType == typeof(PersistenceOptions)).ToArray())
            {
                services.Remove(option);
            }

            services.AddSingleton(persistence);
            TestSqliteIdentity.Configure(services, persistence.ConnectionString, dbPath + ".identity-keys");
            if (services.All(item => item.ServiceType != typeof(IDbContextFactory<AgentCoreDbContext>)))
            {
                services.AddDbContextFactory<AgentCoreDbContext>(options => options.UseSqlite(persistence.ConnectionString));
            }

            foreach (var store in services.Where(item => item.ServiceType == typeof(IMemoryStore)).ToArray())
            {
                services.Remove(store);
            }

            services.AddSingleton<IMemoryStore>(provider =>
            {
                var sqlite = new SqliteMemoryStore(
                    provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                    provider.GetRequiredService<TimeProvider>());
                sqlite.EnsureCreatedAsync().AsTask().GetAwaiter().GetResult();
                return sqlite;
            });

            foreach (var store in services.Where(item => item.ServiceType == typeof(ITriggerStore)).ToArray())
            {
                services.Remove(store);
            }

            services.AddSingleton<ITriggerStore>(provider => new SqliteTriggerStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>()));

            foreach (var store in services.Where(item => item.ServiceType == typeof(IAgentInstanceStore)).ToArray())
            {
                services.Remove(store);
            }

            services.AddSingleton<IAgentInstanceStore>(provider => new SqliteAgentInstanceStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                provider.GetRequiredService<IIdGenerator>()));

            foreach (var store in services.Where(item => item.ServiceType == typeof(IAgentRunStore)).ToArray())
            {
                services.Remove(store);
            }

            services.AddSingleton<IAgentRunStore>(provider => new SqliteAgentRunStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
                (SqliteMemoryStore)provider.GetRequiredService<IMemoryStore>(), provider.GetRequiredService<IDiagnosticIdSource>()));

            foreach (var store in services.Where(item => item.ServiceType == typeof(ICredentialStore) || item.ServiceType == typeof(IAgentCredentialBindingStore)).ToArray())
            {
                services.Remove(store);
            }

            services.RemoveAll<IEmailWatchStore>();
            services.AddSingleton<IEmailWatchStore, SqliteEmailWatchStore>();
            services.RemoveAll<IEmailStore>();
            services.AddSingleton<IEmailStore, SqliteEmailStore>();
            services.AddSingleton<SqliteCredentialStore>();
            services.AddSingleton<ICredentialStore>(provider => provider.GetRequiredService<SqliteCredentialStore>());
            services.AddSingleton<IAgentCredentialBindingStore>(provider => provider.GetRequiredService<SqliteCredentialStore>());

            foreach (var store in services.Where(item => item.ServiceType == typeof(IExternalEventStore)).ToArray())
            {
                services.Remove(store);
            }

            services.AddSingleton<IExternalEventStore>(provider => new SqliteExternalEventStore(
                provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>()));

            if (!runScheduler)
            {
                foreach (var hosted in services.Where(item =>
                             item.ImplementationType == typeof(TriggerSchedulerHostedService)
                             || item.ImplementationType == typeof(EmailWatchHostedService)
                             || item.ImplementationType == typeof(AgentRunHostedService)).ToArray())
                {
                    services.Remove(hosted);
                }
            }

            if (languageModel is not null)
            {
                foreach (var model in services.Where(item => item.ServiceType == typeof(ILanguageModel)).ToArray())
                {
                    services.Remove(model);
                }

                services.AddSingleton(languageModel);
            }
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
