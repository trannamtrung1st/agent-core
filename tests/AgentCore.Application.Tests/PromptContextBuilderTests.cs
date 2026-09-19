using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

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
        Assert.Contains("optional", modeSystem, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("only content intended for TTS", modeSystem, StringComparison.Ordinal);
        Assert.Contains("visual-only", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("MUST begin", modeSystem, StringComparison.Ordinal);
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
}
