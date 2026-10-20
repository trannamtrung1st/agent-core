using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Domain.Events;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public sealed record TenantAutomationAdmissionState(TenantSecurityState State, TenantAgent Agent, ResolvedAgentRunConfiguration Configuration,
    Automation Automation, TriggerOccurrence Occurrence);

public sealed class TenantAutomationAdmissionTarget(IIamStore security, ITenantAutomationStore store, TenantRunConfigurationResolver configuration,
    IamAuthorizer iam, IModelCatalog models, ITenantEventStore? events = null, ITenantWebhookStore? webhooks = null)
{
    public async ValueTask<TenantAutomationAdmissionState> RequireAsync(TenantApprovalRequest request, CancellationToken ct)
    {
        var p = request.Operation.AutomationAdmission ?? throw new ArgumentException("Automation admission source required.");
        var state = await security.ReadTenantAsync(request.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var source = await store.FindTenantAutomationAsync(request.TenantId, p.Current.AutomationId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("automation_unavailable");
        var occurrence = await store.FindTenantOccurrenceAsync(request.TenantId, p.Occurrence.OccurrenceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("occurrence_unavailable");
        if (!TenantAutomationRules.Same(source, p.Next) || !TenantAutomationAdmissionApprovalRules.SameOccurrence(occurrence, TenantAutomationAdmissionApprovalRules.Pending(p)))
            throw new SecurityConflictException("operation_witness_changed");
        var current = await configuration.ResolveAsync(request.TenantId, request.Operation.InstanceId, ct).ConfigureAwait(false);
        TenantAutomationAdmissionApprovalRules.Shape(request, current.Agent);
        try { TenantAutomationRules.AdmissionSource(state, p.Current, current.Agent, current.Configuration.Configuration.Definition, p.ManualInitiatorPrincipalId, p.Occurrence.SourceKind); }
        catch (ArgumentException) { throw new AccessDeniedException("operation_configuration_changed"); }
        if (TenantAutomationRules.Hash(current.Configuration) != p.ConfigurationHash) throw new SecurityConflictException("operation_witness_changed");
        var model = ExecutionModelPolicy.Resolve(models, current.Configuration.Configuration.Definition, current.Agent.Instance, p.Current);
        if (!model.Accepted || model.Pin is null || model.Pin.CatalogKey != p.Model.CatalogKey || model.Pin.ProviderAlias != p.Model.ProviderAlias
            || model.Pin.ModelId != p.Model.ModelId || model.Pin.ReasoningEffort != p.Model.ReasoningEffort) throw new AccessDeniedException("automation_model_unavailable");
        await TenantAutomationAdmissionApprovalRules.ExecutorAsync(state, request, iam, ct).ConfigureAwait(false);
        if (p.EventSource is { } eventSource) await RequireEventAsync(p, eventSource, ct).ConfigureAwait(false);
        return new(state, current.Agent, current.Configuration, source, occurrence);
    }
    private async ValueTask RequireEventAsync(PreparedAutomationAdmission p, PreparedEventAdmissionSource source, CancellationToken ct)
    {
        if (events is null) throw new AccessDeniedException("event_source_unavailable");
        if (source.SourceKind == TriggerSourceKind.ApplicationEvent)
        {
            var resource = webhooks is null ? null : await webhooks.FindTenantWebhookAsync(p.TenantId, source.ResourceId!.Value, ct).ConfigureAwait(false);
            if (resource is not { Status: WebhookEventStatus.Active } || resource.Revision != source.ResourceRevision)
                throw new SecurityConflictException("event_source_changed");
        }
        PreparedEventAdmissionSource frozen; TriggerOccurrence occurrence;
        if (!source.IsBucket)
        {
            var delivery = await events.FindTenantEventDeliveryAsync(p.TenantId, source.SourceKind, source.SourceId, source.TriggerId, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("event_source_unavailable");
            if (delivery.Status != EventMatchStatus.AwaitingAdmission || delivery.ReservedOccurrenceId != p.Occurrence.OccurrenceId)
                throw new SecurityConflictException("event_source_changed");
            var matched = delivery with { Status = EventMatchStatus.Matched, ReservedOccurrenceId = null };
            frozen = TenantEventAdmissionRules.Freeze(matched, source.ResourceRevision);
            occurrence = TenantEventAdmissionRules.Occurrence(p.Current, matched, p.Occurrence.ModelPin!, p.Occurrence.AdmittedAtUtc, source.ResourceRevision);
        }
        else
        {
            var bucket = await events.FindTenantEventBucketAsync(p.TenantId, source.SourceId, ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("event_source_unavailable");
            if (bucket.Bucket.Flushed || bucket.Bucket.ReservedOccurrenceId != p.Occurrence.OccurrenceId)
                throw new SecurityConflictException("event_source_changed");
            frozen = TenantEventAdmissionRules.Freeze(bucket, source.ResourceRevision);
            occurrence = TenantEventAdmissionRules.Occurrence(p.Current, bucket, p.Occurrence.ModelPin!, p.Occurrence.AdmittedAtUtc, source.ResourceRevision);
        }
        if (JsonSerializer.Serialize(source) != JsonSerializer.Serialize(frozen) || !TenantAutomationAdmissionApprovalRules.SameOccurrence(occurrence, p.Occurrence))
            throw new SecurityConflictException("event_source_changed");
    }

}
