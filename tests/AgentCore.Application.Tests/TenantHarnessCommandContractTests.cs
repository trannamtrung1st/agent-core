using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;
public sealed class TenantHarnessCommandContractTests
{
    private static JsonObject Arguments(string tool) => new()
    {
        ["expectedVersion"] = 32, ["expectedInstanceRevision"] = 3, ["policyRevision"] = 2,
        ["expected"] = "Next Run uses the saved content.", ["observed"] = "Verified",
        [tool == "harness.instructions.update" ? "content" : "id"] = tool == "harness.instructions.update" ? "Complete instructions" : tool == "harness.tool.configure" ? "attachments.read" : tool == "harness.tool.select" ? "workspace.read" : "note"
    };
    private static PreparedManagementMutation Plan()
    {
        var original = TenantManagementMutationContract.Parse("harness.instructions.update", Arguments("harness.instructions.update").ToJsonString());
        var canonical = TenantManagementMutationContract.Parse("settings.patch", """{"expectedInstanceRevision":3,"section":"instructions","set":{"agentInstructions":"Complete instructions"},"clear":[]}""");
        var assessment = new HarnessInstanceChange(Guid.NewGuid(), "instructions.update", 4, 32, 2, null, ToolActionHash.Compute(original.Tool, original.Value), null, DateTimeOffset.UnixEpoch,
            new("Agent", 4, "Instance self-management", HarnessEvidenceStatus.PartiallyVerified, "Next Run uses the saved content.", "Verified", "Agent assessment is partial; production effects and subjective quality require external evidence."));
        return new(Guid.NewGuid(), Guid.NewGuid(), canonical.Tool, canonical.Json, new('a', 64), new('b', 64), new('c', 64), "[]",
            Settings: new(AgentInstructions: new("Complete instructions")), AutomationChanges: [], Harness: new(original.Tool, original.Json, assessment));
    }
    [Fact]
    public void Original_Harness_intent_and_partial_assessment_remain_bound_to_saved_canonical_effect()
    {
        var plan = Plan(); TenantManagementMutationRules.Validate(plan);
        var restored = JsonSerializer.Deserialize<PreparedManagementMutation>(JsonSerializer.Serialize(plan))!; Assert.Equal(plan, restored);
        var op = TenantApprovalRules.PrepareManagementMutation(Guid.NewGuid(), Guid.NewGuid(), plan, 3, 1);
        Assert.Equal(op, TenantApprovalRules.Rebuild(JsonSerializer.Deserialize<PreparedTenantOperation>(JsonSerializer.Serialize(op))!));
        Assert.True(new TenantManagementMutationIntent(plan.Harness!.ToolName, plan.Harness.ArgumentsJson).Matches(op));
        Assert.False(new TenantManagementMutationIntent(plan.ToolName, plan.ArgumentsJson).Matches(op));
        Assert.Throws<ArgumentException>(() => TenantManagementMutationRules.Validate(plan with { Harness = plan.Harness with { Assessment = plan.Harness.Assessment with { Evidence = plan.Harness.Assessment.Evidence with { Status = HarnessEvidenceStatus.Verified } } } }));
        Assert.Throws<ArgumentException>(() => TenantManagementMutationRules.Validate(plan with { Harness = plan.Harness with { ArgumentsJson = plan.Harness.ArgumentsJson.Replace("Complete instructions", "Substituted content") } }));
    }
    [Theory]
    [InlineData("extra")] [InlineData("credentials")] [InlineData("duplicate")] [InlineData("foreign_instance")] [InlineData("invalid_version")] [InlineData("wider_tool")]
    public void Malformed_or_wider_Harness_commands_cannot_enter_the_canonical_plan(string boundary)
    {
        const string tool = "harness.instructions.update"; var args = Arguments(tool);
        switch (boundary)
        {
            case "extra": args["publish"] = true; break;
            case "foreign_instance": args["instanceId"] = Guid.NewGuid().ToString(); break;
            case "credentials": args["content"] = "Bearer synthetic-secret-reference-fixture";
                Assert.Throws<AgentCore.Application.Sessions.AgentCoreException>(() => TenantManagementMutationContract.Parse(tool, args.ToJsonString())); return;
            case "invalid_version": args["expectedVersion"] = 0; break;
            case "wider_tool": Assert.Throws<ArgumentException>(() => TenantManagementMutationContract.Parse("harness.publish", args.ToJsonString())); return;
            case "duplicate": Assert.Throws<ArgumentException>(() => TenantManagementMutationContract.Parse(tool, args.ToJsonString().Replace("{", "{\"expectedVersion\":7,"))); return;
        }
        Assert.Throws<ArgumentException>(() => TenantManagementMutationContract.Parse(tool, args.ToJsonString()));
    }
    [Theory]
    [InlineData("harness.knowledge.upsert", HarnessManagementScope.KnowledgeResources)]
    [InlineData("harness.knowledge.remove", HarnessManagementScope.KnowledgeResources)]
    [InlineData("harness.instructions.update", HarnessManagementScope.Instructions)]
    [InlineData("harness.tool.select", HarnessManagementScope.ToolSelection)]
    [InlineData("harness.tool.configure", HarnessManagementScope.ToolSelection)]
    public void Harness_delegation_does_not_require_or_inherit_generic_Settings_or_Resources(string tool, HarnessManagementScope scope)
    {
        var baseline = SampleDefinitions.Examiner with { SelfManagement = new(HarnessManagementMode.Managed, [scope], [], []) };
        Assert.True(TenantManagementMutationContract.Allows(baseline, tool));
        Assert.False(TenantManagementMutationContract.Allows(baseline, "settings.patch"));
        Assert.False(TenantManagementMutationContract.Allows(baseline, "resources.upsert"));
        Assert.False(TenantManagementMutationContract.Allows(baseline with { SelfManagement = baseline.SelfManagement with { Frozen = true } }, tool));
        Assert.False(TenantManagementMutationContract.Allows(baseline with { SelfManagement = baseline.SelfManagement with { Scopes = [HarnessManagementScope.Settings, HarnessManagementScope.Resources] } }, tool));
    }
}
