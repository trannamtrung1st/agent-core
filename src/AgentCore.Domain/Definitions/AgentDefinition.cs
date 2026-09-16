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
    IReadOnlyDictionary<string, string> Metadata,
    RoleEnvironment? Environment = null);

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
    IReadOnlyList<string> Triggers,
    int? MaxConsecutiveProactiveTurns = null,
    int? MaxSilentEvaluations = null,
    int? MaxInactivityMs = null)
{
    public const int DefaultConsecutiveProactiveTurns = 1;
    public const int DefaultSilentEvaluations = 8;
    public const int DefaultInactivityMs = 900_000;

    public int ConsecutiveCap => MaxConsecutiveProactiveTurns ?? DefaultConsecutiveProactiveTurns;

    public int SilentEvaluationCap =>
        MaxSilentEvaluations is > 0 ? MaxSilentEvaluations.Value : DefaultSilentEvaluations;

    public int InactivityLimitMs =>
        MaxInactivityMs is > 0 ? MaxInactivityMs.Value : DefaultInactivityMs;
}

public sealed record VoiceConfiguration(bool Enabled, string VoiceId, double SpeakingRate);

public sealed record ProviderPreferences(
    string LanguageModel,
    string? SpeechRecognizer,
    string? SpeechSynthesizer,
    string InterruptionClassifier = "heuristic");
