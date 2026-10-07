using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
namespace AgentCore.Application.Tests;

public sealed class DynamicSkillActivationTests
{
    [Fact]
    public void Always_is_active_without_keywords_and_demand_load_is_bounded_by_context()
    {
        var catalog = Enumerable.Range(0, 12).Select(i => Skill("definition:skill" + i, new string('p', 1000))).ToArray();
        var first = SkillLoadAdmission.Plan(catalog, [], 0, catalog.Take(4).Select(s => s.Key).ToArray());
        Assert.Equal(4, first.Admitted.Count);
        var second = SkillLoadAdmission.Plan(catalog, first.Admitted, 1, catalog.Skip(4).Take(4).Select(s => s.Key).ToArray());
        Assert.Equal(4, second.Admitted.Count);
        Assert.Equal(8, first.Admitted.Concat(second.Admitted).Count());
        var exhausted = SkillLoadAdmission.Plan(catalog, first.Admitted.Concat(second.Admitted).ToArray(), 2, [catalog[8].Key]);
        Assert.False(exhausted.IncrementInvocation); Assert.Equal("over_budget", Assert.Single(exhausted.Rejected).Reason);
        var overContext = SkillLoadAdmission.Plan(catalog, catalog.Take(8).Select(s => s.Key).ToArray(), 1, [catalog[8].Key]);
        Assert.Equal("over_budget", Assert.Single(overContext.Rejected).Reason);
        Assert.Equal("duplicate", SkillLoadAdmission.Plan(catalog, [catalog[0].Key], 0, [catalog[0].Key]).Outcome);
        Assert.Equal("invalid", Assert.Single(SkillLoadAdmission.Plan(catalog, [], 0, ["skill0"]).Rejected).Reason);
        Assert.Equal("unknown", Assert.Single(SkillLoadAdmission.Plan(catalog, [], 0, ["definition:disabled"]).Rejected).Reason);
    }
    [Fact]
    public void Active_instance_requirements_project_only_authorized_tools()
    {
        var d = SampleDefinitions.Examiner with { Environment = RoleEnvironment.Empty with { ToolAllowlist = null,
            Capabilities = new("Selected", [ToolCatalog.WorkspaceRead]), Projection = new([]) } };
        var skill = Skill("instance:" + Guid.NewGuid().ToString("D"), "Read context") with { RequiredCapabilities = [ToolCatalog.WorkspaceRead, ToolCatalog.WorkspaceWrite] };
        var context = new AgentContext(d, [], "", null, SessionMode.Text, null, false, null, new(Guid.NewGuid(), TriggerKind.UserTurn, "hello"),
            AgentInstanceId: Guid.NewGuid(), PinnedSkillCatalog: [skill], ActiveSkillKeys: [skill.Key]);
        var projected = ToolCatalog.For(d, context, ToolConfigurationGates.AllowAll).Select(t => t.Name).ToArray();
        Assert.Contains(ToolCatalog.WorkspaceRead, projected); Assert.DoesNotContain(ToolCatalog.WorkspaceWrite, projected);
        Assert.DoesNotContain("skills.create", projected);
        Assert.Equal(ToolPolicyDecision.Deny, ToolPolicy.EvaluateExecution(d, "skills.create", ToolConfigurationGates.AllowAll, admission: new(false, TriggerKind.UserTurn, AgentInstanceId: context.AgentInstanceId)));
    }
    [Fact]
    public void Canonical_tool_schemas_have_no_target_instance_or_shape_union()
    {
        foreach (var descriptor in InstanceSkillTools.Descriptors())
        {
            using var json = JsonDocument.Parse(descriptor.ModelDefinition.ParametersJson);
            Assert.Equal("object", json.RootElement.GetProperty("type").GetString());
            Assert.False(json.RootElement.TryGetProperty("oneOf", out _));
            Assert.False(json.RootElement.GetProperty("properties").TryGetProperty("agentInstanceId", out _));
            Assert.False(json.RootElement.GetProperty("additionalProperties").GetBoolean());
        }
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ids\":[]}")]
    [InlineData("{\"ids\":[1]}")]
    [InlineData("{\"ids\":[\"definition:one\"],\"agentInstanceId\":\"other\"}")]
    public void Load_rejects_malformed_shape(string input)
    {
        using var json = JsonDocument.Parse(input);
        Assert.False(SkillLoadAdmission.TryParseIds(json.RootElement, out _, out _));
    }
    private static EffectiveSkill Skill(string key, string procedure) => new(key, key.StartsWith("definition:") ? SkillOrigin.Definition : SkillOrigin.Instance,
        key, "Review", "Review context", procedure, SkillProjection.OnDemand, [], []);
}
