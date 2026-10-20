using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

internal static class TestSqliteIdentity
{
    // Explicit fixture setup before application startup. Production never bootstraps SQLite implicitly.
    internal static void Configure(IServiceCollection services, string connectionString, string keyRoot)
    {
        var options = (PersistenceOptions)services.Last(d => d.ServiceType == typeof(PersistenceOptions)).ImplementationInstance!;
        options.Provider = "Sqlite"; options.ConnectionString = connectionString; options.CredentialProtectionKeyRoot = keyRoot;
        services.TryAddSingleton<SqliteArtifactStore>(p => new(p.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(),
            p.GetRequiredService<TimeProvider>(), p.GetRequiredService<PersistenceOptions>().ArtifactRoot));
        services.Replace(ServiceDescriptor.Singleton<IArtifactStore>(p => p.GetRequiredService<SqliteArtifactStore>()));
        options.AttachmentRoot = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource + ".attachments";
        services.TryAddSingleton<SqliteAttachmentStore>(p => new(p.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(), p.GetRequiredService<TimeProvider>(), p.GetRequiredService<PersistenceOptions>().AttachmentRoot));
        services.Replace(ServiceDescriptor.Singleton<IAttachmentStore>(p => p.GetRequiredService<SqliteAttachmentStore>()));
        // Keep account-effect owners on the same database as tenant IAM/receipts.
        // A Core draft inserted by the IAM transaction must be read by this store.
        services.Replace(ServiceDescriptor.Singleton<IEmailStore, SqliteEmailStore>());
        services.TryAddSingleton<SqliteDriveStore>();
        services.Replace(ServiceDescriptor.Singleton<IDriveOperationStore>(p => p.GetRequiredService<SqliteDriveStore>()));
        services.Replace(ServiceDescriptor.Singleton<ISyntheticDriveStore>(p => p.GetRequiredService<SqliteDriveStore>()));
        services.RemoveAll<IBrowserPrivacyStore>();
        services.AddSingleton<IBrowserPrivacyStore, SqliteBrowserPrivacyStore>();
        services.RemoveAll<IIamStore>();
        services.AddSingleton<IIamStore>(provider =>
        {
            var contexts = provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>();
            using (var db = contexts.CreateDbContext()) db.Database.Migrate();
            var store = new SqliteIamStore(contexts, provider.GetService<IDefinitionResourceContentStore>(),
                provider.GetService<IBuiltInAgentDefinitionStore>(), provider.GetService<AgentCore.Application.Agents.InheritedDefinitionResourceCatalog>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<SqliteArtifactStore>(), provider.GetRequiredService<SqliteAttachmentStore>(), provider.GetRequiredService<BrowserPrivacyAuthority>());
            if (!store.HasBootstrapAsync().AsTask().GetAwaiter().GetResult())
            {
                var identity = new IdentityService(store, provider.GetRequiredService<IAccountPasswordHasher>(),
                    provider.GetRequiredService<IIdentityTokens>(), provider.GetRequiredService<IIdGenerator>(), provider.GetRequiredService<TimeProvider>());
                identity.BootstrapAsync("owner@example.test", "Synthetic test owner", "explicit-synthetic-test-password",
                    "Synthetic test organization", "synthetic-test").AsTask().GetAwaiter().GetResult();
            }
            return store;
        });
    }
}
