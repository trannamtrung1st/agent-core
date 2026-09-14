using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public enum TriggerKind { UserTurn, LongSilence, EnvironmentUpdate, UnfinishedInteraction }

public sealed record AgentTrigger(Guid EventId, TriggerKind Kind, string? Text, string? EnvironmentKind = null);

public sealed record AgentContext(
    AgentDefinition Definition,
    IReadOnlyList<Domain.Conversation.ConversationEntry> History,
    string Summary,
    Domain.Conversation.UserProfile? Profile,
    Domain.Conversation.SessionMode Mode,
    string? PendingTopic,
    bool HelpOfferedDuringSilence,
    string? InterruptedHeardText,
    AgentTrigger Trigger);

public abstract record AgentDecision;

public sealed record StaySilent(string Reason) : AgentDecision;

public sealed record Speak(ModelRequest Request) : AgentDecision;

public interface IAgentBrain
{
    ValueTask<AgentDecision> DecideAsync(
        AgentContext context,
        Guid responseId,
        CancellationToken cancellationToken = default);
}

public sealed record ProviderAliasSet(
    IReadOnlySet<string> LanguageModels,
    IReadOnlySet<string> SpeechRecognizers,
    IReadOnlySet<string> SpeechSynthesizers);

public static class SyntheticProviderAliases
{
    public static ProviderAliasSet Default { get; } = new(
        LanguageModels: new HashSet<string>(StringComparer.Ordinal) { "primary-llm" },
        SpeechRecognizers: new HashSet<string>(StringComparer.Ordinal) { "primary-stt" },
        SpeechSynthesizers: new HashSet<string>(StringComparer.Ordinal) { "primary-tts" });
}
