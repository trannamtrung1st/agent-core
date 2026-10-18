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
                provider.GetRequiredService<IBuiltInAgentDefinitionStore>(), provider.GetRequiredService<AgentCore.Application.Agents.InheritedDefinitionResourceCatalog>(), provider.GetRequiredService<TimeProvider>()),
            "INMEMORY" => CreateInMemoryStore(provider),
            _ => throw new InvalidOperationException("Identity storage must explicitly select Sqlite or InMemory.")
        });
        services.TryAddSingleton<IdentityService>();
        services.TryAddSingleton<IamAuthorizer>();
        services.TryAddSingleton<TenantAccessService>();
        services.TryAddSingleton<ITenantAgentStore>(provider => (ITenantAgentStore)provider.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantAgentService>();
        services.TryAddSingleton<TenantAgentAccessService>();
        services.TryAddSingleton<TenantAgentConfigurationService>();
        services.TryAddSingleton<TenantAgentReadService>();
        services.TryAddSingleton<ITenantDefinitionStore>(provider => (ITenantDefinitionStore)provider.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantDefinitionService>();
        services.TryAddSingleton<ITenantCredentialStore>(provider => (ITenantCredentialStore)provider.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantCredentialService>();
        services.TryAddSingleton<ITenantSandboxProfileStore>(p => (ITenantSandboxProfileStore)p.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantSandboxProfileService>();
        services.TryAddSingleton<TenantSandboxImageAuthorization>();
        services.TryAddSingleton<TenantSandboxSetupResolver>();
        services.TryAddSingleton<ITenantSandboxVerificationStore>(p => (ITenantSandboxVerificationStore)p.GetRequiredService<IIamStore>());
        services.TryAddSingleton<ITenantSandboxRuntimeStore>(p => (ITenantSandboxRuntimeStore)p.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantSandboxRuntimeReadService>();
        services.TryAddSingleton<TenantBrowserStateService>();
        services.TryAddSingleton<TenantSandboxControlService>();
        services.TryAddSingleton<TenantSandboxOperationTarget>();
        services.TryAddSingleton<TenantBrowserOperationTarget>();
        services.TryAddSingleton<TenantBrowserEffectService>();
        services.TryAddSingleton<TenantSandboxControlOperationTarget>();
        services.TryAddSingleton<TenantSandboxApprovedOperationService>();
        services.TryAddSingleton<TenantSandboxExecutionAuthority>();
        services.TryAddSingleton<TenantSandboxGenerationAuthority>();
        services.TryAddSingleton<TenantSandboxVerificationService>();
        services.TryAddSingleton<TenantSandboxInstanceVerificationService>();
        services.TryAddSingleton<TenantSandboxVerificationCleanup>();
        services.AddHostedService<TenantSandboxVerificationCleanupWorker>();
        services.TryAddSingleton<ITenantCredentialBindingStore>(provider => (ITenantCredentialBindingStore)provider.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantCredentialBindingService>();
        services.TryAddSingleton<ITenantCredentialOAuthStore>(provider => (ITenantCredentialOAuthStore)provider.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantCredentialOAuthService>();
        services.TryAddSingleton<TenantCredentialOAuthCheckService>();
        services.TryAddSingleton<ITenantSessionStore>(p => (ITenantSessionStore)p.GetRequiredService<IIamStore>());
        services.TryAddSingleton<ITenantConversationStore>(p => (ITenantConversationStore)p.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantConversationService>();
        services.TryAddSingleton<TenantConversationApprovalService>();
        services.TryAddSingleton<TenantChatService>();
        services.TryAddSingleton<ITenantDeliveryReceiptStore>(p => (ITenantDeliveryReceiptStore)p.GetRequiredService<IIamStore>());
        services.TryAddSingleton<ITenantRunStore>(p => (ITenantRunStore)p.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantRunConfigurationResolver>();
        services.TryAddSingleton<TenantRunService>();
        services.TryAddSingleton<TenantRunCancellationService>();
        services.TryAddSingleton<ITenantOperationPolicyStore>(p => (ITenantOperationPolicyStore)p.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantOperationPolicyService>();
        services.TryAddSingleton<TenantOperationAuthorizer>();
        services.TryAddSingleton<ITenantAuthorizationAuditStore>(p => (ITenantAuthorizationAuditStore)p.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantOperationAudit>();
        services.TryAddSingleton<TenantToolAuthorization>();
        services.TryAddSingleton<ITenantApprovalStore>(p => (ITenantApprovalStore)p.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantApprovalService>();
        services.TryAddSingleton<TenantCredentialOperationTarget>();
        services.TryAddSingleton<ITenantCredentialCheckStore>(p => (ITenantCredentialCheckStore)p.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantCredentialCheckService>();
        services.TryAddSingleton<ITenantApprovedOperationStore>(p => (ITenantApprovedOperationStore)p.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantApprovedOperationService>();
        services.TryAddSingleton<ITenantRuntimeStore>(p => (ITenantRuntimeStore)p.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantRunExecutionAuthority>();
        services.TryAddSingleton<TenantRunContinuationService>();
        services.TryAddSingleton<ITenantPreferenceOperationStore>(p => (ITenantPreferenceOperationStore)p.GetRequiredService<IIamStore>());
        services.TryAddSingleton<TenantPreferenceOperationService>();
        services.TryAddSingleton<TenantSessionService>();
        services.AddHostedService<CredentialOAuthExpiryWorker>();
        services.TryAddSingleton<AccountAdministrationService>();
        return services;
    }
    private static InMemoryIamStore CreateInMemoryStore(IServiceProvider provider)
    {
        var definitions = (AgentCore.Infrastructure.Definitions.InMemoryAgentDefinitionAdminStore)provider.GetRequiredService<IAgentDefinitionAdminStore>();
        _ = provider.GetRequiredService<IDefinitionDraftEvaluationStore>();
        return new(provider.GetRequiredService<InMemoryAgentInstanceStore>(), definitions, provider.GetRequiredService<InMemoryMemoryStore>(),
            provider.GetRequiredService<IBuiltInAgentDefinitionStore>(), provider.GetRequiredService<AgentCore.Application.Agents.InheritedDefinitionResourceCatalog>(),
            provider.GetRequiredService<InMemoryCredentialStore>(), provider.GetRequiredService<TimeProvider>(),
            (InMemorySandboxStore)provider.GetRequiredService<ISandboxStore>());
    }
}
