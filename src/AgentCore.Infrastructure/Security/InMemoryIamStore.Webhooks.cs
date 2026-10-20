using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantWebhookStore
{
    public ValueTask<WebhookEvent?> FindTenantWebhookAsync(Guid tenant, Guid source, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) lock (webhookBacking.TenantGate) return ValueTask.FromResult(webhookBacking.TenantSource(tenant, source)); }
    public ValueTask<TenantWebhookPage> ListTenantWebhooksAsync(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); TenantWebhookRules.Page(page, pageSize); lock (_gate) lock (webhookBacking.TenantGate)
        {
            var all = webhookBacking.TenantSources(tenant).Where(s => allowed is null || allowed.Contains(s.ResourceId)).OrderBy(s => s.ResourceId.ToString("D"), StringComparer.Ordinal).ToArray();
            return ValueTask.FromResult(new TenantWebhookPage(all.Skip(page * pageSize).Take(pageSize).ToArray(), all.Length, page, pageSize));
        }
    }
    public ValueTask<TenantWebhookReceiptPage> ListTenantWebhookReceiptsAsync(Guid tenant, Guid source, int page, int pageSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); TenantWebhookRules.Page(page, pageSize); lock (_gate) lock (webhookBacking.TenantGate)
        {
            var all = webhookBacking.TenantSource(tenant, source) is null ? [] : webhookBacking.TenantReceipts(tenant, source).OrderByDescending(r => r.AdmittedAtUtc).ThenBy(r => r.EventId.ToString("D"), StringComparer.Ordinal).ToArray();
            return ValueTask.FromResult(new TenantWebhookReceiptPage(all.Skip(page * pageSize).Take(pageSize).ToArray(), all.Length, page, pageSize));
        }
    }
    public ValueTask<bool> TrySaveTenantWebhookAsync(Guid tenant, long expectedSecurityRevision, WebhookEvent source,
        long expectedSourceRevision, HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) lock (webhookBacking.TenantGate)
        {
            if (!_tenants.TryGetValue(tenant, out var state) || state.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult(false);
            if (audit.TenantId != state.Tenant.TenantId || audit.ActorPrincipalId != human.PrincipalId
                || audit.Action != "webhooks.manage" || audit.Target != source.ResourceId.ToString("D") || audit.Decision != "allowed" || audit.At != source.UpdatedAtUtc)
                throw new ArgumentException("Invalid webhook management transaction.");
            RequireHumanSession(state.Tenant.TenantId, human); var current = webhookBacking.TenantSource(state.Tenant.TenantId, source.ResourceId);
            if ((current?.Revision ?? 0) != expectedSourceRevision) return ValueTask.FromResult(false);
            TenantWebhookRules.Source(current, source, state.Tenant.TenantId, human, expectedSourceRevision);
            TenantWebhookRules.HumanAsync(state, human.PrincipalId, source.ResourceId, current is null, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            if (current is null && webhookBacking.SourceIdExists(source.ResourceId) || webhookBacking.TenantKeyExists(state.Tenant.TenantId, source.EventKey, source.ResourceId))
                throw new SecurityConflictException("webhook_key_conflict");
            ct.ThrowIfCancellationRequested(); webhookBacking.PutTenantSource(source); _tenants[state.Tenant.TenantId] = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } }; _audit.Add(audit); return ValueTask.FromResult(true);
        }
    }
    public ValueTask<TenantWebhookAdmission> TryAdmitTenantWebhookAsync(Guid tenant, Guid source, string token, ExternalEvent receipt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); TenantWebhookRules.Receipt(tenant, source, receipt);
        lock (_gate) lock (automationBacking.AdmissionGate) lock (webhookBacking.TenantGate)
        {
            var current = webhookBacking.TenantSource(tenant, source);
            if (_tenants.GetValueOrDefault(tenant)?.Tenant.Status != IdentityStatus.Active || current?.Status != WebhookEventStatus.Active
                || current.Revision != receipt.SourceRevision || !WebhookTokens.Matches(current.CredentialHash, token))
                return ValueTask.FromResult(new TenantWebhookAdmission(TenantWebhookAdmissionKind.Unauthorized));
            if (TenantWebhookRules.ContainsCredential(receipt.EvidenceJson, token)) throw new ArgumentException("Source credentials cannot enter event evidence.");
            var prior = webhookBacking.TenantReceipt(tenant, source, receipt.SourceEventId);
            if (prior is not null) return ValueTask.FromResult(new TenantWebhookAdmission(TenantWebhookRules.SameReceipt(prior, receipt) ? TenantWebhookAdmissionKind.Duplicate : TenantWebhookAdmissionKind.Conflict,
                TenantWebhookRules.SameReceipt(prior, receipt) ? prior.EventId : null));
            if (webhookBacking.EventIdExists(receipt.EventId)) throw new SecurityConflictException("identity_conflict");
            var targets = automationBacking.TenantAutomations(tenant).Where(a => a.Status == AgentCore.Domain.Triggers.AutomationStatus.Active
                && (a.ExpiresAtUtc is null || a.ExpiresAtUtc > ApprovalNow)).SelectMany(a => a.Triggers
                    .Where(t => t.Enabled && t.Configuration is AgentCore.Domain.Triggers.EventTrigger e && e.EventId == source)
                    .Select(t => new AgentCore.Domain.Events.EventSubscriptionSnapshot(a.AutomationId, a.Owner, t.Revision,
                        ((AgentCore.Domain.Triggers.EventTrigger)t.Configuration).FilterExpression, ((AgentCore.Domain.Triggers.EventTrigger)t.Configuration).Dispatch,
                        AgentCore.Domain.Triggers.TriggerSourceKind.ApplicationEvent, source) { TriggerId = t.TriggerId })).Take(AgentCore.Domain.Triggers.TriggerLimits.MaxWebhookFanoutTargets + 1).ToArray();
            if (targets.Length > AgentCore.Domain.Triggers.TriggerLimits.MaxWebhookFanoutTargets) throw new SecurityConflictException("webhook_fanout_limit");
            if (targets.Select(t => t.TriggerId).Distinct().Count() != targets.Length) throw new SecurityConflictException("event_subscription_changed");
            ct.ThrowIfCancellationRequested(); webhookBacking.PutTenantReceipt(TenantWebhookRules.Record(receipt, ApprovalNow));
            foreach (var target in targets) webhookBacking.PutTenantDelivery(new(receipt.EventId, target.AutomationId, target.Owner.AgentInstanceId, null, ExternalEventDeliveryStatus.Pending, target));
            return ValueTask.FromResult(new TenantWebhookAdmission(TenantWebhookAdmissionKind.Accepted, receipt.EventId));
        }
    }
}
