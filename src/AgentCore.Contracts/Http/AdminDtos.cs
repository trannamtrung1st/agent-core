namespace AgentCore.Contracts.Http;

public sealed record AdminDefinitionInventoryResponse(IReadOnlyList<AdminDefinitionInventoryItemResponse> Items);

public sealed record AdminDefinitionInventoryItemResponse(
    string DefinitionId,
    int Version,
    string Source,
    string Status,
    string DisplayName);

public sealed record AdminInstanceInventoryResponse(IReadOnlyList<AdminInstanceInventoryItemResponse> Items);

public sealed record AdminInstanceInventoryItemResponse(
    string InstanceId,
    string DefinitionId,
    int ActiveVersion,
    string Lifecycle,
    bool Compatibility,
    string PersonaName,
    string CreatedAt,
    string UpdatedAt);

public sealed record AdminEffectiveConfigurationResponse(
    string DefinitionSource,
    string DefinitionId,
    int DefinitionVersion,
    string DefinitionStatus,
    string InstanceId,
    string InstanceLifecycle,
    bool Compatibility,
    AdminPersonaResponse Persona,
    AdminProviderPreferencesResponse ProviderPreferences,
    AdminModelDefaultsResponse? ModelDefaults,
    IReadOnlyList<string> EffectiveToolAllowlist,
    IReadOnlyList<AdminKnowledgeSourceResponse> KnowledgeSources,
    AdminMemoryPolicyResponse MemoryPolicy,
    AdminTriggerPolicyResponse? TriggerPolicy,
    AdminDurableExecutionEligibilityResponse DurableExecutionEligibility);

public sealed record AdminPersonaResponse(string Name, string Role, string Description, string Tone);

public sealed record AdminProviderPreferencesResponse(
    string LanguageModel,
    string? SpeechRecognizer,
    string? SpeechSynthesizer,
    string InterruptionClassifier);

public sealed record AdminModelDefaultsResponse(string? CatalogKey, string? ReasoningEffort);

public sealed record AdminKnowledgeSourceResponse(string Identity, string Title, string Citation);

public sealed record AdminMemoryPolicyResponse(
    bool SessionMemory,
    bool IdentityUserPromotion,
    bool IdentityUserRetrieval,
    bool UserPromotion,
    bool UserRetrieval);

public sealed record AdminTriggerPolicyResponse(
    bool Enabled,
    bool AllowUserScheduling,
    bool AllowOneShot,
    bool AllowDaily,
    bool AllowWeekly,
    bool AllowIndefiniteRecurrence,
    int MaxActiveRegistrations,
    int OneShotHorizonDays,
    int MinRecurrenceDays,
    IReadOnlyList<string> AllowedSourceKinds,
    bool AllowFixedInterval,
    int MinFixedIntervalSeconds);

public sealed record AdminDurableExecutionEligibilityResponse(
    bool InstanceActive,
    bool DefinitionResolved,
    bool TriggerPolicyEnabled,
    bool CanAcceptNewTriggeredWork);
