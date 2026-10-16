using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Infrastructure.Security;

public static class IdentityRegistration
{
    public static IServiceCollection AddAgentCoreIdentity(this IServiceCollection services)
    {
        services.AddDataProtection().SetApplicationName("AgentCore.AccountSessions.v1");
        // Resolve the configured (and test-overridden) persistence root at service creation time.
        // Account cookies never fall back to a machine/user-global ASP.NET key ring.
        services.AddOptions<KeyManagementOptions>().Configure<PersistenceOptions, ILoggerFactory>((options, persistence, logs) =>
        {
            var path = Path.GetFullPath(Path.Combine(persistence.CredentialProtectionKeyRoot, "account-sessions"));
            var root = OperatingSystem.IsWindows() ? Directory.CreateDirectory(path)
                : Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.XmlRepository = new FileSystemXmlRepository(root, logs);
        });
        services.TryAddSingleton<IAccountPasswordHasher, AccountPasswordHasher>();
        services.TryAddSingleton<IIdentityTokens, IdentityTokens>();
        services.TryAddSingleton<IIamStore>(provider => provider.GetRequiredService<PersistenceOptions>().Provider.ToUpperInvariant() switch
        {
            "SQLITE" => new SqliteIamStore(provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>(), provider.GetService<IDefinitionResourceContentStore>(),
                provider.GetRequiredService<IBuiltInAgentDefinitionStore>(), provider.GetRequiredService<AgentCore.Application.Agents.InheritedDefinitionResourceCatalog>()),
            "INMEMORY" => CreateInMemoryStore(provider),
            _ => throw new InvalidOperationException("Identity storage must explicitly select Sqlite or InMemory.")
        });
        services.TryAddSingleton<IdentityService>();
        services.TryAddSingleton<IamAuthorizer>();
        services.TryAddSingleton<TenantAccessService>();
        services.TryAddSingleton<ITenantAgentStore>(provider => (ITenantAgentStore)provider.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantAgentService>();
        services.TryAddSingleton<TenantAgentConfigurationService>();
        services.TryAddSingleton<ITenantDefinitionStore>(provider => (ITenantDefinitionStore)provider.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantDefinitionService>();
        services.TryAddSingleton<AccountAdministrationService>();
        return services;
    }
    private static InMemoryIamStore CreateInMemoryStore(IServiceProvider provider)
    {
        var definitions = (AgentCore.Infrastructure.Definitions.InMemoryAgentDefinitionAdminStore)provider.GetRequiredService<IAgentDefinitionAdminStore>();
        _ = provider.GetRequiredService<IDefinitionDraftEvaluationStore>();
        return new(provider.GetRequiredService<InMemoryAgentInstanceStore>(), definitions, provider.GetRequiredService<InMemoryMemoryStore>(),
            provider.GetRequiredService<IBuiltInAgentDefinitionStore>(), provider.GetRequiredService<AgentCore.Application.Agents.InheritedDefinitionResourceCatalog>());
    }
}
