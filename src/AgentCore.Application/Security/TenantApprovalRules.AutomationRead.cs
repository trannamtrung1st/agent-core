using System.Text.Json;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;
public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareAutomationRead(Guid id, Guid instance, PreparedAutomationRead value, long revision, long persona)
    {
        TenantAutomationReadRules.Validate(value);
        if (id == Guid.Empty || instance == Guid.Empty || revision < 1 || persona < 1 || TenantAutomationReadRules.Sources(value).Length == 0)
            throw new ArgumentException("Read consent requires exact nonempty Automation sources.");
        var json = JsonSerializer.Serialize(new { operation = "automations.read", read = value, expectedInstanceRevision = revision, expectedPersonaRevision = persona });
        return new(id, instance, null, revision, persona, json, TenantMemoryOperationRules.Hash(json), AutomationRead: value);
    }
}
