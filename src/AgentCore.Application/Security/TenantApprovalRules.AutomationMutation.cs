using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;
public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareAutomationMutation(Guid id, Guid instance, PreparedAutomationMutation value, long revision, long persona)
    {
        var args = TenantAutomationMutationContract.Parse(value.ToolName, value.ArgumentsJson);
        if (id == Guid.Empty || instance == Guid.Empty || value.Proposed.Owner.AgentInstanceId != instance || revision < 1 || persona < 1
            || value.SessionId == Guid.Empty || value.RootSessionId == Guid.Empty || value.Proposed.Owner.TenantId is null || args.Json != value.ArgumentsJson
            || (value.Current?.AutomationId) != args.AutomationId || (value.Current?.Revision ?? 0) != args.ExpectedRevision || !TenantMemoryOperationRules.ValidHash(value.IntentHash))
            throw new ArgumentException("Use one exact owned Automation authoring intent.");
        var json = JsonSerializer.Serialize(new { operation = "automation.mutate", mutation = value, expectedInstanceRevision = revision, expectedPersonaRevision = persona });
        return new(id, instance, null, revision, persona, json, TenantMemoryOperationRules.Hash(json), AutomationMutation: value);
    }
}
