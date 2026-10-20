using System.Text;
using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareAutomationAdmission(Guid operation, Guid instance, PreparedAutomationAdmission p, long revision, long personaRevision)
    {
        var ids = new[] { operation, instance, p.TenantId, p.Current.AutomationId, p.RequestId, p.SessionId, p.EntryId, p.ActivationId, p.RunId, p.ResponseId };
        if (ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length || revision < 1 || personaRevision < 1
            || p.Permission is not ("automations.run" or "sessions.create" or "agent-runs.create")
            || p.Current.Owner != p.Next.Owner || p.Current.AutomationId != p.Next.AutomationId || p.Current.Owner.TenantId != p.TenantId
            || p.Current.Owner.AgentInstanceId != instance || p.Current.Status != AutomationStatus.Active
            || p.Occurrence.Owner != p.Current.Owner || p.Occurrence.AutomationId != p.Current.AutomationId
            || p.Occurrence.SourceKind is not (TriggerSourceKind.Schedule or TriggerSourceKind.ManualInvocation)
            || (p.Occurrence.SourceKind == TriggerSourceKind.ManualInvocation) != (p.ManualInitiatorPrincipalId is not null)
            || p.ManualInitiatorPrincipalId == Guid.Empty || p.Occurrence.Disposition != OccurrenceRoutingDisposition.Pending || p.Occurrence.RoutingRevision != 0
            || p.Occurrence.ExecutionSessionId is not null || p.Occurrence.AcceptedAgentRunId is not null || p.Next.Revision != p.Current.Revision + 1
            || p.ConfigurationHash is not { Length: 64 } || p.ConfigurationHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Invalid exact Automation admission intent.");
        _ = p.Model.ToPin();
        var resource = p.Permission == "automations.run" ? "automation" : "tenant";
        var target = p.Permission == "automations.run" ? p.Current.AutomationId : p.TenantId;
        var json = JsonSerializer.Serialize(new { operation = "automations.occurrence.admit", permission = p.Permission, resourceType = resource,
            resourceId = target.ToString("D"), expectedInstanceRevision = revision, expectedPersonaRevision = personaRevision, admission = p });
        if (Encoding.UTF8.GetByteCount(json) > 65536) throw new ArgumentException("Automation admission exceeds its bounded consent size.");
        return new(operation, instance, null, revision, personaRevision, json, TenantWorkspaceMutationContract.Hash(json), AutomationAdmission: p);
    }
}
