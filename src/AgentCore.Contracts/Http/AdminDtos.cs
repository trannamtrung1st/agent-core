using System.Text.Json;

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
    long InstanceRevision,
    long PersonaRevision,
    bool Compatibility,
    AdminPersonaResponse Persona,
    AdminProviderPreferencesResponse ProviderPreferences,
    AdminEffectiveModelResponse EffectiveModel,
    IReadOnlyList<string> EffectiveToolAllowlist,
    IReadOnlyList<string> HarnessReferences,
    string? WorkspaceTemplateId,
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

public sealed record AdminEffectiveModelResponse(
    string CatalogKey,
    string DisplayName,
    string SelectionSource,
    string? ReasoningEffort,
    string? ModelId);

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
    bool AllowsScheduleSource,
    bool AllowsApplicationEventSource,
    bool CanAcceptNewTriggeredWork);

public sealed record AdminToolRegistryResponse(IReadOnlyList<string> ToolNames);

public sealed record AdminDefinitionDraftListResponse(IReadOnlyList<AdminDefinitionDraftSummaryResponse> Items);

public sealed record AdminDefinitionDraftSummaryResponse(
    string DraftId,
    string DefinitionId,
    long Revision,
    string SourceKind,
    int? SourceVersion,
    string UpdatedAt);

public sealed record AdminDefinitionDraftResponse(
    string DraftId,
    string DefinitionId,
    long Revision,
    string SourceKind,
    int? SourceVersion,
    string CreatedAt,
    string UpdatedAt,
    JsonElement Candidate);

public sealed record AdminDefinitionPublicationListResponse(
    IReadOnlyList<AdminDefinitionPublicationSummaryResponse> Items);

public sealed record AdminDefinitionPublicationSummaryResponse(
    string DefinitionId,
    int Version,
    string Status,
    long MetadataRevision,
    string PublishedAt);

public sealed record AdminCreateDefinitionDraftRequest(string DefinitionId, JsonElement Candidate);

public sealed record AdminForkDefinitionDraftRequest(string DefinitionId, int SourceVersion, string SourceKind);

public sealed record AdminUpdateDefinitionDraftRequest(long ExpectedRevision, JsonElement Candidate);

public sealed record AdminPublishDefinitionDraftRequest(long ExpectedRevision);

public sealed record AdminDefinitionDraftValidationResponse(
    string DraftId,
    long DraftRevision,
    string ConfigurationFingerprint,
    bool HasBlockingFindings,
    IReadOnlyList<AdminDefinitionValidationFindingResponse> Findings);

public sealed record AdminUpsertDefinitionEvaluationScenarioRequest(
    long ExpectedRevision,
    string ScenarioId,
    string Title,
    string Prompt,
    string RequirementLevel,
    string CheckType,
    string? ToolName);

public sealed record AdminDefinitionEvaluationScenarioResponse(
    string ScenarioId,
    int ScenarioVersion,
    string Title,
    string Prompt,
    string RequirementLevel,
    string CheckType,
    string? ToolName,
    string UpdatedAt);

public sealed record AdminDefinitionEvaluationResultResponse(
    string DraftId,
    long DraftRevision,
    string ConfigurationFingerprint,
    string ScenarioId,
    int ScenarioVersion,
    string RuntimeKind,
    bool Passed,
    IReadOnlyList<string> Findings,
    string RecordedAt);

public sealed record AdminDefinitionDraftDiffResponse(
    string DraftId,
    long DraftRevision,
    string BaselineKind,
    int? BaselineVersion,
    IReadOnlyList<AdminDefinitionDiffSectionResponse> Sections);

public sealed record AdminDefinitionDiffSectionResponse(
    string SectionId,
    string Label,
    string ChangeKind,
    string? BeforeSummary,
    string? AfterSummary);

public sealed record AdminDefinitionValidationFindingResponse(
    string Field,
    string Code,
    string Message,
    string Severity);

public sealed record AdminDeprecateDefinitionPublicationRequest(long ExpectedMetadataRevision);

public sealed record AdminDefinitionDraftResourceListResponse(
    IReadOnlyList<AdminDefinitionDraftResourceResponse> Items);

public sealed record AdminDefinitionDraftResourceResponse(
    string ResourceId,
    string LogicalPath,
    string Kind,
    string MediaType,
    string ContentSha256,
    long ByteLength,
    string UpdatedAt);

public sealed record AdminDefinitionResourceContentStoredResponse(
    string ContentSha256,
    long ByteLength,
    string MediaType);

public sealed record AdminUpsertDefinitionDraftResourceRequest(
    long ExpectedRevision,
    string? ResourceId,
    string LogicalPath,
    string Kind,
    string MediaType,
    string ContentSha256,
    long ByteLength);

public sealed record AdminRemoveDefinitionDraftResourceRequest(long ExpectedRevision);

public sealed record AdminDefinitionPublicationResourceListResponse(
    IReadOnlyList<AdminDefinitionPublicationResourceResponse> Items);

public sealed record AdminDefinitionPublicationResourceResponse(
    string ResourceId,
    string LogicalPath,
    string Kind,
    string MediaType,
    string ContentSha256,
    long ByteLength);

public sealed record AdminCreateAgentInstanceRequest(string DefinitionId, int Version);

public sealed record AdminAgentInstanceResponse(
    string InstanceId,
    string DefinitionId,
    int ActiveVersion,
    bool Compatibility,
    string Lifecycle,
    long Revision,
    long PersonaRevision);

public sealed record AdminUpdateAgentInstancePersonaRequest(
    long ExpectedRevision,
    long ExpectedPersonaRevision,
    string Name,
    string Role,
    string Description,
    string Tone);

public sealed record AdminUpdateAgentInstanceLifecycleRequest(long ExpectedRevision, string Lifecycle);

public sealed record AdminReassociateAgentInstanceVersionRequest(long ExpectedRevision, int Version);

public sealed record AdminLearnedMemoryListResponse(
    string Scope,
    IReadOnlyList<AdminLearnedMemoryItemResponse> Items);

public sealed record AdminLearnedMemoryItemResponse(
    string MemoryId,
    string Kind,
    string Subject,
    string Content,
    AdminLearnedMemoryProvenanceResponse Provenance,
    string UpdatedAt);

public sealed record AdminLearnedMemoryProvenanceResponse(
    string Source,
    string? OriginSessionId,
    string? OriginMemoryId,
    string RecordedAt);

public sealed record AdminLearnedMemoryResetRequest(string Scope, string? SessionId, bool Confirm);

public sealed record AdminLearnedMemoryResetResponse(string Scope, int ItemsRemoved);

public sealed record AdminAutomationRegistrationListResponse(
    IReadOnlyList<AdminAutomationRegistrationResponse> Items);

public sealed record AdminAutomationProvenanceResponse(
    string AuthorizationOrigin,
    string? SourceSessionId,
    string CreatedAt,
    string UpdatedAt);

public sealed record AdminAutomationRegistrationResponse(
    string RegistrationId,
    string Intent,
    string Status,
    string ScheduleKind,
    string TimeZoneId,
    string ScheduleSummary,
    string? NextOccurrenceAtUtc,
    long Revision,
    string? SuspensionReason,
    AdminAutomationProvenanceResponse Provenance);

public sealed record AdminCancelAutomationRegistrationRequest(long ExpectedRevision, bool Confirm);
