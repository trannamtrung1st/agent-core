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
    private static PreparedTenantOperation Large(Guid? report = null, Guid? target = null)
    {
        var tenant = Guid.NewGuid(); var instance = Guid.NewGuid(); var owner = TriggerOwner.ForTenant(tenant, instance, Guid.NewGuid());
        var filter = "true" + new string(' ', 1014) + "&&true"; Assert.Equal(1024, filter.Length);
        var triggers = Enumerable.Range(0, 32).Select(_ => new AutomationTriggerRecord(Guid.NewGuid(), new EventTrigger(Guid.NewGuid(), filter))).ToArray();
        var at = DateTimeOffset.UnixEpoch;
        var automation = new Automation(Guid.NewGuid(), owner, AutomationStatus.Active, new string('a', 2000), triggers, null, null, 0, 1, 1,
            new(TriggerAuthorizationOrigin.AdminOwner, null, null, at, at), null, name: new string('n', 120), completionDelivery: new AutomationCompletionDelivery(report), executionTarget: target is { } selected ? AutomationExecutionTarget.Existing(selected) : AutomationExecutionTarget.Background);
        var child = automation.Triggers[0]; var source = ((EventTrigger)child.Configuration).EventId;
        var receipt = new ExternalEvent(Guid.NewGuid(), source, "received-once", at, at, "{\"data\":{}}", tenant, 1);
        var snapshot = new EventSubscriptionSnapshot(automation.AutomationId, owner, 1, filter, new(), TriggerSourceKind.ApplicationEvent, source) { TriggerId = child.TriggerId };
        var delivery = new TenantEventDelivery(tenant, snapshot, EventMatchStatus.Matched, new(true, "matched"), null, receipt);
        var witness = TenantEventAdmissionRules.Freeze(delivery, 1);
        var model = new ExecutionModelPin("synthetic", "synthetic", "synthetic", null, ExecutionModelSource.UnattendedDefault);
        var occurrence = TenantEventAdmissionRules.Occurrence(automation, delivery, model, at, 1);
        var intent = new PreparedAutomationAdmission(tenant, automation, TenantEventAdmissionRules.Next(automation, at), occurrence,
            Guid.NewGuid(), target ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "webhooks.read", new('a', 64),
            new(model.OfferingId, model.ConnectionId, model.ExternalModelId, model.ReasoningEffort), witness, target is null ? null : new(7, 2));
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
    [Fact]
    public void Configured_report_consent_roundtrips_exact_destination_and_keeps_historical_event_bytes()
    {
        var historical = Large();
        var oldJson = JsonSerializer.Serialize(historical);
        Assert.DoesNotContain("ReportToSessionId", oldJson);
        Assert.Contains("\"SchemaVersion\":2", oldJson);
        Assert.Equal(oldJson, JsonSerializer.Serialize(JsonSerializer.Deserialize<PreparedTenantOperation>(oldJson)));
        var destination = Guid.NewGuid();
        var operation = Large(destination);
        var json = JsonSerializer.Serialize(operation);
        Assert.Contains("\"SchemaVersion\":3", json);
        var restored = JsonSerializer.Deserialize<PreparedTenantOperation>(json)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored));
        Assert.Equal(operation.ActionHash, TenantApprovalRules.Rebuild(restored).ActionHash);
        Assert.Equal(destination, restored.AutomationAdmission!.Current.CompletionDelivery.SessionId);
        Assert.Equal(destination, restored.AutomationAdmission.Next.CompletionDelivery.SessionId);
        Assert.Equal(destination, restored.AutomationAdmission.Occurrence.CompletionDelivery.SessionId);
    }

    [Theory]
    [InlineData("old-schema")] [InlineData("missing")] [InlineData("null")] [InlineData("empty")]
    [InlineData("duplicate")] [InlineData("execution-target")]
    public void Configured_report_consent_rejects_ambiguous_or_missing_destinations(string mode)
    {
        var operation = Large(Guid.NewGuid());
        var node = JsonNode.Parse(JsonSerializer.Serialize(operation))!;
        var current = node["AutomationAdmission"]!["Current"]!.AsObject();
        if (mode == "old-schema") current["SchemaVersion"] = 2;
        else if (mode == "missing") current.Remove("ReportToSessionId");
        else if (mode == "null") current["ReportToSessionId"] = null;
        else if (mode == "empty") current["ReportToSessionId"] = Guid.Empty;
        else if (mode == "duplicate") current["reportToSessionId"] = Guid.NewGuid();
        else current["ExecutionTarget"] = "existingSession";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PreparedTenantOperation>(node.ToJsonString(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));
    }

    [Fact]
    public void Existing_conversation_consent_roundtrips_its_exact_target_boundary_model_and_hash()
    {
        var target = Guid.NewGuid(); var operation = Large(target: target); var json = JsonSerializer.Serialize(operation);
        Assert.Contains("\"SchemaVersion\":4", json); Assert.DoesNotContain("ReportToSessionId", json);
        var restored = JsonSerializer.Deserialize<PreparedTenantOperation>(json)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored)); Assert.Equal(operation.ActionHash, TenantApprovalRules.Rebuild(restored).ActionHash);
        Assert.Equal(target, restored.AutomationAdmission!.Current.ExecutionTarget.SessionId);
        Assert.Equal(target, restored.AutomationAdmission.Next.ExecutionTarget.SessionId); Assert.Equal(target, restored.AutomationAdmission.SessionId);
        Assert.Equal(new PreparedAutomationConversation(7, 2), restored.AutomationAdmission.Conversation);
    }

    [Theory] [InlineData("missing-target")] [InlineData("null-target")] [InlineData("empty-target")] [InlineData("mixed-report")]
    [InlineData("old-schema")] [InlineData("duplicate-target")] [InlineData("unknown-conversation")]
    public void Existing_conversation_consent_rejects_missing_mixed_shadowed_or_unknown_witnesses(string mode)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(Large(target: Guid.NewGuid())))!;
        var current = node["AutomationAdmission"]!["Current"]!.AsObject();
        if (mode == "missing-target") current.Remove("TargetSessionId");
        else if (mode == "null-target") current["TargetSessionId"] = null;
        else if (mode == "empty-target") current["TargetSessionId"] = Guid.Empty;
        else if (mode == "mixed-report") current["ReportToSessionId"] = Guid.NewGuid();
        else if (mode == "old-schema") current["SchemaVersion"] = 2;
        else if (mode == "duplicate-target") current["targetSessionId"] = Guid.NewGuid();
        else node["AutomationAdmission"]!["Conversation"]!["UntrustedField"] = 1;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PreparedTenantOperation>(node.ToJsonString(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }));
    }
    [Theory] [InlineData("missing")] [InlineData("negative")] [InlineData("wrong-session")]
    public void Existing_conversation_preparation_requires_its_exact_snapshot_and_address(string mode)
    {
        var operation = Large(target: Guid.NewGuid()); var source = operation.AutomationAdmission!;
        source = mode switch { "missing" => source with { Conversation = null }, "negative" => source with { Conversation = new(0, -1) }, _ => source with { SessionId = Guid.NewGuid() } };
        Assert.Throws<ArgumentException>(() => TenantApprovalRules.PrepareAutomationAdmission(operation.OperationId, operation.InstanceId, source, 1, 1));
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
