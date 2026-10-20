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
            || p.Permission is not ("automations.run" or "sessions.create" or "agent-runs.create" or "webhooks.read" or "core-events.read")
            || p.Current.Owner != p.Next.Owner || p.Current.AutomationId != p.Next.AutomationId || p.Current.Owner.TenantId != p.TenantId
            || p.Current.Owner.AgentInstanceId != instance || p.Current.Status != AutomationStatus.Active
            || p.Occurrence.Owner != p.Current.Owner || p.Occurrence.AutomationId != p.Current.AutomationId
            || p.Occurrence.SourceKind is not (TriggerSourceKind.Schedule or TriggerSourceKind.ManualInvocation or TriggerSourceKind.CoreEvent or TriggerSourceKind.ApplicationEvent)
            || (p.Occurrence.SourceKind == TriggerSourceKind.ManualInvocation) != (p.ManualInitiatorPrincipalId is not null)
            || p.ManualInitiatorPrincipalId == Guid.Empty || p.Occurrence.Disposition != OccurrenceRoutingDisposition.Pending || p.Occurrence.RoutingRevision != 0
            || p.Occurrence.ExecutionSessionId is not null || p.Occurrence.AcceptedAgentRunId is not null || p.Next.Revision != p.Current.Revision + 1
            || p.ConfigurationHash is not { Length: 64 } || p.ConfigurationHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Invalid exact Automation admission intent.");
        if (p.Occurrence.SourceKind is TriggerSourceKind.CoreEvent or TriggerSourceKind.ApplicationEvent)
            TenantEventAdmissionRules.Shape(p.Current, p.Occurrence, p.EventSource ?? throw new ArgumentException("An event source witness is required."));
        else if (p.EventSource is not null) throw new ArgumentException("Only event admissions may pin an event source.");
        _ = p.Model.ToPin();
        var primary = TenantAutomationAdmissionApprovalRules.Targets(p).FirstOrDefault(t => t.Permission == p.Permission);
        if (primary.Permission is null) throw new ArgumentException("The exact primary permission has no owned target.");
        var resource = primary.Scope.Kind == AccessScopeKind.Tenant ? "tenant" : primary.Scope.ResourceType;
        var target = primary.Scope.Kind == AccessScopeKind.Tenant ? p.TenantId.ToString("D") : primary.Scope.ResourceId;
        var json = JsonSerializer.Serialize(new { operation = "automations.occurrence.admit", permission = p.Permission, resourceType = resource,
            resourceId = target, expectedInstanceRevision = revision, expectedPersonaRevision = personaRevision, admission = p });
        if (Encoding.UTF8.GetByteCount(json) > (p.EventSource is null ? 65536 : 262144)) throw new ArgumentException("Automation admission exceeds its bounded consent size.");
        return new(operation, instance, null, revision, personaRevision, json, TenantWorkspaceMutationContract.Hash(json), AutomationAdmission: p);
    }
}
