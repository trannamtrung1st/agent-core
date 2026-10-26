using System.Collections.ObjectModel;

namespace AgentCore.Domain.Models;

public enum ModelProviderKind { OpenAI, Anthropic, Google, OpenRouter, OpenAICompatible }
public enum ModelTransport { Responses, AnthropicMessages, GeminiContent, ChatCompletions }
public enum ModelInstructionHierarchy { Unsupported, NativeDeveloper, OrderedTrustedSections }
public enum ModelSemanticMode { NativeStructuredOutput, ResponseFunction }
public enum ModelMetadataSource { Unknown, NativeApi, VerifiedPreset, Manual, SyntheticFixture }
public enum ModelObservation { Unknown, Observed, NotReturned }

public sealed record ModelCapabilityProfile(bool Tools, bool Vision, ModelInstructionHierarchy Hierarchy,
    ModelSemanticMode SemanticMode, bool StructuredOutput, bool ForcedToolChoice,
    IReadOnlyList<string> ReasoningEfforts, string? DefaultReasoningEffort,
    ModelMetadataSource Source, DateTimeOffset? VerifiedAt = null);

public sealed record ModelPreset(Guid PresetId, ModelProviderKind Kind, string ExternalModelId, string Label,
    ModelTransport Transport, ModelCapabilityProfile Capabilities, bool Enabled, bool Archived, long Revision);

// Credential material is owned exclusively by the protected Credential aggregate.
public sealed record ProviderConnection(Guid ConnectionId, Guid TenantId, ModelProviderKind Kind, string Label,
    string Endpoint, Guid? CredentialId, bool NoCredential, bool Enabled, long Revision,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? LastTestAt = null, string? LastTestResult = null);

public sealed record ModelOffering(Guid OfferingId, Guid TenantId, Guid ConnectionId, ModelProviderKind Kind,
    Guid? PresetId, string ExternalModelId, string Label, ModelTransport Transport,
    ModelCapabilityProfile Capabilities, bool Enabled, bool Retired, long Revision,
    ModelObservation Observation = ModelObservation.Unknown, DateTimeOffset? LastSeenAt = null);

public sealed record TenantModelSettings(Guid TenantId, Guid? DefaultOfferingId, string? DefaultReasoningEffort, long Revision, AutoRoutingPolicy? Routing = null);

// Nonsecret, immutable dispatch witness. A credential/configuration edit cannot retarget it.
public sealed record ModelRoutePin(Guid TenantId, Guid OfferingId, long OfferingRevision,
    Guid ConnectionId, long ConnectionRevision, Guid? CredentialId, long? CredentialRevision,
    long? SecretRevision, ModelProviderKind Kind, string ExternalModelId, ModelTransport Transport);

public static class ModelManagementRules
{
    public static string Label(string value) => Text(value, 120, "Label");
    public static string ExternalId(string value) => Text(value, 255, "External model ID");
    private static string Text(string value, int max, string field)
    {
        value = value?.Trim() ?? "";
        if (value.Length < 1 || value.Length > max || value.Any(char.IsControl))
            throw new ArgumentException($"{field} must contain 1–{max} printable characters.");
        return value;
    }
    public static ModelCapabilityProfile Freeze(ModelCapabilityProfile value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Enum.IsDefined(value.Hierarchy) || !Enum.IsDefined(value.SemanticMode) || !Enum.IsDefined(value.Source)
            || value.ReasoningEfforts is null || value.ReasoningEfforts.Count > 12)
            throw new ArgumentException("Invalid model capability profile.");
        var efforts = value.ReasoningEfforts.Select(e => Text(e, 32, "Reasoning effort")).ToArray();
        if (efforts.Distinct(StringComparer.Ordinal).Count() != efforts.Length
            || (value.DefaultReasoningEffort is not null && !efforts.Contains(value.DefaultReasoningEffort, StringComparer.Ordinal))
            || efforts.Length > 0 && value.DefaultReasoningEffort is null
            || value.SemanticMode == ModelSemanticMode.ResponseFunction && (!value.Tools || !value.ForcedToolChoice))
            throw new ArgumentException("Invalid semantic-output or reasoning contract.");
        return value with { ReasoningEfforts = new ReadOnlyCollection<string>(efforts) };
    }
    public static void RequireTransport(ModelProviderKind kind, ModelTransport transport)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(transport) || !(kind switch
        {
            ModelProviderKind.OpenAI => transport == ModelTransport.Responses,
            ModelProviderKind.Anthropic => transport == ModelTransport.AnthropicMessages,
            ModelProviderKind.Google => transport == ModelTransport.GeminiContent,
            ModelProviderKind.OpenRouter => transport == ModelTransport.ChatCompletions,
            ModelProviderKind.OpenAICompatible => transport is ModelTransport.ChatCompletions or ModelTransport.Responses,
            _ => false
        })) throw new ArgumentException("The transport does not belong to this provider kind.");
    }
    public static ModelPreset Freeze(ModelPreset value)
    {
        if (value.PresetId == Guid.Empty || value.Revision < 1) throw new ArgumentException("Invalid model preset identity/revision.");
        RequireTransport(value.Kind, value.Transport);
        return value with { Label = Label(value.Label), ExternalModelId = ExternalId(value.ExternalModelId), Capabilities = Freeze(value.Capabilities) };
    }
    public static ProviderConnection Freeze(ProviderConnection value)
    {
        if (value.TenantId == Guid.Empty || value.ConnectionId == Guid.Empty || !Enum.IsDefined(value.Kind)
            || value.Revision < 1 || value.CredentialId == Guid.Empty || value.UpdatedAt < value.CreatedAt
            || value.NoCredential != (value.CredentialId is null)
            || value.NoCredential && value.Kind != ModelProviderKind.OpenAICompatible
            || !Uri.TryCreate(value.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || value.Endpoint.Length > 2048 || value.Endpoint.Any(char.IsControl)
            || value.LastTestResult is not (null or "metadata_available" or "synthetic_metadata_available" or "authentication_failed" or "metadata_unavailable"))
            throw new ArgumentException("Invalid provider connection or credential mode.");
        return value with { Label = Label(value.Label), Endpoint = uri.AbsoluteUri.TrimEnd('/') + "/" };
    }
    public static ModelOffering Freeze(ModelOffering value)
    {
        if (value.TenantId == Guid.Empty || value.OfferingId == Guid.Empty || value.ConnectionId == Guid.Empty
            || value.PresetId == Guid.Empty || value.Revision < 1 || value.Retired && value.Enabled || !Enum.IsDefined(value.Observation))
            throw new ArgumentException("Invalid model offering identity/state.");
        RequireTransport(value.Kind, value.Transport);
        return value with { Label = Label(value.Label), ExternalModelId = ExternalId(value.ExternalModelId), Capabilities = Freeze(value.Capabilities) };
    }
    public static TenantModelSettings Freeze(TenantModelSettings value)
    {
        value.Routing?.Validate();
        if (value.TenantId == Guid.Empty || value.DefaultOfferingId == Guid.Empty || value.Revision < 1
            || value.DefaultOfferingId is null && value.DefaultReasoningEffort is not null)
            throw new ArgumentException("Invalid tenant model default.");
        return value with { DefaultReasoningEffort = value.DefaultReasoningEffort is null ? null : Text(value.DefaultReasoningEffort, 32, "Reasoning effort") };
    }
}
