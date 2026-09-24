using AgentCore.Application.Memory;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public enum TriggerKind
{
    UserTurn,
    LongSilence,
    EnvironmentUpdate,
    UnfinishedInteraction,
    ScheduledOccurrence,
    ApplicationEvent
}

public sealed record AgentTrigger(Guid EventId, TriggerKind Kind, string? Text, string? EnvironmentKind = null);

public sealed record SessionAttachmentManifestItem(
    Guid AttachmentId,
    string DisplayName,
    string ContentType,
    long UploadedWithEntrySequence);

public sealed record AgentContext(
    AgentDefinition Definition,
    IReadOnlyList<Domain.Conversation.ConversationEntry> History,
    string Summary,
    Domain.Conversation.UserProfile? Profile,
    Domain.Conversation.SessionMode Mode,
    string? PendingTopic,
    bool HelpOfferedDuringSilence,
    string? InterruptedHeardText,
    AgentTrigger Trigger,
    IReadOnlyList<AttachmentProcessResult>? AttachmentContents = null,
    IReadOnlyList<SessionAttachmentManifestItem>? SessionAttachments = null,
    int ConsecutiveProactiveSpeaks = 0,
    int SilentEvaluations = 0,
    int SpeaksThisSilencePeriod = 0,
    bool InitiativeHeld = false,
    bool InactivityExceeded = false,
    bool ModelSupportsTools = true,
    DateTimeOffset UtcNow = default,
    DateTimeOffset? LastUserActivityAt = null,
    ILanguageModel? LanguageModel = null,
    string? ReasoningEffort = null,
    long SummarizedThroughEntrySequence = 0,
    long LastEntrySequence = 0,
    IReadOnlyList<Domain.Memory.StructuredMemoryItem>? LearnedMemories = null,
    AgentIdentity? Persona = null,
    ExplicitUserMemoryCaptureOutcome ExplicitMemoryCapture = ExplicitUserMemoryCaptureOutcome.None)
{
    public AgentIdentity EffectiveIdentity => Persona ?? Definition.Identity;
}

public abstract record AgentDecision;

public sealed record StaySilent(
    string Reason,
    bool CountsTowardSilentCap = true,
    int? NextWaitMs = null) : AgentDecision;

public sealed record Speak(
    ModelRequest Request,
    int? NextWaitMs = null,
    InitiativePlan? Plan = null) : AgentDecision;

public sealed record RequestDeactivate(string Reason) : AgentDecision;

public abstract record CompletionDecision;

public sealed record ContinueSession(string Reason) : CompletionDecision;

public sealed record RequestComplete(string Reason) : CompletionDecision;

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
