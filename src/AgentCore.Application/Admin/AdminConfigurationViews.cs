using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed record AdminKnowledgeSource(
    string Identity,
    string Title,
    string Citation,
    string ResolvedResourcePath);

public sealed record AdminEffectiveModel(
    string OfferingId,
    string DisplayName,
    string SelectionSource,
    string? ReasoningEffort,
    string? ExternalModelId,
    bool Available = true,
    string? UnavailableReason = null);

public sealed record AdminDurableExecutionEligibility(
    bool InstanceActive,
    bool DefinitionResolved,
    bool TriggerPolicyEnabled,
    bool AllowsScheduleSource,
    bool AllowsApplicationEventSource,
    bool CanAcceptNewTriggeredWork);

public sealed record AdminEffectiveConfiguration(
    string DefinitionSource,
    string DefinitionId,
    int DefinitionVersion,
    string DefinitionStatus,
    Guid InstanceId,
    string InstanceLifecycle,
    long InstanceRevision,
    long PersonaRevision,
    AgentIdentity Persona,
    ProviderPreferences ProviderPreferences,
    AdminEffectiveModel EffectiveModel,
    IReadOnlyList<string> EffectiveToolAllowlist,
    IReadOnlyList<string> HarnessReferences,
    string? WorkspaceTemplateId,
    IReadOnlyList<AdminKnowledgeSource> KnowledgeSources,
    MemoryPolicy MemoryPolicy,
    TriggerPolicy? TriggerPolicy,
    AdminDurableExecutionEligibility DurableExecutionEligibility,
    string? UnattendedOfferingId = null,
    string? UnattendedReasoningEffort = null,
    BrowserEffectiveConfiguration? Browser = null);

public sealed record BrowserEffectiveConfiguration(string ProviderId, string DisplayName, bool Enabled, bool Ready, string ProfileMode, string PolicyMode, IReadOnlyList<string> SupportedFeatures, int MaxSnapshotBytes, int MaxCaptureBytes, int MaxDownloadBytes, string Engine = "unknown", BrowserOperationalLimits? Limits = null);
