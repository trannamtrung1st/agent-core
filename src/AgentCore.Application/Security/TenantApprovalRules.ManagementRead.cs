using System.Text.Json;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;
public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareManagementRead(Guid id, Guid instance, PreparedManagementRead value, long revision, long persona)
    {
        TenantManagementReadRules.Validate(value);
        if (id == Guid.Empty || instance == Guid.Empty || revision < 1 || persona < 1 || value.InstanceRevision != revision)
            throw new ArgumentException("Read consent requires exact owned management sources.");
        var json = JsonSerializer.Serialize(new { operation = "agents.read", read = value, expectedInstanceRevision = revision, expectedPersonaRevision = persona });
        return new(id, instance, null, revision, persona, json, TenantMemoryOperationRules.Hash(json), ManagementRead: value);
    }
}
