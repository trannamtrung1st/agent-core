using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Application.Agents;

namespace AgentCore.Application.Tests;

public sealed class BrowserContractCutoverTests
{
    [Theory]
    [InlineData("The retired contract documented scopeRef, targetRef and opaque refs.")]
    [InlineData("Do not use browser.click with targetRef; use a direct target.")]
    [InlineData("Use browser.snapshot without scopeRef.")]
    [InlineData("Use browser.snapshot instead of targetRef.")]
    public void Historical_mentions_and_upgrade_guidance_are_not_executable(string text)
    {
        Assert.False(BrowserContractCutover.Retired(text));
        BrowserContractCutover.EnsureCurrent(SampleDefinitions.Examiner with { SystemInstructions = text });
    }

    [Fact]
    public void Inactive_obsolete_skill_does_not_block_prompt_or_independent_execution()
    {
        var old = new EffectiveSkill("instance:old-browser", SkillOrigin.Instance, "old-browser", "Old browser", "Historical", "Use browser.find and pass its opaque ref as scopeRef.", SkillProjection.OnDemand, [ToolCatalog.BrowserClick], []);
        var context = new AgentContext(SampleDefinitions.Examiner, [], "", null, SessionMode.Text, null, false, null,
            new(Guid.NewGuid(), TriggerKind.UserTurn, "Hello"), PinnedSkillCatalog: [old], ActiveSkillKeys: []);
        new PromptContextBuilder().BuildSections(context);
        var plan = SkillLoadAdmission.Plan([old], [], 0, [old.Key]);
        Assert.Empty(plan.Admitted);
        Assert.Contains("browser_contract_retired", Assert.Single(plan.Rejected).Reason);
        Assert.Contains("Update this Skill", Assert.Single(plan.Rejected).Message);
        var error = Assert.Throws<AgentCoreException>(() => new PromptContextBuilder().BuildSections(context with { ActiveSkillKeys = [old.Key] }));
        Assert.Contains(old.Key, error.Message);
        Assert.Throws<AgentCoreException>(() => PromptContextBuilder.BuildActiveSkillSystem([old], [old.Key]));
        // An independent execution starts with its own active keys, never prior Run activation.
        BrowserContractCutover.EnsureCurrent(SampleDefinitions.Examiner, [old], []);
        var current = old with { Procedure = "Use browser.click with target by role and accessible name." };
        Assert.Single(SkillLoadAdmission.Plan([current], [], 0, [current.Key]).Admitted);
        BrowserContractCutover.EnsureCurrent(SampleDefinitions.Examiner, [current], [current.Key]);
    }

    [Theory]
    [InlineData(SkillProjection.OnDemand, true, false)]
    [InlineData(SkillProjection.Always, false, false)]
    [InlineData(SkillProjection.Always, true, true)]
    public void Definition_skill_validation_matches_default_activation(SkillProjection projection, bool enabled, bool rejected)
    {
        var definition = SampleDefinitions.Examiner with { Skills = [new("old", "Old browser", "History", "Use scopeRef from browser.find.", projection, enabled, [], [])] };
        Assert.Equal(rejected, BrowserContractCutover.Diagnostic(definition) is not null);
    }

    [Fact]
    public void Historical_receipts_remain_readable_but_cannot_resume_browser_effects()
    {
        var call = new ModelToolCall("old-effect", ToolCatalog.BrowserClick, "{\"ref\":\"el_historical\"}");
        var document = JsonNode.Parse(AgentRunToolCallCheckpoint.Write([
            new(ModelRole.Assistant, "", ToolCalls: [call])]))!.AsObject();
        document.Remove("BrowserContractVersion");
        var historical = new AgentRunCheckpoint(document.ToJsonString(), 1, 0, 300000);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(historical, out var messages));
        Assert.Equal(call, Assert.Single(AgentRunToolCallCheckpoint.PendingCalls(messages!)));
        var error = Assert.Throws<AgentCoreException>(() => AgentRunToolCallCheckpoint.EnsureCurrentBrowserContract(historical));
        Assert.Contains("cannot resume", error.Message);
        Assert.Equal(call.ArgumentsJson, Assert.Single(messages![0].ToolCalls!).ArgumentsJson);
    }

    [Fact]
    public void Current_browser_checkpoint_and_old_nonbrowser_checkpoint_remain_executable()
    {
        var current = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([
            new(ModelRole.Assistant, "", ToolCalls: [new("now", ToolCatalog.BrowserClose, "{}")])]), 1, 0, 300000);
        AgentRunToolCallCheckpoint.EnsureCurrentBrowserContract(current);
        var document = JsonNode.Parse(AgentRunToolCallCheckpoint.Write([new(ModelRole.Tool, "safe", Name: ToolCatalog.WorkspaceRead)]))!.AsObject();
        document.Remove("BrowserContractVersion");
        AgentRunToolCallCheckpoint.EnsureCurrentBrowserContract(new(document.ToJsonString(), 1, 0, 300000));
    }

    [Fact]
    public void Restored_frame_observation_keeps_identity_and_requires_current_provider_inventory()
    {
        const string reference = "fr_00000000000000000000000000000000";
        var call = new ModelToolCall("observe", ToolCatalog.BrowserSnapshot, "{\"frameRef\":\"" + reference + "\"}");
        var checkpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 300000);
        AgentRunToolCallCheckpoint.EnsureCurrentBrowserContract(checkpoint);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(checkpoint, out var messages));
        var restored = Assert.Single(AgentRunToolCallCheckpoint.PendingCalls(messages!));
        Assert.Equal(call, restored);
        Assert.True(BrowserToolArguments.TryRequest(Guid.NewGuid(), restored.Name, System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(restored.ArgumentsJson), out var request, out _));
        Assert.Equal(reference, Assert.IsType<BrowserObserve>(request.Command).FrameRef);
        // The typed command carries data only: native dispatch must resolve this ID again.
    }

    [Theory]
    [InlineData("{\"Phase\":\"waiting-signal\"}")]
    [InlineData("{\"Phase\":\"model-turn\"}")]
    [InlineData("{not-model-checkpoint")]
    public void Contract_fence_does_not_parse_unrelated_or_empty_checkpoint_phases(string payload) =>
        AgentRunToolCallCheckpoint.EnsureCurrentBrowserContract(new(payload, 0, 0, 1000));

    [Fact]
    public void Unreadable_model_browser_checkpoint_fails_closed() =>
        Assert.Throws<AgentCoreException>(() => AgentRunToolCallCheckpoint.EnsureCurrentBrowserContract(
            new("{\"Phase\":\"model-turn\",\"Name\":\"browser.click\",", 0, 0, 1000)));

    [Fact]
    public void Old_pinned_instructions_are_rejected_before_building_a_model_context()
    {
        var historical = SampleDefinitions.Examiner with { SystemInstructions = "Use browser.find and its opaque ref as scopeRef." };
        Assert.Throws<AgentCoreException>(() => BrowserContractCutover.EnsureCurrent(historical));
        BrowserContractCutover.EnsureCurrent(SampleDefinitions.Examiner);
    }
}
