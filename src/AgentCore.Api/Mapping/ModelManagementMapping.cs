using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Models;

namespace AgentCore.Api.Mapping;

internal static class ModelManagementMapping
{
    internal static ModelPreferenceDto? Map(ModelPreference? p) => p is null ? null : new(p.Mode, p.OfferingId, p.Reasoning is null ? null : new(p.Reasoning.Mode, p.Reasoning.Effort));
    internal static ModelPreference Input(ModelPreferenceDto? p)
    {
        var value = p is null ? ModelPreference.Inherit : new ModelPreference(p.Mode, p.OfferingId,
            p.Reasoning is null ? null : new(p.Reasoning.Mode, p.Reasoning.Effort));
        try { value.Validate(); } catch (ArgumentException error) { throw AgentCoreErrors.Validation(error.Message); }
        return value;
    }
    internal static T Parse<T>(string value) where T : struct, Enum => Enum.TryParse<T>(value, false, out var result)
        && Enum.IsDefined(result) && Enum.GetNames<T>().Contains(value, StringComparer.Ordinal)
        ? result : throw AgentCoreErrors.Validation("Unknown model provider, transport or capability value.");
    internal static ModelCapabilityProfile Input(ModelCapabilityInput? c) => c is null
        ? throw AgentCoreErrors.Validation("Declare the model's capabilities.")
        : new(c.Tools, c.Vision, Parse<ModelInstructionHierarchy>(c.Hierarchy), Parse<ModelSemanticMode>(c.SemanticMode), c.StructuredOutput,
            c.ForcedToolChoice, c.ReasoningEfforts, c.DefaultReasoningEffort, ModelMetadataSource.Manual);
    internal static ModelCapabilityDto Map(ModelCapabilityProfile c) => new(c.Tools, c.Vision, c.Hierarchy.ToString(), c.SemanticMode.ToString(),
        c.StructuredOutput, c.ForcedToolChoice, c.ReasoningEfforts, c.DefaultReasoningEffort, c.Source.ToString(), c.VerifiedAt);
    internal static ModelPresetDto Map(ModelPreset p) => new(p.PresetId, p.Kind.ToString(), p.ExternalModelId, p.Label, p.Transport.ToString(), Map(p.Capabilities), p.Enabled, p.Archived, p.Revision);
    internal static ModelPresetDto Map(ModelPresetSummary p) => Map(p.Preset) with { OfferingReferences = p.OfferingReferences };
    internal static ModelConnectionDto Map(ProviderConnection c) => new(c.ConnectionId, c.TenantId, c.Kind.ToString(), c.Label, c.Endpoint,
        c.CredentialId, c.NoCredential, c.Enabled, c.Revision, c.CreatedAt, c.UpdatedAt, c.LastTestAt, c.LastTestResult);
    internal static ModelOfferingDto Map(ModelOffering o) => new(o.OfferingId, o.TenantId, o.ConnectionId, o.Kind.ToString(), o.PresetId,
        o.ExternalModelId, o.Label, o.Transport.ToString(), Map(o.Capabilities), o.Enabled, o.Retired, o.Revision, o.Observation.ToString(), o.LastSeenAt);
    internal static TenantModelSettingsDto Map(TenantModelSettings s) => new(s.TenantId, s.DefaultOfferingId, s.DefaultReasoningEffort, s.Revision, new((s.Routing ?? new()).Enabled, (s.Routing ?? new()).AllowConfiguredDefault, (s.Routing ?? new()).MinimumProbability));
    internal static TenantModelAdminDto Map(TenantModelAdminView v) => new(v.Connections.Select(Map).ToArray(), v.Offerings.Select(Map).ToArray(), Map(v.Settings), v.Presets.Select(Map).ToArray(), v.SecurityRevision, v.Availability.Select(a => new ModelOfferingAvailabilityDto(a.OfferingId, a.Available, a.Reason, a.Observations is { } o ? new(o.SampleCount, o.ErrorCount, o.From, o.To, o.P50Milliseconds, o.P95Milliseconds, o.OfferingRevision, o.ConnectionRevision, o.Scope) : null)).ToArray(), new(v.Access.CanCreateConnection, v.Access.CanCreateOffering, v.Access.CanManageDefault,
        v.Access.ManageConnectionIds, v.Access.TestConnectionIds, v.Access.ManageOfferingIds));
    internal static ModelMetadataTestDto Map(ModelMetadataTestView v) => new(v.MetadataAvailable, v.Result, v.ModelsObserved, v.TestedAt);
    internal static ProviderConnectionChange Input(ModelConnectionRequest c) => new(Parse<ModelProviderKind>(c.Kind), c.Label, c.Endpoint, c.CredentialId, c.NoCredential, c.Enabled);
    internal static ModelOfferingChange Input(ModelOfferingRequest o) => new(o.ConnectionId, o.PresetId, o.ExternalModelId, o.Label, Parse<ModelTransport>(o.Transport), Input(o.Capabilities), o.Enabled, o.Retired, o.RestoreAvailability);
}
