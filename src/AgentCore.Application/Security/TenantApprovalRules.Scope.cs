using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static AccessScope Scope(Guid tenant, PreparedTenantOperation operation) => (operation.ManagementMutation?.Resource is { CreateScope: true }
        || operation.ManagementMutation?.Skill is { CreateScope: true } || operation.AutomationMutation is { Current: null }
        || operation.AutomationAdmission is { Permission: "sessions.create" or "agent-runs.create" } || operation.BackgroundReport is not null)
        ? AccessScope.ForTenant(tenant) : AccessScope.ForResource(tenant, operation.ResourceType, operation.ResourceId);
    public static string ConfigurationHash(ResolvedAgentRunConfiguration value) => Tools.TenantWorkspaceMutationContract.Hash(JsonSerializer.Serialize(value));
}
