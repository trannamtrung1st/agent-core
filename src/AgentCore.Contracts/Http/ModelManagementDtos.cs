using System.Text.Json.Serialization;

namespace AgentCore.Contracts.Http;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelCapabilityInput([property: JsonRequired] bool Tools, [property: JsonRequired] bool Vision,
    string Hierarchy, string SemanticMode, [property: JsonRequired] bool StructuredOutput, [property: JsonRequired] bool ForcedToolChoice,
    IReadOnlyList<string> ReasoningEfforts, string? DefaultReasoningEffort);
public sealed record ModelCapabilityDto(bool Tools, bool Vision, string Hierarchy, string SemanticMode,
    bool StructuredOutput, bool ForcedToolChoice, IReadOnlyList<string> ReasoningEfforts, string? DefaultReasoningEffort,
    string Source, DateTimeOffset? VerifiedAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelConnectionRequest(string Kind, string Label, string? Endpoint, Guid? CredentialId,
    [property: JsonRequired] bool NoCredential, [property: JsonRequired] bool Enabled,
    [property: JsonRequired] long ExpectedRevision, [property: JsonRequired] long ExpectedConnectionRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelOfferingRequest(Guid ConnectionId, Guid? PresetId, string ExternalModelId, string Label, string Transport,
    ModelCapabilityInput Capabilities, [property: JsonRequired] bool Enabled, [property: JsonRequired] bool Retired,
    [property: JsonRequired] long ExpectedRevision, [property: JsonRequired] long ExpectedOfferingRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TenantModelDefaultRequest([property: JsonRequired] Guid? OfferingId, string? ReasoningEffort,
    [property: JsonRequired] long ExpectedRevision, [property: JsonRequired] long ExpectedSettingsRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelMetadataTestRequest([property: JsonRequired] long ExpectedRevision, [property: JsonRequired] long ExpectedConnectionRevision);
public sealed record ModelMetadataTestDto(bool MetadataAvailable, string Result, int ModelsObserved, DateTimeOffset TestedAt);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModelPresetRequest(string Kind, string ExternalModelId, string Label, string Transport, ModelCapabilityInput Capabilities,
    [property: JsonRequired] bool Enabled, [property: JsonRequired] bool Archived, [property: JsonRequired] long ExpectedRevision);
public sealed record ModelPresetDto(Guid PresetId, string Kind, string ExternalModelId, string Label, string Transport,
    ModelCapabilityDto Capabilities, bool Enabled, bool Archived, long Revision, int? OfferingReferences = null);
public sealed record ModelConnectionDto(Guid ConnectionId, Guid TenantId, string Kind, string Label, string Endpoint,
    Guid? CredentialId, bool NoCredential, bool Enabled, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? LastTestAt, string? LastTestResult);
public sealed record ModelOfferingDto(Guid OfferingId, Guid TenantId, Guid ConnectionId, string Kind, Guid? PresetId,
    string ExternalModelId, string Label, string Transport, ModelCapabilityDto Capabilities, bool Enabled, bool Retired,
    long Revision, string Observation, DateTimeOffset? LastSeenAt);
public sealed record TenantModelSettingsDto(Guid TenantId, Guid? DefaultOfferingId, string? DefaultReasoningEffort, long Revision);
public sealed record ModelAdministrationAccessDto(bool CanCreateConnection, bool CanCreateOffering, bool CanManageDefault,
    IReadOnlyList<Guid> ManageConnectionIds, IReadOnlyList<Guid> TestConnectionIds, IReadOnlyList<Guid> ManageOfferingIds);
public sealed record ModelOfferingAvailabilityDto(Guid OfferingId, bool Available, string Reason);
public sealed record TenantModelAdminDto(IReadOnlyList<ModelConnectionDto> Connections, IReadOnlyList<ModelOfferingDto> Offerings,
    TenantModelSettingsDto Settings, IReadOnlyList<ModelPresetDto> Presets, long SecurityRevision, IReadOnlyList<ModelOfferingAvailabilityDto> Availability, ModelAdministrationAccessDto Access);
