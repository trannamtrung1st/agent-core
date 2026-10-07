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
        var instance = await factory.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 16);
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
        var instance = await factory.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 16);
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
        Assert.Equal(16, (await service.ReviewAsync(instance.InstanceId)).ActiveVersion);
    }

    [Fact]
    public async Task Scope_source_secret_skill_and_self_escalation_boundaries_are_enforced()
    {
        await using var factory = new AgentCoreApiFactory();
        var (instance, review) = await Start(factory, HarnessManagementMode.Managed, [HarnessManagementScope.KnowledgeResources, HarnessManagementScope.Skills]);
        var service = factory.Services.GetRequiredService<HarnessManagementService>();
        var prep = review.State.Preparation!;
        async Task Denied(HarnessAuthoringOperation op) => await Assert.ThrowsAsync<AgentCoreException>(async () => await service.RequestOperationAsync(instance.InstanceId, prep.PreparationId, op));
        await Denied(new("instructions.update", review.Draft!.Revision, Content: "Out of scope."));
        await Denied(new("policy.update", review.Draft.Revision));
        await Denied(new("publish", review.Draft.Revision));
        await Denied(new("knowledge.upsert", review.Draft.Revision, Id: "reference", Content: "Safe policy", Source: "https://unauthorized.example"));
        await Denied(new("knowledge.upsert", review.Draft.Revision, Id: "reference", Content: "OPENAI_API_KEY=secret", Source: "knowledge:support-order-policy"));
        await Denied(new("skill.upsert", review.Draft.Revision, Skill: new("unsafe", "Unsafe", "No grants", "Use a missing tool", [], ["demo.sensitive_action"], [])));
        await Denied(new("skill.upsert", review.Draft.Revision, Skill: new("unsafe", "Unsafe", "No code", "```sh\nrm -rf /\n```", [], [], [])));
        var projection = System.Text.Json.JsonSerializer.Serialize(await service.InspectAsync(instance.InstanceId, prep.PreparationId));
        Assert.DoesNotContain("ProviderPreferences", projection);
        Assert.DoesNotContain("PersonaJson", projection);
        Assert.DoesNotContain("ActionHash", projection);
        Assert.Equal(16, (await service.ReviewAsync(instance.InstanceId)).ActiveVersion);
    }

    [Theory]
    [InlineData("knowledge.upsert")]
    [InlineData("skill.upsert")]
    public async Task Assisted_knowledge_and_Skill_changes_wait_for_exact_owner_decision(string kind)
    {
        await using var factory = new AgentCoreApiFactory();
        var (instance, review) = await Start(factory, HarnessManagementMode.Assisted, [HarnessManagementScope.KnowledgeResources, HarnessManagementScope.Skills]);
        var service = factory.Services.GetRequiredService<HarnessManagementService>();
        var operation = kind == "knowledge.upsert"
            ? new HarnessAuthoringOperation(kind, review.Draft!.Revision, Id: "assisted", Content: "Safe owner policy", Source: "knowledge:support-order-policy")
            : new HarnessAuthoringOperation(kind, review.Draft!.Revision, Skill: new("assisted.review", "Review", "Safe review", "Review policy and stop.", [], ["chat.respond"], []));
        instance = await service.RequestOperationAsync(instance.InstanceId, review.State.Preparation!.PreparationId, operation);
        Assert.Equal(review.Draft.Revision, (await service.ReviewAsync(instance.InstanceId)).Draft!.Revision);
        var approval = Assert.Single(instance.HarnessManagement!.Preparation!.Approvals);
        instance = await service.DecideApprovalAsync(instance.InstanceId, instance.Revision, approval.ApprovalId, approval.ActionHash, true);
        Assert.True((await service.ReviewAsync(instance.InstanceId)).Draft!.Revision > review.Draft.Revision);
        Assert.Equal(16, instance.ActiveVersion);
    }

    [Fact]
    public async Task Managed_knowledge_and_Skill_crud_are_revisioned_and_instance_policy_is_independent()
    {
        await using var factory = new AgentCoreApiFactory();
        var (instance, review) = await Start(factory, HarnessManagementMode.Managed, [HarnessManagementScope.KnowledgeResources, HarnessManagementScope.Skills]);
        var other = await factory.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 16);
        var service = factory.Services.GetRequiredService<HarnessManagementService>();
        Assert.Equal(HarnessManagementMode.Disabled, (await service.ReviewAsync(other.InstanceId)).State.Policy.Mode);
        var prepId = review.State.Preparation!.PreparationId;
        async Task Edit(HarnessAuthoringOperation operation)
        {
            await service.RequestOperationAsync(instance.InstanceId, prepId, operation);
            var next = await service.ReviewAsync(instance.InstanceId);
            Assert.True(next.Draft!.Revision > review.Draft!.Revision);
            review = next;
        }
        await Edit(new("knowledge.upsert", review.Draft!.Revision, Id: "reference", Content: "Order review requires evidence.", Source: "knowledge:support-order-policy"));
        await Edit(new("knowledge.upsert", review.Draft!.Revision, Id: "reference", Content: "Updated order review requires owner evidence.", Source: "knowledge:support-order-policy"));
        var resource = review.Resources.Single(r => r.LogicalPath == "knowledge/reference");
        Assert.Contains("Updated", System.Text.Encoding.UTF8.GetString(await service.ReadCandidateResourceAsync(instance.InstanceId, prepId, resource.ResourceId, default)));
        var skill = new SkillSpec("orders.review", "Order review", "Safe review", "Read evidence and stop before a refund.", ["review"], ["chat.respond"], ["knowledge/reference"]);
        await Edit(new("skill.upsert", review.Draft.Revision, Skill: skill));
        await Edit(new("skill.upsert", review.Draft!.Revision, Skill: skill with { Procedure = "Read evidence, classify owner attention and stop." }));
        await service.TestSkillActivationAsync(instance.InstanceId, prepId, skill.Id, default);
        Assert.Contains((await service.ReviewAsync(instance.InstanceId)).State.Preparation!.Evidence, e => e.Check == "Candidate Skill activation" && e.Status == HarnessEvidenceStatus.Verified);
        await Edit(new("skill.remove", review.Draft!.Revision, Id: skill.Id));
        await Edit(new("knowledge.remove", review.Draft!.Revision, Id: "reference"));
        Assert.DoesNotContain(review.Resources, r => r.LogicalPath == "knowledge/reference");
        Assert.DoesNotContain(review.Draft!.Candidate.SkillList, s => s.Id == skill.Id);
        Assert.Equal(16, review.ActiveVersion);
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
        Assert.Equal(16, instance.ActiveVersion);
    }

    [Theory]
    [InlineData("Agent")]
    [InlineData("Knowledge")]
    [InlineData("Skill")]
    public async Task Failed_evidence_after_ready_revokes_promotion_without_another_verify(string failure)
    {
        await using var factory = new AgentCoreApiFactory();
        var (instance, review) = await Start(factory, HarnessManagementMode.Managed, [HarnessManagementScope.KnowledgeResources, HarnessManagementScope.Skills]);
        var service = factory.Services.GetRequiredService<HarnessManagementService>();
        var prepId = review.State.Preparation!.PreparationId;
        await service.RecordAgentEvidenceAsync(instance.InstanceId, prepId,
            new("Agent", review.Draft!.Revision, "Initial assessment", HarnessEvidenceStatus.PartiallyVerified, "Expected", "Observed", "External effects untested."));
        instance = await service.VerifyAsync(instance.InstanceId, prepId);
        Assert.Equal(HarnessPreparationStatus.Ready, instance.HarnessManagement!.Preparation!.Status);
        instance = failure switch
        {
            "Agent" => await service.RecordAgentEvidenceAsync(instance.InstanceId, prepId,
                new("Agent", review.Draft.Revision, "Failed assessment", HarnessEvidenceStatus.Failed, "Expected", "Failed")),
            "Knowledge" => await service.TestKnowledgeAsync(instance.InstanceId, prepId, "missing", "Expected content"),
            _ => await service.TestSkillActivationAsync(instance.InstanceId, prepId, "missing", default)
        };
        Assert.Equal(HarnessPreparationStatus.Failed, instance.HarnessManagement!.Preparation!.Status);
        await Assert.ThrowsAsync<AgentCoreException>(async () => await service.PromoteAsync(instance.InstanceId, instance.Revision, review.Draft.Revision));
        instance = await service.VerifyAsync(instance.InstanceId, prepId);
        Assert.Equal(HarnessPreparationStatus.Failed, instance.HarnessManagement!.Preparation!.Status);
        Assert.Equal(16, instance.ActiveVersion);
        instance = await service.StartAsync(instance.InstanceId, instance.Revision, "Prepare a corrected candidate.");
        Assert.NotEqual(prepId, instance.HarnessManagement!.Preparation!.PreparationId);
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
        Assert.Equal(16, after.ActiveVersion);
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
        Assert.Equal(16, review.ActiveVersion);
    }
}
