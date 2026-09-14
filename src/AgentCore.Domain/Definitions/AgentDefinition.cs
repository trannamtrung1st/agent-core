namespace AgentCore.Domain.Definitions;

public sealed record AgentDefinition(
    int SchemaVersion,
    string Id,
    int Version,
    AgentIdentity Identity,
    IReadOnlyList<string> Goals,
    string SystemInstructions,
    BehaviorPolicy BehaviorPolicy,
    ConversationPolicy ConversationPolicy,
    InitiativePolicy InitiativePolicy,
    VoiceConfiguration Voice,
    ProviderPreferences ProviderPreferences,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record AgentIdentity(string Name, string Role, string Description, string Tone);

public sealed record BehaviorPolicy(
    string InterruptionStyle,
    bool AcknowledgeInterruption,
    bool AvoidUnsupportedClaims);

public sealed record ConversationPolicy(
    string ResponseLength,
    bool AskOneQuestionAtATime,
    string Language,
    int MaxOutputTokens);

public sealed record InitiativePolicy(
    bool Enabled,
    int SilenceThresholdMs,
    int CooldownMs,
    int MaxPerSilencePeriod,
    IReadOnlyList<string> Triggers);

public sealed record VoiceConfiguration(bool Enabled, string VoiceId, double SpeakingRate);

public sealed record ProviderPreferences(
    string LanguageModel,
    string? SpeechRecognizer,
    string? SpeechSynthesizer,
    string InterruptionClassifier = "heuristic");
