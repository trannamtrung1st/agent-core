using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tests;

public sealed class ManagementProjectionTests
{
    private static AgentContext Context()
    {
        var policy = new HarnessManagementPolicy(HarnessManagementMode.Managed, [HarnessManagementScope.Settings, HarnessManagementScope.KnowledgeResources], [], [],
            ManagedAutoApply: [ManagementAutoApply.ConversationPreferences]);
        var definition = SampleDefinitions.Examiner with { SelfManagement = policy, ExecutionPolicy = new(ToolAllowlist: ["configuration.inspect"]) };
        var session = Guid.NewGuid(); var activation = Guid.NewGuid();
        return new(definition, [], "", null, SessionMode.Text, null, false, null, new(Guid.NewGuid(), TriggerKind.UserTurn, "Load a delegated procedure"),
            AgentInstanceId: Guid.NewGuid(), OwnedSessionId: session, Harness: new(policy, 1, 1), PinnedSkillCatalog: ManagementBuiltInSkills.Catalog)
        { ExecutionContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), activation, activation, null, null, ExecutionOrigin.UserMessage) };
    }
    [Theory]
    [InlineData("live_freeze")] [InlineData("pinned_off")] [InlineData("live_off")]
    [InlineData("missing_context")] [InlineData("foreign_session")] [InlineData("no_tools")]
    [InlineData("no_builtin_choices")] [InlineData("background")] [InlineData("no_pin")]
    public void Canonical_procedure_offering_requires_current_and_admitted_context(string boundary)
    {
        var context = Context();
        context = boundary switch {
            "live_freeze" => context with { Harness = context.Harness! with { Policy = context.Harness.Policy with { Frozen = true } } },
            "pinned_off" => context with { Definition = context.Definition with { SelfManagement = ManagementPolicyResolver.Restrict(null, context.Definition.SelfManagement) } },
            "live_off" => context with { Definition = context.Definition with { SelfManagement = ManagementPolicyResolver.Restrict(context.Definition.SelfManagement, null) }, Harness = null },
            "missing_context" => context with { Harness = null },
            "foreign_session" => context with { OwnedSessionId = Guid.NewGuid() },
            "no_tools" => context with { ModelSupportsTools = false },
            "no_builtin_choices" => context with { Harness = context.Harness! with { Policy = context.Harness.Policy with { BuiltInSkills = new(false, false) } } },
            "background" => context with { OutputContract = AgentRunOutputContract.BackgroundOutcome },
            "no_pin" => context with { PinnedSkillCatalog = [] }, _ => throw new ArgumentException()
        };
        Assert.Empty(TenantCoreProcedureContract.Visible(context));
        var load = ToolCatalog.For(context.Definition, context, ToolConfigurationGates.AllowAll).SingleOrDefault(t => t.Name == ToolCatalog.SkillsLoad);
        if (boundary is "foreign_session" or "no_tools" or "background") Assert.Null(load);
        else
        {
            Assert.NotNull(load); Assert.Contains("(definition|instance)", load.ParametersJson);
            foreach (var core in ManagementBuiltInSkills.Catalog) Assert.DoesNotContain(core.Key, load.ParametersJson);
        }
    }
    [Fact]
    public void Offered_schema_names_exact_Core_keys_and_live_choice_removes_a_single_procedure()
    {
        var context = Context();
        var tool = Assert.Single(ToolCatalog.For(context.Definition, context, ToolConfigurationGates.AllowAll), t => t.Name == ToolCatalog.SkillsLoad);
        using var schema = JsonDocument.Parse(tool.ParametersJson);
        Assert.Equal(ManagementBuiltInSkills.Catalog.Select(s => s.Key), schema.RootElement.GetProperty("properties").GetProperty("ids").GetProperty("items").GetProperty("anyOf")[0].GetProperty("enum").EnumerateArray().Select(s => s.GetString()));
        context = context with { Harness = context.Harness! with { Policy = context.Harness.Policy with { BuiltInSkills = new(false, true) } } };
        Assert.Equal(ManagementBuiltInSkills.HarnessKey, Assert.Single(TenantCoreProcedureContract.Visible(context)).Key);
        Assert.DoesNotContain(ToolCatalog.For(context.Definition, context, ToolConfigurationGates.AllowAll), t => t.Name == "harness.inspect");
    }
    [Fact]
    public void Restrictive_projection_preserves_instructions_and_preferences_while_removing_live_auto_apply()
    {
        var context = Context(); var pin = context.Definition;
        var projected = pin with { SelfManagement = ManagementPolicyResolver.Restrict(pin.SelfManagement, pin.SelfManagement! with { Mode = HarnessManagementMode.Assisted }) };
        Assert.Equal(pin.AgentInstructions, projected.AgentInstructions); Assert.Equal(pin.ConversationPolicy, projected.ConversationPolicy);
        Assert.False(TenantPreferenceToolContract.Allows(projected)); Assert.True(TenantPreferenceToolContract.Allows(pin));
    }
    [Theory]
    [InlineData("{\"ids\":[\"builtin:harness-authoring.v1\"],\"ids\":[\"instance:private\"]}")]
    [InlineData("{\"ids\":[\"builtin:harness-authoring.v1\"],\"owner\":\"foreign\"}")]
    public void Procedure_argument_parser_rejects_ambiguous_or_wider_selectors(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(SkillLoadAdmission.TryParseIds(document.RootElement, out _, out _));
    }
}
