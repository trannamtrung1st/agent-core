using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;
public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareManagementMutation(Guid id, Guid instance, PreparedManagementMutation value, long revision, long persona)
    {
        TenantManagementMutationRules.Validate(value);
        if (id == Guid.Empty || instance == Guid.Empty || revision < 1 || persona < 1 || TenantManagementMutationContract.Parse(value.ToolName, value.ArgumentsJson).ExpectedInstanceRevision != revision)
            throw new ArgumentException("Use an exact owned management plan.");
        var json = JsonSerializer.Serialize(new { operation = "agents.update", change = value, expectedInstanceRevision = revision, expectedPersonaRevision = persona });
        var result = new PreparedTenantOperation(id, instance, null, revision, persona, json, TenantMemoryOperationRules.Hash(json), ManagementMutation: value);
        if (System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result)) > 115000) throw new ArgumentException("The exact management plan exceeds the saved approval budget.");
        return result;
    }
}
