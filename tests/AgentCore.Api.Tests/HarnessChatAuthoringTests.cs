using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class HarnessChatAuthoringTests
{
    [Fact]
    public async Task Inspect_separates_future_authoring_from_the_current_session_pin()
    {
        await using var factory = new AgentCoreApiFactory();
        var services = factory.Services;
        var authoring = services.GetRequiredService<HarnessManagementService>();
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 17);
        instance = await authoring.ConfigureAsync(instance.InstanceId, instance.Revision,
            new(HarnessManagementMode.Managed, [HarnessManagementScope.ToolSelection], [], []));
        var sessions = services.GetRequiredService<AgentCore.Application.Sessions.SessionManager>();
        var current = await sessions.CreateForInstanceAsync(instance.InstanceId, AgentCore.Domain.Conversation.SessionMode.Text);
        var executor = services.GetRequiredService<SessionToolExecutor>();
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId);
        var args = JsonSerializer.Serialize(new { expectedVersion = 17, policyRevision = instance.HarnessManagement!.PolicyRevision,
            id = ToolCatalog.WorkspaceMove, enabled = false, expected = "Future sessions omit move.", observed = "Owner removes the capability." });
        using var action = JsonDocument.Parse(args);
        var grant = new ToolApprovalGrant(Guid.NewGuid(), "harness.tool.select", ToolActionHash.Compute("harness.tool.select", action.RootElement), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var saved = await executor.ExecuteAsync(current.Definition, current.SessionId, new("change", "harness.tool.select", args), 100000, approvalGrant: grant, admission: admission);
        Assert.Contains("\"saved\":true", saved.Text);
        var inspection = await executor.ExecuteAsync(current.Definition, current.SessionId, new("inspect", "harness.inspect", "{}"), 100000, admission: admission);
        using var json = JsonDocument.Parse(inspection.Text);
        var root = json.RootElement;
        Assert.Equal(21, root.GetProperty("activeDefinitionVersion").GetInt32());
        Assert.Equal(17, root.GetProperty("currentSessionPinnedDefinitionVersion").GetInt32());
        Assert.True(root.GetProperty("changesApplyToFutureSessions").GetBoolean());
        Assert.True(root.TryGetProperty("authoringEligibleTools", out _));
        Assert.DoesNotContain(ToolCatalog.WorkspaceMove, root.GetProperty("activeDefinitionAuthorizedCapabilities").EnumerateArray().Select(v => v.GetString()));
        Assert.Contains(ToolCatalog.WorkspaceMove, RoleEnvironments.Of((await sessions.GetAsync(current.SessionId)).Definition).ToolList);
        var fresh = await sessions.CreateForInstanceAsync(instance.InstanceId, AgentCore.Domain.Conversation.SessionMode.Text);
        Assert.Equal(21, fresh.Definition.Version);
        Assert.DoesNotContain(ToolCatalog.WorkspaceMove, RoleEnvironments.Of(fresh.Definition).ToolList);
    }


    private sealed class BrowserUnavailableFactory : AgentCoreApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IToolConfigurationGate>();
                services.AddSingleton(ToolConfigurationGates.Unconfigured);
            });
        }
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
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 17);
        instance = await service.ConfigureAsync(instance.InstanceId, instance.Revision, new(mode, [scope], [], []));
        var pinned = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 17))!;
        var tools = services.GetRequiredService<SessionToolExecutor>();
        var context = await tools.HarnessContextAsync(instance.InstanceId, default);
        var args = JsonSerializer.Serialize(new { expectedVersion = 17, policyRevision = context!.PolicyRevision,
            id = name == "harness.tool.configure" ? "attachments.read" : name == "harness.tool.select" ? "web.fetch" : "policy",
            enabled = false, allowUnreadUnsupportedTypes = true, content = "Reusable owner policy.", source = "conversation:user", expected = "Retain policy", observed = "Owner supplied it." });
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: instance.InstanceId, OwnerTurnText: "Retain this policy.");
        var call = new ModelToolCall("change", name, args);
        Assert.Contains("approval_required", (await tools.ExecuteAsync(pinned, Guid.NewGuid(), call, 100000, admission: admission)).Text);
        Assert.Equal(17, (await service.ReviewAsync(instance.InstanceId)).ActiveVersion);
        using var json = JsonDocument.Parse(args);
        var grant = new ToolApprovalGrant(Guid.NewGuid(), name, ToolActionHash.Compute(name, json.RootElement), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Contains("stale_approval", (await tools.ExecuteAsync(pinned, Guid.NewGuid(), call, 100000, approvalGrant: grant with { ActionHash = "altered" }, admission: admission)).Text);
        instance = await service.ConfigureAsync(instance.InstanceId, instance.Revision, instance.HarnessManagement!.Policy);
        var stale = await tools.ExecuteAsync(pinned, Guid.NewGuid(), call, 100000, approvalGrant: grant, admission: admission);
        Assert.Contains("changed", stale.Text);
        Assert.Equal(17, (await service.ReviewAsync(instance.InstanceId)).ActiveVersion);
        var freshArgs = args.Replace($"\"policyRevision\":{context.PolicyRevision}", $"\"policyRevision\":{instance.HarnessManagement!.PolicyRevision}");
        using var freshJson = JsonDocument.Parse(freshArgs);
        var result = await tools.ExecuteAsync(pinned, Guid.NewGuid(), call with { ArgumentsJson = freshArgs }, 100000,
            approvalGrant: grant with { ActionHash = ToolActionHash.Compute(name, freshJson.RootElement), ApprovalId = Guid.NewGuid() }, admission: admission);
        Assert.Contains("\"saved\":true", result.Text);
        Assert.Equal(ToolPolicyDecision.RequireApproval, ToolPolicy.EvaluateExecution(pinned, "http.request", executorGate(services)));
    }

    [Fact]
    public void Harness_approval_shows_the_actual_semantic_operation_and_untruncated_change()
    {
        using var content = JsonDocument.Parse(JsonSerializer.Serialize(new { content = new string('x', 600) + " exact tail", expectedVersion = 17, policyRevision = 1 }));
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
