using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class PromptContextBuilderTests
{
    [Fact]
    public void Voice_mode_system_prompt_requires_explicit_speech_projection_first()
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
            .First(text => text.Contains("Voice output contract:", StringComparison.Ordinal));
        Assert.Contains("[[speech:", modeSystem, StringComparison.Ordinal);
        Assert.Contains("MUST begin", modeSystem, StringComparison.Ordinal);
        Assert.Contains("only content intended for TTS", modeSystem, StringComparison.Ordinal);
        Assert.Contains("visual-only", modeSystem, StringComparison.Ordinal);
        Assert.DoesNotContain("Absent [[speech:...]], display prose is the spoken answer", modeSystem, StringComparison.Ordinal);
    }

    [Fact]
    public void Voice_mode_system_prompt_describes_speech_display_composition()
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
            .First(text => text.Contains("Voice output contract:", StringComparison.Ordinal));

        Assert.Contains(PromptContextBuilder.VoiceModeOutputGuidance, modeSystem, StringComparison.Ordinal);

        Assert.Contains("semantically the same", modeSystem, StringComparison.Ordinal);
        Assert.Contains("several sentences long", modeSystem, StringComparison.Ordinal);
        Assert.Contains("tables", modeSystem, StringComparison.Ordinal);
        Assert.Contains("shorter or simpler than the display", modeSystem, StringComparison.Ordinal);
        Assert.Contains("substantially more detailed than display", modeSystem, StringComparison.Ordinal);
        Assert.Contains("Here is the summary", modeSystem, StringComparison.Ordinal);
        Assert.Contains("label or caption", modeSystem, StringComparison.Ordinal);
        Assert.Contains("visually rich", modeSystem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Text_mode_system_prompt_omits_voice_composition_guidance()
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
        Assert.DoesNotContain("Voice output contract:", combined, StringComparison.Ordinal);
        Assert.DoesNotContain(PromptContextBuilder.VoiceModeOutputGuidance, combined, StringComparison.Ordinal);
    }
}
