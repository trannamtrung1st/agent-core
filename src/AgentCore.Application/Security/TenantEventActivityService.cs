using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public sealed record TenantEventActivityItem(Guid EventId, Guid TriggerId, long TriggerRevision, string SourceKind, string SourceKey,
    DateTimeOffset ReceivedAtUtc, DateTimeOffset OccurredAtUtc, string Status, string? Code, string? Decision, bool BatchReadable,
    Guid? BucketId, int? BatchCount, Guid? OccurrenceId, Guid? RunId, Guid? SessionId, string? RunStatus, Guid? ApprovalRequestId, JsonElement Data);
public sealed record TenantEventCoverageView(IReadOnlyList<EventSourceCoverage> Items, string? NextCursor, long SecurityRevision);
public sealed record TenantEventActivityView(IReadOnlyList<TenantEventActivityItem> Items, int Total, int Page, int PageSize, long SecurityRevision);

public sealed class TenantEventActivityService(IIamStore security, ITenantEventStore events, TenantAutomationService automations,
    ITenantRunStore runs, ITenantWebhookStore webhooks, IdentityService identity, IamAuthorizer iam, ITenantApprovalStore approvals)
{
    public async ValueTask<TenantEventActivityView> ListAsync(Guid tenant, Guid instance, Guid automation, HumanSessionReference human,
        int page, int pageSize, CancellationToken ct = default)
    {
        TenantWebhookRules.Page(page, pageSize);
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var parent = await automations.ReadAsync(tenant, automation, human, ct).ConfigureAwait(false);
        if (parent.Owner.AgentInstanceId != instance) throw new AccessDeniedException("resource_unavailable");
        var visibility = await Visibility(state, human.PrincipalId, ct).ConfigureAwait(false);
        var result = await events.ListTenantEventActivityAsync(tenant, instance, automation, visibility, page, pageSize, ct).ConfigureAwait(false);
        var items = new List<TenantEventActivityItem>();
        foreach (var row in result.Items)
        {
            var d = row.Delivery; var b = row.Bucket?.Bucket; var o = row.Occurrence;
            var status = b is { ReservedOccurrenceId: not null, Flushed: false } ? "awaitingAdmission"
                : b is { Flushed: true, CompletionCode: not null } ? "policySkipped"
                : o?.Disposition == OccurrenceRoutingDisposition.AcceptedDurable ? "admitted"
                : o?.Disposition == OccurrenceRoutingDisposition.Rejected ? "policySkipped"
                : d.Status == EventMatchStatus.Coalesced ? "coalescing" : JsonNamingPolicy.CamelCase.ConvertName(d.Status.ToString());
            Guid? runId = null; Guid? sessionId = null; Guid? approvalId = null; string? runStatus = null;
            if (row.BatchReadable && o?.AcceptedAgentRunId is { } accepted && await Can("agent-runs.read", TenantRunRules.Scope(tenant, accepted)))
            {
                var run = await runs.FindTenantRunAsync(tenant, accepted, ct).ConfigureAwait(false);
                if (run is not null && run.Owner.TenantId == parent.Owner.TenantId && run.Owner.AgentInstanceId == parent.Owner.AgentInstanceId && run.Owner.ExecutorPrincipalId == parent.Owner.ExecutorPrincipalId)
                {
                    runId = accepted; runStatus = JsonNamingPolicy.CamelCase.ConvertName(run.Status.ToString());
                    if (await Can("sessions.read", TenantSessionRules.Scope(tenant, run.SessionId))
                        && await Can("agents.chat", AccessScope.ForResource(tenant, "agent", instance.ToString("D")))) sessionId = run.SessionId;
                }
            }
            if (row.BatchReadable && o is not null)
            {
                using var evidence = JsonDocument.Parse(o.EvidenceJson);
                if (evidence.RootElement.TryGetProperty("admissionApproval", out var source) && source.TryGetProperty("requestId", out var id)
                    && id.TryGetGuid(out var requestId) && await Can("approvals.read", AccessScope.ForResource(tenant, "approval", requestId.ToString("D"))))
                {
                    var request = await approvals.FindApprovalAsync(tenant, requestId, ct).ConfigureAwait(false);
                    if (request?.Operation.AutomationAdmission?.Occurrence.OccurrenceId == o.OccurrenceId)
                        try { await TenantAutomationAdmissionApprovalRules.ReadAsync(state, request, human.PrincipalId, iam, ct).ConfigureAwait(false); approvalId = requestId; }
                        catch (AccessDeniedException) { }
                }
            }
            var key = d.Core?.Event.Key ?? (await webhooks.FindTenantWebhookAsync(tenant, d.Subscription.ResourceId!.Value, ct).ConfigureAwait(false))?.EventKey ?? "Unavailable webhook";
            var data = d.Core is { } core ? JsonSerializer.Deserialize<JsonElement>(core.Event.DataJson)
                : JsonSerializer.Deserialize<JsonElement>(d.Webhook!.EvidenceJson).GetProperty("data").Clone();
            items.Add(new(d.EventId, d.Subscription.TriggerId, d.Subscription.TriggerRevision,
                d.Subscription.SourceKind == TriggerSourceKind.CoreEvent ? "builtin" : "webhook", key, d.ReceivedAt, d.OccurredAt,
                status, b?.CompletionCode ?? o?.DispositionReason ?? d.Code, d.Decision?.Status, row.BatchReadable,
                b?.BucketId, b?.Sources.Count, o?.OccurrenceId, runId, sessionId, runStatus, approvalId, data));
        }
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (current.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        return new(items, result.Total, result.Page, result.PageSize, current.Tenant.Revision);

        async ValueTask<bool> Can(string key, AccessScope scope) => (await iam.CheckOwnedTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed;
    }
    public async ValueTask<TenantEventCoverageView> CoverageAsync(Guid tenant, Guid instance, Guid automation, HumanSessionReference human,
        string? cursor, int limit = 8, CancellationToken ct = default)
    {
        if (limit is < 1 or > 24) throw new ArgumentException("Coverage limit must be between 1 and 24.");
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var parent = await automations.ReadAsync(tenant, automation, human, ct).ConfigureAwait(false);
        if (parent.Owner.AgentInstanceId != instance) throw new AccessDeniedException("resource_unavailable");
        var result = await events.ListTenantEventCoverageAsync(tenant, instance, automation, await Visibility(state, human.PrincipalId, ct).ConfigureAwait(false), cursor, limit, ct).ConfigureAwait(false);
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (current.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        return new(result.Items, result.NextCursor, current.Tenant.Revision);
    }
    private async ValueTask<TenantEventReadVisibility> Visibility(TenantSecurityState state, Guid principal, CancellationToken ct)
    {
        return new(await Ids("core-events.read", "event"), await Ids("webhooks.read", "webhook"), await TenantEmailWatchRules.VisibleAccountsAsync(state, principal, iam, ct).ConfigureAwait(false));
        async ValueTask<IReadOnlyList<Guid>?> Ids(string key, string type)
        {
            if ((await iam.CheckTenantAsync(state, principal, PermissionCatalog.Get(key), AccessScope.ForTenant(state.Tenant.TenantId), ct).ConfigureAwait(false)).Allowed) return null;
            var ids = new List<Guid>();
            foreach (var scope in state.Assignments.Where(a => a.PrincipalId == principal && a.TenantId == state.Tenant.TenantId
                && a.Scope.ResourceType == type && state.Roles.Any(r => r.RoleId == a.RoleId && r.Permissions.Contains(key))).Select(a => a.Scope).Distinct())
                if (Guid.TryParseExact(scope.ResourceId, "D", out var id) && (await iam.CheckOwnedTenantAsync(state, principal, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed) ids.Add(id);
            return ids;
        }
    }
}
