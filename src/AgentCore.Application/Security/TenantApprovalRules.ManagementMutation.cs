using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;
public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareManagementMutation(Guid id, Guid instance, PreparedManagementMutation value, long revision, long persona)
    {
        TenantManagementMutationRules.Validate(value);
        if (id == Guid.Empty || instance == Guid.Empty || revision < 1 || persona < 1 || TenantManagementMutationContract.Parse(value.ToolName, value.ArgumentsJson, value.Harness, remedy: value.Remedy).ExpectedInstanceRevision != revision)
            throw new ArgumentException("Use an exact owned management plan.");
        var json = JsonSerializer.Serialize(new { operation = value.Binding is not null ? "credentials.bind" : value.Resource is not null ? TenantManagementResourceRules.Permission(value.ToolName) : value.Skill is null ? "agents.update" : "skills.manage", change = value, expectedInstanceRevision = revision, expectedPersonaRevision = persona });
        var result = new PreparedTenantOperation(id, instance, null, revision, persona, json, TenantMemoryOperationRules.Hash(json), ManagementMutation: value);
        if (System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result)) > (value.Resource is null && value.Harness is null ? 115000 : 1040000)) throw new ArgumentException("The exact management plan exceeds the saved approval budget.");
        return result;
    }
}
