using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Tests;

public sealed class TenantEventAdmissionContractTests
{
    private static PreparedTenantOperation Large()
    {
        var tenant = Guid.NewGuid(); var instance = Guid.NewGuid(); var owner = TriggerOwner.ForTenant(tenant, instance, Guid.NewGuid());
        var filter = "true" + new string(' ', 1014) + "&&true"; Assert.Equal(1024, filter.Length);
        var triggers = Enumerable.Range(0, 32).Select(_ => new AutomationTriggerRecord(Guid.NewGuid(), new EventTrigger(Guid.NewGuid(), filter))).ToArray();
        var at = DateTimeOffset.UnixEpoch;
        var automation = new Automation(Guid.NewGuid(), owner, AutomationStatus.Active, new string('a', 2000), triggers, null, null, 0, 1, 1,
            new(TriggerAuthorizationOrigin.AdminOwner, null, null, at, at), null, name: new string('n', 120));
        var child = automation.Triggers[0]; var source = ((EventTrigger)child.Configuration).EventId;
        var receipt = new ExternalEvent(Guid.NewGuid(), source, "received-once", at, at, "{\"data\":{}}", tenant, 1);
        var snapshot = new EventSubscriptionSnapshot(automation.AutomationId, owner, 1, filter, new(), TriggerSourceKind.ApplicationEvent, source) { TriggerId = child.TriggerId };
        var delivery = new TenantEventDelivery(tenant, snapshot, EventMatchStatus.Matched, new(true, "matched"), null, receipt);
        var witness = TenantEventAdmissionRules.Freeze(delivery, 1);
        var model = new ExecutionModelPin("synthetic", "synthetic", "synthetic", null, ExecutionModelSource.UnattendedDefault);
        var occurrence = TenantEventAdmissionRules.Occurrence(automation, delivery, model, at, 1);
        var intent = new PreparedAutomationAdmission(tenant, automation, TenantEventAdmissionRules.Next(automation, at), occurrence,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "webhooks.read", new('a', 64),
            new(model.CatalogKey, model.ProviderAlias, model.ModelId, model.ReasoningEffort), witness);
        return TenantApprovalRules.PrepareAutomationAdmission(Guid.NewGuid(), instance, intent, 1, 1);
    }
    [Fact]
    public void Maximum_child_and_predicate_shapes_roundtrip_exact_event_consent_above_the_old_schedule_bound()
    {
        var operation = Large(); var size = Encoding.UTF8.GetByteCount(operation.CanonicalActionJson);
        Assert.InRange(size, 65537, 262144); Assert.Equal("webhook", operation.ResourceType);
        var restored = JsonSerializer.Deserialize<PreparedTenantOperation>(JsonSerializer.Serialize(operation))!;
        Assert.Equal(operation, restored); Assert.Equal(operation.ActionHash, TenantApprovalRules.Rebuild(restored).ActionHash);
        Assert.Equal(32, restored.AutomationAdmission!.Current.Triggers.Count); Assert.Equal(32, restored.AutomationAdmission.Next.Triggers.Count);
        var a = restored.AutomationAdmission;
        var request = new TenantApprovalRequest(a.RequestId, a.TenantId, a.RunId, a.SessionId, a.ActivationId, a.Current.Owner.ExecutorPrincipalId!.Value,
            null, ApprovalKind.Review, operation, TenantApprovalStatus.Pending, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1));
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(request).Length, 1, 1048576);
        Assert.Equal("webhooks.read", request.Permission); Assert.Equal(a.EventSource!.ResourceId!.Value.ToString("D"), request.ResourceId);
    }
    [Theory]
    [InlineData("unknown-child")] [InlineData("unknown-parent")] [InlineData("shadow-child")] [InlineData("shadow-owner")]
    public void Event_consent_rejects_unknown_and_duplicate_source_fields(string mode)
    {
        var operation = Large(); var node = JsonNode.Parse(JsonSerializer.Serialize(operation))!;
        var current = node["AutomationAdmission"]!["Current"]!;
        if (mode == "unknown-parent") current["UnknownSource"] = "untrusted";
        else if (mode == "unknown-child") current["Triggers"]![0]!["UnknownSource"] = "untrusted";
        else if (mode == "shadow-child") current["Triggers"]![0]!["revision"] = 999;
        else current["Owner"]!["tenantId"] = Guid.NewGuid();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PreparedTenantOperation>(node.ToJsonString(), options));
    }
}
