using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;

namespace AgentCore.Application.Tests;

public sealed class PromptContextBuilderTests
{
    [Fact]
    public void Voice_mode_system_prompt_keeps_marker_compatibility()
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
            .First(text => text.Contains("Voice compatibility", StringComparison.Ordinal));
        Assert.Contains(PromptContextBuilder.VoiceModeOutputGuidance, modeSystem, StringComparison.Ordinal);
        Assert.Contains("[[speech:", modeSystem, StringComparison.Ordinal);
        Assert.Contains("only streaming TTS source", modeSystem, StringComparison.Ordinal);
        Assert.Contains("spoken fallback", modeSystem, StringComparison.Ordinal);
        Assert.Contains("visual-only", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("MUST begin", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("Never rely on display prose", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("Absent [[speech:...]], display prose is the spoken answer", modeSystem, StringComparison.Ordinal);
    }

    [Fact]
    public void Voice_mode_system_prompt_does_not_teach_composition_heuristics()
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
            .First(text => text.Contains("Voice compatibility", StringComparison.Ordinal));

        Assert.DoesNotContain("semantically the same", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("several sentences long", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("shorter or simpler than the display", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("Here is the summary", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("Examples (GOOD", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("Examples (BAD", modeSystem, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_mode_system_prompt_omits_voice_marker_compatibility()
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
