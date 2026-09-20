using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;

namespace AgentCore.Application.Tests;

public sealed class PromptContextBuilderTests
{
    [Fact]
    public void Voice_mode_system_prompt_has_no_marker_syntax()
    {
        var builder = new PromptContextBuilder();
        var context = new AgentContext(
            SampleDefinitions.Examiner,
            [],
            string.Empty,
            null,
            SessionMode.Voice,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "Hello"));
        var request = builder.Build(context, Guid.NewGuid());
        var modeSystem = request.Messages
            .Where(message => message.Role == ModelRole.System)
            .Select(message => message.Text)
            .First(text => text.Contains("Current session mode: Voice", StringComparison.Ordinal));
        Assert.Contains(PromptContextBuilder.VoiceModeOutputGuidance, modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("[[", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("]]", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("speech:", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("Voice compatibility", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("marker", modeSystem, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Speak naturally", modeSystem, StringComparison.Ordinal);
        Assert.Contains("name the relevant on-screen detail", modeSystem, StringComparison.Ordinal);
        Assert.Null(request.ResponseContract);
        Assert.DoesNotContain("Speak naturally", request.Messages[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("MUST begin", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("Here is the summary", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("Examples (GOOD", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("Examples (BAD", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("semantically the same", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("several sentences long", modeSystem, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_mode_system_prompt_omits_voice_output_guidance()
    {
        var builder = new PromptContextBuilder();
        var context = new AgentContext(
            SampleDefinitions.Examiner,
            [],
            string.Empty,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "Hello"));
        var request = builder.Build(context, Guid.NewGuid());
        var combined = string.Join('\n', request.Messages.Where(message => message.Role == ModelRole.System).Select(message => message.Text));
        Assert.DoesNotContain("Voice compatibility", combined, StringComparison.Ordinal);
        Assert.DoesNotContain(PromptContextBuilder.VoiceModeOutputGuidance, combined, StringComparison.Ordinal);
        Assert.DoesNotContain("[[speech:", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("[[md:", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("[[artifact:", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void Application_prompt_guidance_does_not_reintroduce_markers()
    {
        Assert.DoesNotContain("[[", PromptContextBuilder.VoiceModeOutputGuidance, StringComparison.Ordinal);
        Assert.DoesNotContain("speech:", PromptContextBuilder.VoiceModeOutputGuidance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Identity_system_includes_conversation_language_policy_not_persona_duplication()
    {
        var fixedIdentity = PromptContextBuilder.BuildIdentitySystem(SampleDefinitions.Examiner);
        Assert.Contains("language=en", fixedIdentity, StringComparison.Ordinal);
        Assert.Contains(ConversationLanguagePolicy.PromptInstruction("en"), fixedIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain("Respond in", SampleDefinitions.Examiner.SystemInstructions, StringComparison.Ordinal);

        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        var general = await store.GetAsync("general-assistant", 1);
        Assert.NotNull(general);
        Assert.True(ConversationLanguagePolicy.IsAuto(general!.ConversationPolicy.Language));
        var autoIdentity = PromptContextBuilder.BuildIdentitySystem(general);
        Assert.Contains("language=auto", autoIdentity, StringComparison.Ordinal);
        Assert.Contains(
            ConversationLanguagePolicy.PromptInstruction(ConversationLanguagePolicy.Auto),
            autoIdentity,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Respond in", general.SystemInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("[[speech:", general.SystemInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("[[md:", general.SystemInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("[[artifact:", general.SystemInstructions, StringComparison.Ordinal);
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents) && File.Exists(Path.Combine(agents, "examiner.json")))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }
}
