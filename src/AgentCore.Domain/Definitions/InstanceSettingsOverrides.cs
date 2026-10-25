using System.Text.Json.Serialization;
namespace AgentCore.Domain.Definitions;

/// <summary>A missing wrapper inherits; a present wrapper can carry an explicit null.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InstanceField<T>([property: JsonRequired] T Value);

/// <summary>Allowlisted sparse values. Persona, budgets and unattended selection retain their dedicated owners.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InstanceSettingsOverrides(
    InstanceField<string>? AgentInstructions = null,
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
    InstanceField<string?>? ModelOfferingId = null,
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
    InstanceField<string?>? ProviderSpeechRecognizer = null,
    InstanceField<string?>? ProviderSpeechSynthesizer = null,
    InstanceField<string>? ProviderInterruptionClassifier = null,
    InstanceField<string>? SandboxProfileId = null,
    InstanceField<string?>? SandboxInitializationScript = null,
    InstanceField<double>? SandboxCpu = null,
    InstanceField<int>? SandboxMemoryMiB = null,
    InstanceField<int>? SandboxMaxCommandSeconds = null,
    InstanceField<int>? SandboxIdleTtlSeconds = null,
    InstanceField<int>? SandboxMaxLifetimeSeconds = null,
    InstanceField<long>? SandboxStorageBytes = null,
    InstanceField<string[]>? SandboxAllowedProfileIds = null,
    InstanceField<string>? ConversationContextScope = null,
    InstanceField<string>? HomeAccess = null,
    InstanceField<string>? WorkingCleanup = null,
    InstanceField<string>? BrowserIsolation = null,
    InstanceField<string>? AgentStateVisibility = null,
    InstanceField<bool>? ExperienceRecord = null,
    InstanceField<bool>? ExperienceRecall = null,
    InstanceField<bool>? ProfileEvidence = null,
    InstanceField<bool>? CrossSessionContinuity = null)
{
    public InstanceSettingsOverrides Copy() => this with {
        SandboxAllowedProfileIds = SandboxAllowedProfileIds is { } profiles ? new(profiles.Value.ToArray()) : null,
        InitiativeTriggers = InitiativeTriggers is { } initiative ? new(initiative.Value.ToArray()) : null,
        SelectedCapabilities = SelectedCapabilities is { } selected ? new(selected.Value.ToArray()) : null,
        AlwaysCapabilities = AlwaysCapabilities is { } always ? new(always.Value.ToArray()) : null,
        AllowedSourceKinds = AllowedSourceKinds is { } sources ? new(sources.Value.ToArray()) : null
    };
}
