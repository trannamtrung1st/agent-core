using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Models;

public static class TenantModelRules
{
    public static void ApiKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new ArgumentException("An inference API key must contain 1–8192 printable characters without whitespace.");
    }
    public static AccessScope Scope(Guid tenant, Guid id, bool connection = false) =>
        AccessScope.ForResource(tenant, connection ? "model-connection" : "model-offering", id.ToString("D"));
    public static bool TryId(AccessScope scope, out Guid id)
    {
        id = Guid.Empty;
        return scope.Kind == AccessScopeKind.Resource && scope.TenantId is { } tenant && tenant != Guid.Empty
            && scope.ResourceType is "model-connection" or "model-offering"
            && Guid.TryParseExact(scope.ResourceId, "D", out id) && id != Guid.Empty && scope.ResourceId == id.ToString("D");
    }
    public static void RequireCredential(ProviderConnection connection, TenantCredential? credential, bool requireActive = true)
    {
        if (connection.NoCredential) return;
        if (credential is null || credential.Owner.TenantId != connection.TenantId || credential.Owner.DeletedAt is not null
            || credential.Value.CredentialId != connection.CredentialId || credential.Value.Purpose != CredentialPurpose.ModelInference
            || credential.Value.Kind != CredentialKind.ApiKey || requireActive && credential.Value.Status != CredentialStatus.Active)
            throw new AccessDeniedException("model_credential_unavailable");
    }
    public static void ConnectionChange(ProviderConnection value, ProviderConnection? old, long expected)
    {
        if (expected < 0 || value.Revision != checked(expected + 1)
            || old is not null && (old.ConnectionId != value.ConnectionId || old.TenantId != value.TenantId
                || old.Kind != value.Kind || old.CreatedAt != value.CreatedAt))
            throw new ArgumentException("Provider connection identity/kind is immutable.");
    }
    public static void OfferingChange(ModelOffering value, ModelOffering? old, long expected, ProviderConnection? connection, ModelPreset? preset)
    {
        if (expected < 0 || value.Revision != checked(expected + 1) || connection is null
            || connection.TenantId != value.TenantId || connection.ConnectionId != value.ConnectionId || connection.Kind != value.Kind
            || value.PresetId is not null && (preset is null || preset.PresetId != value.PresetId || preset.Kind != value.Kind || preset.ExternalModelId != value.ExternalModelId)
            || old is not null && (old.TenantId != value.TenantId || old.ConnectionId != value.ConnectionId || old.ExternalModelId != value.ExternalModelId
                || old.Kind != value.Kind || old.PresetId != value.PresetId || old.Retired && !value.Retired))
            throw new ArgumentException("Model offering requires its exact owned connection/preset; its identity is immutable.");
    }
    public static void RequireDefault(TenantModelSettings settings, ModelOffering? offering, ProviderConnection? connection, TenantCredential? credential)
    {
        if (settings.DefaultOfferingId is null) return;
        if (offering is null || offering.TenantId != settings.TenantId || offering.OfferingId != settings.DefaultOfferingId
            || !offering.Enabled || offering.Retired || offering.Observation == ModelObservation.NotReturned
            || connection is null || !connection.Enabled || connection.TenantId != settings.TenantId || offering.ConnectionId != connection.ConnectionId
            || offering.Capabilities.Hierarchy == ModelInstructionHierarchy.Unsupported || !offering.Capabilities.Tools
            || offering.Capabilities.Source is ModelMetadataSource.Unknown or ModelMetadataSource.SyntheticFixture
            || offering.Capabilities.SemanticMode == ModelSemanticMode.NativeStructuredOutput && !offering.Capabilities.StructuredOutput
            || settings.DefaultReasoningEffort is { } effort && !offering.Capabilities.ReasoningEfforts.Contains(effort, StringComparer.Ordinal))
            throw new SecurityConflictException("model_default_unavailable");
        RequireCredential(connection, credential);
    }
    public static void Audit(HumanSessionReference human, AuthorizationAudit audit, Guid? tenant, string action, Guid id)
    {
        if (audit.ActorPrincipalId != human.PrincipalId || audit.TenantId != tenant || audit.Action != action
            || audit.Target != id.ToString("D") || audit.Decision != "allowed")
            throw new ArgumentException("Invalid model administration audit.");
    }
}
