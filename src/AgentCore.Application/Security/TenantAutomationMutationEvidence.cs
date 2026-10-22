using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;
public static class TenantAutomationMutationEvidence
{
    public static bool Valid(TenantAutomationMutationReceipt value) => value.AutomationId != Guid.Empty && TenantAutomationMutationContract.IsTool(value.ToolName)
        && value.ExpectedRevision is >= 0 and < long.MaxValue && value.Revision > 0 && value.Revision == value.ExpectedRevision + 1 && TenantMemoryOperationRules.ValidHash(value.AfterHash)
        && (value.ExpectedRevision == 0 ? value.ToolName == ToolCatalog.AutomationCreate && value.BeforeHash is null : value.ToolName != ToolCatalog.AutomationCreate && value.BeforeHash is not null && TenantMemoryOperationRules.ValidHash(value.BeforeHash));
    // Exact completed receipt chains explain this task's own revision changes;
    // they never waive current raw reads, Review, event witnesses or external edits.
    public static bool Explains(TenantMemoryReader reader, TenantAutomationReadSource source, Automation current, IEnumerable<TenantOperationReceipt> receipts)
    {
        var rows = receipts.Where(r => r.TenantId == reader.Security.Tenant.TenantId && r.InstanceId == reader.Run.AgentInstanceId && r.ExecutorPrincipalId == reader.Agent.PrincipalId
            && r.SessionId == reader.Run.SessionId && r.RootActivationId == reader.Run.Admission.Activation.RootActivationId && r.Outcome == TenantOperationOutcome.Completed
            && r.ReasonCode == TenantAutomationMutationRules.Completed && r.AutomationMutation?.AutomationId == source.AutomationId).Select(r => r.AutomationMutation!).ToArray();
        if (rows.Length > 1024 || rows.Any(r => !Valid(r))) return false;
        var revision = source.Revision; var hash = source.ConfigurationHash;
        for (var index = 0; index < rows.Length && revision < current.Revision; index++)
        {
            var matches = rows.Where(r => r.ExpectedRevision == revision && r.BeforeHash == hash).ToArray();
            if (matches.Length != 1) return false;
            revision = matches[0].Revision; hash = matches[0].AfterHash;
        }
        return revision == current.Revision && hash == TenantAutomationReadRules.ConfigurationHash(current);
    }
}
