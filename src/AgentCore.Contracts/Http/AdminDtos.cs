using System.Text.Json;

namespace AgentCore.Contracts.Http;

public sealed record AdminDefinitionInventoryResponse(IReadOnlyList<AdminDefinitionInventoryItemResponse> Items);

public sealed record AdminDefinitionInventoryItemResponse(
    string DefinitionId,
    int Version,
    string Source,
    string Status,
    string DisplayName,
    int DraftCount);

public sealed record AdminInstanceInventoryResponse(IReadOnlyList<AdminInstanceInventoryItemResponse> Items);

public sealed record AdminInstanceInventoryItemResponse(
    string InstanceId,
    string DefinitionId,
    int ActiveVersion,
    string Lifecycle,
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
    AdminPersonaResponse Persona,
    AdminProviderPreferencesResponse ProviderPreferences,
    AdminEffectiveModelResponse EffectiveModel,
    IReadOnlyList<string> EffectiveToolAllowlist,
    IReadOnlyList<string> HarnessReferences,
    string? WorkspaceTemplateId,
    IReadOnlyList<AdminKnowledgeSourceResponse> KnowledgeSources,
    AdminMemoryPolicyResponse MemoryPolicy,
    AdminTriggerPolicyResponse? TriggerPolicy,
    AdminDurableExecutionEligibilityResponse DurableExecutionEligibility,
    string? UnattendedModelCatalogKey = null,
    string? UnattendedReasoningEffort = null);

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

public sealed record AdminKnowledgeSourceResponse(
    string Identity,
    string Title,
    string Citation,
    string ResolvedResourcePath);

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

public sealed record AdminToolRegistryResponse(IReadOnlyList<string> ToolNames, int? MaxToolAllowlistEntries, IReadOnlyList<AdminCapabilityDescriptor>? Capabilities = null);
public sealed record AdminCapabilityDescriptor(string Name, string Category, string Summary, IReadOnlyList<string> Tags, bool Discoverable, string DefaultProjectionClass, bool Configured);

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

public sealed record AdminPublishedSkillResponse(
    string Id,
    string Name,
    IReadOnlyList<string> RequiredCapabilities);

public sealed record AdminDefinitionPublicationSummaryResponse(
    string DefinitionId,
    int Version,
    string Status,
    long MetadataRevision,
    string PublishedAt,
    IReadOnlyList<AdminPublishedSkillResponse> Skills);

public sealed record AdminCreateDefinitionDraftRequest(string DefinitionId, JsonElement Candidate);

public sealed record AdminCreateNewDefinitionDraftRequest(string DefinitionId);

public sealed record AdminInstanceDeleteRequest(long ExpectedRevision);

public sealed record AdminDefinitionDeleteRequest(
    IReadOnlyList<AdminDefinitionDraftRevisionRequest> Drafts,
    IReadOnlyList<AdminDefinitionPublicationRevisionRequest> Publications);

public sealed record AdminDefinitionDraftRevisionRequest(string DraftId, long Revision);

public sealed record AdminDefinitionPublicationRevisionRequest(int Version, long MetadataRevision);

public sealed record AdminAuthoringOptionsResponse(
    IReadOnlyList<string> LanguageModelAliases,
    IReadOnlyList<string> SpeechRecognizerAliases,
    IReadOnlyList<string> SpeechSynthesizerAliases,
    string? DefaultLanguageModelAlias,
    string? DefaultSpeechRecognizerAlias,
    string? DefaultSpeechSynthesizerAlias,
    string DefaultModelKey,
    IReadOnlyList<AdminAuthoringModelOptionResponse> Models,
    IReadOnlyList<string> InterruptionClassifiers);

public sealed record AdminAuthoringModelOptionResponse(
    string Key,
    string DisplayName,
    IReadOnlyList<string> SupportedReasoningEfforts,
    string? DefaultReasoningEffort);

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

public sealed record AdminBindDefinitionDraftResourcesRequest(
    long ExpectedRevision,
    IReadOnlyList<AdminBindDefinitionDraftResourceItemRequest> Items);

public sealed record AdminBindDefinitionDraftResourceItemRequest(
    string? ResourceId,
    string LogicalPath,
    string Kind,
    string MediaType,
    string ContentSha256,
    long ByteLength);

public sealed record AdminBindDefinitionDraftResourcesResponse(
    long Revision,
    IReadOnlyList<AdminDefinitionDraftResourceResponse> Items);

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

public sealed record AdminCreateAgentInstanceRequest(
    string DefinitionId,
    int Version,
    AdminPersonaResponse? Persona = null);

public sealed record AdminAgentInstanceResponse(
    string InstanceId,
    string DefinitionId,
    int ActiveVersion,
    string Lifecycle,
    long Revision,
    long PersonaRevision,
    AdminPersonaResponse Persona,
    string? UnattendedModelCatalogKey = null,
    string? UnattendedReasoningEffort = null);

public sealed record AdminSetUnattendedModelRequest(
    long ExpectedRevision,
    string? CatalogKey,
    string? ReasoningEffort);

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
    string UpdatedAt, string Status = "Active");

public sealed record AdminLearnedMemoryProvenanceResponse(
    string Source,
    string? OriginSessionId,
    string? OriginMemoryId,
    string RecordedAt, IReadOnlyList<string>? DerivedFromMemoryIds = null, string? MaintenanceOrigin = null,
    string? MaintenanceAgentInstanceId = null, string? MaintenanceSessionId = null, string? MaintenanceWorkItemId = null);

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
    AdminAutomationProvenanceResponse Provenance,
    string? ModelOverrideCatalogKey = null,
    string? ModelOverrideReasoningEffort = null,
    string ModelSource = "Conversation default");

public sealed record AdminSetRegistrationModelRequest(
    long ExpectedRevision,
    string? CatalogKey,
    string? ReasoningEffort);

public sealed record AdminCancelAutomationRegistrationRequest(long ExpectedRevision, bool Confirm);

public sealed record AdminEventListResponse(IReadOnlyList<AdminEventResponse> Items);

public sealed record AdminEventSourceResponse(
    string SourceId,
    string DisplayName,
    string Kind,
    string SourceKey,
    string Status,
    long Revision);

public sealed record AdminEventSourceListResponse(IReadOnlyList<AdminEventSourceResponse> Items);

public sealed record AdminCreateEventSourceRequest(string DisplayName);

public sealed record AdminEventSourceCredentialResponse(
    string SourceId,
    string SourceKey,
    string Token,
    string Status);

public sealed record AdminEventSubscriptionResponse(
    string RegistrationId,
    string SourceId,
    string EventType,
    string Status,
    long Revision);

public sealed record AdminEventSubscriptionListResponse(IReadOnlyList<AdminEventSubscriptionResponse> Items);

public sealed record AdminCreateEventSubscriptionRequest(string SourceId, string EventType);

public sealed record AdminEventResponse(
    string EventId,
    string OperationId,
    string OccurredAt,
    string ActorKind,
    string Operation,
    string TargetType,
    string TargetId,
    long? Revision,
    int? Version,
    JsonElement Summary);
