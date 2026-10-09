using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class HarnessManagementBoundaryTests
{
    private static async Task<(AgentInstance Instance, HarnessReview Review)> Start(AgentCoreApiFactory factory,
        HarnessManagementMode mode, HarnessManagementScope[] scopes, string[]? tools = null)
    {
        var instance = await factory.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        var service = factory.Services.GetRequiredService<HarnessManagementService>();
        instance = await service.ConfigureAsync(instance.InstanceId, instance.Revision,
            new(mode, scopes, ["knowledge:support-order-policy"], tools ?? []));
        instance = await service.StartAsync(instance.InstanceId, instance.Revision, "Prepare an operations harness.");
        return (instance, await service.ReviewAsync(instance.InstanceId));
    }

    [Fact]
    public async Task Disabled_offers_nothing_and_policy_cannot_grant_registered_unauthorized_tools()
    {
        Assert.False(HarnessChatTools.Allows(HarnessChatTools.Inspect, new(HarnessManagementPolicy.Disabled, 1, 7)));
        await using var factory = new AgentCoreApiFactory();
        var instance = await factory.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
        var service = factory.Services.GetRequiredService<HarnessManagementService>();
        await Assert.ThrowsAsync<AgentCoreException>(async () => await service.ConfigureAsync(instance.InstanceId, instance.Revision,
            new(HarnessManagementMode.Managed, [HarnessManagementScope.ToolSelection], [], ["demo.sensitive_action"])));
        await Assert.ThrowsAsync<AgentCoreException>(async () => await service.StartAsync(instance.InstanceId, instance.Revision, "Prepare"));
        Assert.Null((await factory.Services.GetRequiredService<IAgentInstanceStore>().FindAsync(instance.InstanceId))!.HarnessManagement);
    }

    [Fact]
    public async Task Assisted_exact_edit_reject_and_stale_approval_keep_active_version_unchanged()
    {
        await using var factory = new AgentCoreApiFactory();
        var (instance, review) = await Start(factory, HarnessManagementMode.Assisted, [HarnessManagementScope.Instructions]);
        var service = factory.Services.GetRequiredService<HarnessManagementService>();
        var before = review.Draft!.Candidate.SystemInstructions;
        instance = await service.RequestOperationAsync(instance.InstanceId, review.State.Preparation!.PreparationId,
            new("instructions.update", review.Draft.Revision, Content: "Use a concise operations review."));
        Assert.Equal(before, (await service.ReviewAsync(instance.InstanceId)).Draft!.Candidate.SystemInstructions);
        var approval = Assert.Single(instance.HarnessManagement!.Preparation!.Approvals);
        await Assert.ThrowsAsync<AgentCoreException>(async () => await service.DecideApprovalAsync(instance.InstanceId,
            instance.Revision, approval.ApprovalId, "altered", true));
        instance = await service.DecideApprovalAsync(instance.InstanceId, instance.Revision, approval.ApprovalId, approval.ActionHash, false);
        Assert.Equal(before, (await service.ReviewAsync(instance.InstanceId)).Draft!.Candidate.SystemInstructions);
        instance = await service.RequestOperationAsync(instance.InstanceId, review.State.Preparation.PreparationId,
            new("instructions.update", review.Draft.Revision, Content: "Use another concise operations review."));
        approval = instance.HarnessManagement!.Preparation!.Approvals.Last();
        await factory.Services.GetRequiredService<AgentDefinitionLifecycleService>().UpdateDraftAsync(review.Draft.DraftId,
            review.Draft.Revision, review.Draft.Candidate with { SystemInstructions = "Concurrent owner change." });
        await Assert.ThrowsAsync<AgentCoreException>(async () => await service.DecideApprovalAsync(instance.InstanceId,
            instance.Revision, approval.ApprovalId, approval.ActionHash, true));
        Assert.Equal(21, (await service.ReviewAsync(instance.InstanceId)).ActiveVersion);
    }

    [Fact]
    public async Task Failed_actual_check_blocks_promotion_and_cancelled_grants_cannot_resume()
    {
        await using var factory = new AgentCoreApiFactory();
        var (instance, review) = await Start(factory, HarnessManagementMode.Managed, [HarnessManagementScope.KnowledgeResources]);
        var service = factory.Services.GetRequiredService<HarnessManagementService>();
        var prepId = review.State.Preparation!.PreparationId;
        await service.RecordAgentEvidenceAsync(instance.InstanceId, prepId, new("Agent", review.Draft!.Revision, "Assessment", HarnessEvidenceStatus.Verified, "Expected", "Observed"));
        await service.TestKnowledgeAsync(instance.InstanceId, prepId, "support-order-policy", "A deliberately absent factual assertion", default);
        instance = await service.VerifyAsync(instance.InstanceId, prepId);
        Assert.Equal(HarnessPreparationStatus.Failed, instance.HarnessManagement!.Preparation!.Status);
        await Assert.ThrowsAsync<AgentCoreException>(async () => await service.PromoteAsync(instance.InstanceId, instance.Revision, review.Draft.Revision));
        instance = await service.CancelAsync(instance.InstanceId, instance.Revision);
        await Assert.ThrowsAsync<AgentCoreException>(async () => await service.RequestOperationAsync(instance.InstanceId, prepId,
            new("knowledge.remove", review.Draft.Revision, Id: "support-order-policy")));
        Assert.Equal(21, instance.ActiveVersion);
    }

    [Theory]
    [InlineData("tool.select", true, null, HarnessManagementMode.Managed)]
    [InlineData("tool.select", true, null, HarnessManagementMode.Assisted)]
    [InlineData("tool.select", false, null, HarnessManagementMode.Managed)]
    [InlineData("tool.select", false, null, HarnessManagementMode.Assisted)]
    [InlineData("tool.configure", null, true, HarnessManagementMode.Managed)]
    [InlineData("tool.configure", null, true, HarnessManagementMode.Assisted)]
    public async Task Every_tool_change_requires_exact_approval_even_managed(string kind, bool? enabled, bool? readability, HarnessManagementMode mode)
    {
        await using var factory = new AgentCoreApiFactory();
        var tool = kind == "tool.configure" ? "attachments.read" : "http.request";
        // attachments.read is offered by the normal role policy rather than the serialized allowlist.
        var (instance, review) = await Start(factory, mode, [HarnessManagementScope.ToolSelection], kind == "tool.configure" ? [] : [tool]);
        var service = factory.Services.GetRequiredService<HarnessManagementService>();
        if (kind == "tool.configure")
        {
            // Explicit eligibility for the existing attachment capability is owner-owned.
            instance = await service.ConfigureAsync(instance.InstanceId, instance.Revision,
                new(mode, [HarnessManagementScope.ToolSelection], [], [tool]));
            instance = await service.StartAsync(instance.InstanceId, instance.Revision, "Configure attachment readability.");
            review = await service.ReviewAsync(instance.InstanceId);
        }
        var before = review.Draft!.Candidate;
        instance = await service.RequestOperationAsync(instance.InstanceId, review.State.Preparation!.PreparationId,
            new(kind, review.Draft.Revision, Id: tool, Enabled: enabled, AllowUnreadUnsupportedTypes: readability));
        Assert.Equal(review.Draft.Revision, (await service.ReviewAsync(instance.InstanceId)).Draft!.Revision);
        var approval = Assert.Single(instance.HarnessManagement!.Preparation!.Approvals);
        instance = await service.DecideApprovalAsync(instance.InstanceId, instance.Revision, approval.ApprovalId, approval.ActionHash, true);
        var after = await service.ReviewAsync(instance.InstanceId);
        Assert.Equal(21, after.ActiveVersion);
        Assert.True(after.Draft!.Revision > review.Draft.Revision);
        if (tool == "http.request" && enabled == true)
            Assert.Equal(ToolPolicyDecision.RequireApproval, ToolPolicy.EvaluateExecution(after.Draft.Candidate.ToPublished(1), tool, ToolConfigurationGates.AllowAll));
    }

    [Fact]
    public async Task Evidence_is_owned_and_stale_or_failed_checks_cannot_promote()
    {
        await using var factory = new AgentCoreApiFactory();
        var (instance, review) = await Start(factory, HarnessManagementMode.Managed, [HarnessManagementScope.Instructions]);
        var service = factory.Services.GetRequiredService<HarnessManagementService>();
        var prep = review.State.Preparation!;
        instance = await service.RecordAgentEvidenceAsync(instance.InstanceId, prep.PreparationId,
            new("Core", review.Draft!.Revision, "Fake system success", HarnessEvidenceStatus.Verified, "Claim", "Claim"));
        Assert.Equal("Agent", Assert.Single(instance.HarnessManagement!.Preparation!.Evidence).Actor);
        instance = await service.VerifyAsync(instance.InstanceId, prep.PreparationId);
        var oldRevision = review.Draft.Revision;
        instance = await service.RequestOperationAsync(instance.InstanceId, prep.PreparationId,
            new("instructions.update", oldRevision, Content: "Changed after verification."));
        review = await service.ReviewAsync(instance.InstanceId);
        await Assert.ThrowsAsync<AgentCoreException>(async () => await service.PromoteAsync(instance.InstanceId, instance.Revision, review.Draft!.Revision));
        await Assert.ThrowsAsync<AgentCoreException>(async () => await service.RecordAgentEvidenceAsync(instance.InstanceId, prep.PreparationId,
            new("Agent", oldRevision, "Stale", HarnessEvidenceStatus.Verified, "Old", "Old")));
        Assert.Equal(21, review.ActiveVersion);
    }
}
