namespace AgentCore.Domain.Definitions;

/// <summary>A missing wrapper inherits; a present wrapper can carry an explicit null.</summary>
public sealed record InstanceField<T>(T Value);

/// <summary>Allowlisted sparse values. Persona, budgets and unattended selection retain their dedicated owners.</summary>
public sealed record InstanceSettingsOverrides(
    InstanceField<string>? SystemInstructions = null,
    InstanceField<string>? ResponseLength = null,
    InstanceField<bool>? AskOneQuestionAtATime = null,
    InstanceField<string>? Language = null,
    InstanceField<int>? MaxOutputTokens = null,
    InstanceField<string>? InterruptionStyle = null,
    InstanceField<bool>? AcknowledgeInterruption = null,
    InstanceField<bool>? AvoidUnsupportedClaims = null,
    InstanceField<bool>? InitiativeEnabled = null,
    InstanceField<int>? SilenceThresholdMs = null,
    InstanceField<int>? CooldownMs = null,
    InstanceField<int>? MaxPerSilencePeriod = null,
    InstanceField<string[]>? InitiativeTriggers = null,
    InstanceField<int?>? MaxConsecutiveProactiveTurns = null,
    InstanceField<int?>? MaxSilentEvaluations = null,
    InstanceField<int?>? MaxInactivityMs = null,
    InstanceField<bool>? VoiceEnabled = null,
    InstanceField<string>? VoiceId = null,
    InstanceField<double>? SpeakingRate = null,
    InstanceField<string?>? ModelCatalogKey = null,
    InstanceField<string?>? ReasoningEffort = null,
    InstanceField<bool>? MemorySessionMemory = null,
    InstanceField<bool>? MemoryIdentityUserPromotion = null,
    InstanceField<bool>? MemoryIdentityUserRetrieval = null,
    InstanceField<bool>? MemoryUserPromotion = null,
    InstanceField<bool>? MemoryUserRetrieval = null,
    InstanceField<string[]>? SelectedCapabilities = null,
    InstanceField<string[]>? AlwaysCapabilities = null,
    InstanceField<bool>? AllowUnreadUnsupportedTypes = null,
    InstanceField<bool>? TriggerEnabled = null,
    InstanceField<bool>? AllowUserScheduling = null,
    InstanceField<bool>? AllowOneShot = null,
    InstanceField<bool>? AllowDaily = null,
    InstanceField<bool>? AllowWeekly = null,
    InstanceField<bool>? AllowIndefiniteRecurrence = null,
    InstanceField<int>? MaxActiveRegistrations = null,
    InstanceField<int>? OneShotHorizonDays = null,
    InstanceField<int>? MinRecurrenceDays = null,
    InstanceField<string[]>? AllowedSourceKinds = null,
    InstanceField<bool>? AllowFixedInterval = null,
    InstanceField<int>? MinFixedIntervalSeconds = null,
    InstanceField<string>? ProviderLanguageModel = null,
    InstanceField<string?>? ProviderSpeechRecognizer = null,
    InstanceField<string?>? ProviderSpeechSynthesizer = null,
    InstanceField<string>? ProviderInterruptionClassifier = null)
{
    public InstanceSettingsOverrides Copy() => this with {
        InitiativeTriggers = InitiativeTriggers is { } initiative ? new(initiative.Value.ToArray()) : null,
        SelectedCapabilities = SelectedCapabilities is { } selected ? new(selected.Value.ToArray()) : null,
        AlwaysCapabilities = AlwaysCapabilities is { } always ? new(always.Value.ToArray()) : null,
        AllowedSourceKinds = AllowedSourceKinds is { } sources ? new(sources.Value.ToArray()) : null
    };
}
