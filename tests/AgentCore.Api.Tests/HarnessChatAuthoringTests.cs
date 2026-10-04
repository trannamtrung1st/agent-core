using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class HarnessChatAuthoringTests
{
    [Fact]
    public async Task Managed_chat_publishes_knowledge_for_future_sessions_without_source_configuration()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var admin = services.GetRequiredService<AdminAgentInstanceService>();
        var authoring = services.GetRequiredService<HarnessManagementService>();
        var instance = await admin.CreateManagedAsync("general-assistant", 7);
        instance = await authoring.ConfigureAsync(instance.InstanceId, instance.Revision, new(HarnessManagementMode.Managed, [HarnessManagementScope.KnowledgeResources], [], []));
        var pinned = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 7))!;
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var context = await executor.HarnessContextAsync(instance.InstanceId, default);
        var offered = ToolCatalog.For(pinned, new(pinned, [], "", null, AgentCore.Domain.Conversation.SessionMode.Text, null, false, null,
            new(Guid.NewGuid(), TriggerKind.UserTurn, "Learn this for future conversations"), Harness: context), executorGate(services));
        Assert.Contains(offered, t => t.Name == "harness.knowledge.upsert");
        Assert.DoesNotContain(offered, t => t.Name == "harness.skill.upsert");
        var args = JsonSerializer.Serialize(new { expectedVersion = 7, policyRevision = context!.PolicyRevision,
            id = "owner-policy", content = "Orders need payment, shipping and fraud review.", source = "conversation:user",
            expected = "Reusable order policy can be retained.", observed = "Owner supplied enduring role knowledge." });
        var result = await executor.ExecuteAsync(pinned, Guid.NewGuid(), new("learn", "harness.knowledge.upsert", args), 100000,
            admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId, OwnerTurnText: "Orders need payment, shipping and fraud review."));
        Assert.Contains("\"saved\":true", result.Text);
        var updated = (await services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!;
        Assert.True(updated.ActiveVersion > 7);
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(async () => await authoring.CancelAsync(instance.InstanceId, updated.Revision));
        Assert.Equal(HarnessPreparationStatus.Published, (await authoring.ReviewAsync(instance.InstanceId)).State.Preparation!.Status);
        Assert.Equal(7, pinned.Version);
        var future = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync(updated.DefinitionId, updated.ActiveVersion))!;
        Assert.Contains(future.Environment!.KnowledgeList, k => k.Identity == "owner-policy");
        Assert.Equal(HarnessPreparationStatus.Published, updated.HarnessManagement!.Preparation!.Status);
        Assert.Contains(updated.HarnessManagement.Preparation.Evidence, e => e.Actor == "Core" && e.Check == "Candidate knowledge readback" && e.Status == HarnessEvidenceStatus.Verified);
    }
    [Theory]
    [InlineData(HarnessManagementMode.Assisted, "harness.knowledge.upsert", HarnessManagementScope.KnowledgeResources)]
    [InlineData(HarnessManagementMode.Managed, "harness.instructions.update", HarnessManagementScope.Instructions)]
    [InlineData(HarnessManagementMode.Managed, "harness.tool.select", HarnessManagementScope.ToolSelection)]
    [InlineData(HarnessManagementMode.Managed, "harness.tool.configure", HarnessManagementScope.ToolSelection)]
    public async Task Approval_is_exact_and_authority_is_rechecked_after_the_wait(HarnessManagementMode mode, string name, HarnessManagementScope scope)
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var service = services.GetRequiredService<HarnessManagementService>();
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 7);
        instance = await service.ConfigureAsync(instance.InstanceId, instance.Revision, new(mode, [scope], [], []));
        var pinned = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 7))!;
        var tools = services.GetRequiredService<SessionToolExecutor>();
        var context = await tools.HarnessContextAsync(instance.InstanceId, default);
        var args = JsonSerializer.Serialize(new { expectedVersion = 7, policyRevision = context!.PolicyRevision,
            id = name == "harness.tool.configure" ? "attachments.read" : name == "harness.tool.select" ? "web.fetch" : "policy",
            enabled = false, allowUnreadUnsupportedTypes = true, content = "Reusable owner policy.", source = "conversation:user", expected = "Retain policy", observed = "Owner supplied it." });
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId, OwnerTurnText: "Retain this policy.");
        var call = new ModelToolCall("change", name, args);
        Assert.Contains("approval_required", (await tools.ExecuteAsync(pinned, Guid.NewGuid(), call, 100000, admission: admission)).Text);
        Assert.Equal(7, (await service.ReviewAsync(instance.InstanceId)).ActiveVersion);
        using var json = JsonDocument.Parse(args);
        var grant = new ToolApprovalGrant(Guid.NewGuid(), name, ToolActionHash.Compute(name, json.RootElement), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Contains("stale_approval", (await tools.ExecuteAsync(pinned, Guid.NewGuid(), call, 100000, approvalGrant: grant with { ActionHash = "altered" }, admission: admission)).Text);
        instance = await service.ConfigureAsync(instance.InstanceId, instance.Revision, instance.HarnessManagement!.Policy);
        var stale = await tools.ExecuteAsync(pinned, Guid.NewGuid(), call, 100000, approvalGrant: grant, admission: admission);
        Assert.Contains("changed", stale.Text);
        Assert.Equal(7, (await service.ReviewAsync(instance.InstanceId)).ActiveVersion);
        var freshArgs = args.Replace($"\"policyRevision\":{context.PolicyRevision}", $"\"policyRevision\":{instance.HarnessManagement!.PolicyRevision}");
        using var freshJson = JsonDocument.Parse(freshArgs);
        var result = await tools.ExecuteAsync(pinned, Guid.NewGuid(), call with { ArgumentsJson = freshArgs }, 100000,
            approvalGrant: grant with { ActionHash = ToolActionHash.Compute(name, freshJson.RootElement), ApprovalId = Guid.NewGuid() }, admission: admission);
        Assert.Contains("\"saved\":true", result.Text);
        Assert.Equal(ToolPolicyDecision.RequireApproval, ToolPolicy.EvaluateExecution(pinned, "http.request", executorGate(services)));
    }

    [Fact]
    public async Task Disabled_frozen_background_absent_scope_and_unread_source_cannot_author()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var service = services.GetRequiredService<HarnessManagementService>();
        var tools = services.GetRequiredService<SessionToolExecutor>();
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 7);
        var pinned = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 7))!;
        Assert.Null(await tools.HarnessContextAsync(instance.InstanceId, default));
        instance = await service.ConfigureAsync(instance.InstanceId, instance.Revision, new(HarnessManagementMode.Managed, [HarnessManagementScope.KnowledgeResources], [], []));
        var ctx = await tools.HarnessContextAsync(instance.InstanceId, default);
        Assert.Equal(ToolPolicyDecision.Deny, tools.EvaluateExecutionPolicy(pinned, "harness.knowledge.upsert", admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId, Harness: ctx, SupportsTools: false)));
        Assert.Equal(ToolPolicyDecision.Deny, tools.EvaluateExecutionPolicy(pinned, "harness.skill.upsert", admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId, Harness: ctx)));
        Assert.Equal(ToolPolicyDecision.Deny, tools.EvaluateExecutionPolicy(pinned, "harness.knowledge.upsert", admission: new(true, TriggerKind.ScheduledOccurrence, AgentInstanceId: instance.InstanceId, Harness: ctx)));
        var args = JsonSerializer.Serialize(new { expectedVersion = 7, policyRevision = ctx!.PolicyRevision, id = "unread", source = "https://unread.example", content = "Unproven content", expected = "Read it", observed = "Claimed success" });
        var call = new ModelToolCall("unread", "harness.knowledge.upsert", args);
        Assert.Contains("Read the source", (await tools.ExecuteAsync(pinned, Guid.NewGuid(), call, 100000, admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId))).Text);
        instance = await service.ConfigureAsync(instance.InstanceId, instance.Revision, instance.HarnessManagement!.Policy with { Frozen = true });
        Assert.Null(await tools.HarnessContextAsync(instance.InstanceId, default));
        Assert.Contains("forbidden", (await tools.ExecuteAsync(pinned, Guid.NewGuid(), call, 100000, admission: new(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId, Harness: ctx))).Text);
        Assert.Equal(7, instance.ActiveVersion);
    }

    [Fact]
    public void Harness_approval_shows_the_actual_semantic_operation_and_untruncated_change()
    {
        using var content = JsonDocument.Parse(JsonSerializer.Serialize(new { content = new string('x', 600) + " exact tail", expectedVersion = 7, policyRevision = 1 }));
        Assert.EndsWith("exact tail", ToolApprovalPreview.Build("harness.instructions.update", content.RootElement).Details["Change"]);
        using var tool = JsonDocument.Parse("""{"id":"http.request","enabled":false,"content":"misleading unrelated content","expectedVersion":7,"policyRevision":1}""");
        var preview = ToolApprovalPreview.Build("harness.tool.select", tool.RootElement);
        Assert.Contains("http.request", preview.Details["Change"]);
        Assert.DoesNotContain("misleading", preview.Details["Change"]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{\"error\":\"source_denied\",\"content\":\"not obtained\"}")]
    [InlineData("{\"finalUrl\":\"https://example.test\"}")]
    public void Failed_empty_or_non_object_results_never_create_source_receipts(string result)
    {
        Assert.Empty(HarnessChatTools.Sources(new("read", "web.fetch", """{"url":"https://example.test"}"""), result));
    }

    private static IToolConfigurationGate executorGate(IServiceProvider services) => services.GetRequiredService<IToolConfigurationGate>();
}
