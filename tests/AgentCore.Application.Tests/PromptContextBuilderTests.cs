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
}
