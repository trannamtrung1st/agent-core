using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Events;
using System.Text.Json;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantWebhookRules
{
    public static AccessScope Scope(Guid tenant, Guid source) => AccessScope.ForResource(tenant, "webhook", source.ToString("D"));
    public static bool TryId(AccessScope scope, out Guid id)
    {
        id = Guid.Empty; return scope.Kind == AccessScopeKind.Resource && scope.TenantId is { } tenant && tenant != Guid.Empty && scope.ResourceType == "webhook"
            && Guid.TryParseExact(scope.ResourceId, "D", out id) && id != Guid.Empty && scope.ResourceId == id.ToString("D");
    }
    public static async ValueTask HumanAsync(TenantSecurityState state, Guid human, Guid source, bool create, IamAuthorizer iam, CancellationToken ct)
    {
        var scope = create ? AccessScope.ForTenant(state.Tenant.TenantId) : Scope(state.Tenant.TenantId, source);
        foreach (var key in create ? new[] { "webhooks.manage" } : new[] { "webhooks.read", "webhooks.manage" })
        {
            var access = await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), scope, ct);
            if (!access.Allowed) throw new AccessDeniedException(access.ReasonCode);
        }
    }
    public static void Page(int page, int pageSize)
    { if (page is < 0 or > 100000 || pageSize is < 1 or > 100) throw new ArgumentException("Use a bounded webhook page."); }
    public static void Source(WebhookEvent? current, WebhookEvent next, Guid tenant, HumanSessionReference human, long expected)
    {
        if (next.TenantId != tenant || next.Revision != expected + 1 || (current?.Revision ?? 0) != expected
            || current is null && (next.CreatedByPrincipalId != human.PrincipalId || next.CreatedAtUtc != next.UpdatedAtUtc)
            || current is not null && (current.TenantId != tenant || current.ResourceId != next.ResourceId || current.Kind != next.Kind || current.EventKey != next.EventKey
                || current.CreatedAtUtc != next.CreatedAtUtc || current.CreatedByPrincipalId != next.CreatedByPrincipalId || next.UpdatedAtUtc < current.UpdatedAtUtc))
            throw new ArgumentException("Webhook identity, creator and event key are immutable; revisions must advance exactly once.");
    }
    public static void Receipt(Guid tenant, Guid source, ExternalEvent receipt)
    {
        if (receipt.TenantId != tenant || receipt.ResourceId != source || receipt.SourceEventId.Length is < 1 or > 64 || receipt.SourceEventId.Any(c => c is < '!' or > '~')
            || Encoding.UTF8.GetByteCount(receipt.EvidenceJson) > 4096) throw new ArgumentException("Invalid tenant webhook receipt.");
        using var json = JsonDocument.Parse(receipt.EvidenceJson); var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Webhook evidence must be a bounded object.");
        var names = root.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != 5 || names.Distinct(StringComparer.Ordinal).Count() != 5
            || names.Any(name => name is not ("sourceEventId" or "data" or "occurredAtUtc" or "rootAgentRunId" or "triggerDepth"))
            || root.GetProperty("sourceEventId").ValueKind != JsonValueKind.String || root.GetProperty("sourceEventId").GetString() != receipt.SourceEventId
            || root.GetProperty("data").ValueKind != JsonValueKind.Object || root.GetProperty("rootAgentRunId").ValueKind != JsonValueKind.Null
            || root.GetProperty("triggerDepth").ValueKind != JsonValueKind.Number || !root.GetProperty("triggerDepth").TryGetInt32(out var depth) || depth != 0)
            throw new ArgumentException("Webhook evidence cannot supply trusted causal authority.");
        DateTimeOffset? occurred = null; var sourceAt = root.GetProperty("occurredAtUtc");
        if (sourceAt.ValueKind != JsonValueKind.Null)
        {
            if (sourceAt.ValueKind != JsonValueKind.String || !sourceAt.TryGetDateTimeOffset(out var parsed) || parsed.Offset != TimeSpan.Zero
                || AgentCore.Application.Triggers.TriggerScheduleCalculator.Truncate(parsed) != receipt.OccurredAtUtc)
                throw new ArgumentException("Webhook occurrence time differs from its normalized source evidence.");
            occurred = parsed;
        }
        var input = JsonSerializer.SerializeToUtf8Bytes(new { eventId = receipt.SourceEventId, data = root.GetProperty("data"), occurredAt = occurred });
        if (!ExternalEventEnvelope.TryNormalize(input, out var verified, out _, out _, out _) || verified != receipt.EvidenceJson)
            throw new ArgumentException("Webhook evidence is not the exact bounded canonical envelope.");
    }
    public static ExternalEvent Record(ExternalEvent receipt, DateTimeOffset admittedAt) => new(receipt.EventId, receipt.ResourceId, receipt.SourceEventId,
        receipt.OccurredAtUtc, AgentCore.Application.Triggers.TriggerScheduleCalculator.Truncate(admittedAt), receipt.EvidenceJson, receipt.TenantId, receipt.SourceRevision);
    public static bool SameReceipt(ExternalEvent a, ExternalEvent b) => a.TenantId == b.TenantId && a.ResourceId == b.ResourceId && a.SourceEventId == b.SourceEventId
        && a.EvidenceJson == b.EvidenceJson; // Admission/omitted occurrence time belongs to the first receipt.
}
