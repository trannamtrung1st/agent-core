using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using SpaceEnvironment = AgentCore.Domain.Spaces.Environment;

namespace AgentCore.Infrastructure.Security;

internal static class EnvironmentStoreRules
{
    internal const int MaxProfiles = 64;
    internal const int MaxVariables = 256;
    internal static void Secret(TenantSecurityState next, Secret secret, long expected, AuthorizationAudit audit)
    {
        secret.Validate();
        if (secret.TenantId != next.Tenant.TenantId || secret.Status == SecretStatus.Deleted || secret.Revision != checked(expected + 1)
            || expected < 0 || audit.TenantId != secret.TenantId) throw new ArgumentException("Invalid Secret transaction.");
    }
    internal static void Environment(TenantSecurityState next, SpaceEnvironment environment, long expected, AuthorizationAudit audit)
    {
        environment.Validate();
        if (environment.TenantId != next.Tenant.TenantId || environment.DeletedAt is not null || environment.Revision != checked(expected + 1)
            || expected < 0 || audit.TenantId != environment.TenantId) throw new ArgumentException("Invalid Environment transaction.");
    }
    internal static void Variable(TenantSecurityState next, EnvironmentVariable variable, long expected, AuthorizationAudit audit)
    {
        variable.Validate();
        if (variable.TenantId != next.Tenant.TenantId || variable.Revision != checked(expected + 1) || expected < 0
            || audit.TenantId != variable.TenantId) throw new ArgumentException("Invalid variable transaction.");
    }
}
