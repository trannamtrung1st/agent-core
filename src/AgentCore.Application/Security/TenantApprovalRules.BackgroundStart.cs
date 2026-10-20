using System.Text.Json;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static partial class TenantApprovalRules
{
    public static AccessScope Scope(Guid tenant, PreparedTenantOperation operation) => (operation.AutomationAdmission is { Permission: not "automations.run" } || operation.BackgroundStart is not null || operation.BackgroundReport is not null)
        ? AccessScope.ForTenant(tenant) : AccessScope.ForResource(tenant, operation.ResourceType, operation.ResourceId);
    public static PreparedTenantOperation PrepareBackgroundStart(Guid operation, Guid instance, PreparedBackgroundStart value, long revision, long personaRevision)
    {
        var ids = new[] { value.TenantId, value.SessionId, value.ChildSessionId, value.EntryId, value.ActivationId, value.RunId, value.ResponseId };
        if (operation == Guid.Empty || instance == Guid.Empty || revision < 1 || personaRevision < 1 || ids.Any(id => id == Guid.Empty)
            || ids.Distinct().Count() != ids.Length || value.Permission is not ("sessions.create" or "agent-runs.create")
            || value.Objective is not { Length: > 0 and <= AgentCore.Domain.Conversation.AgentRunLimits.MaxBackgroundObjectiveCharacters } || value.Objective != value.Objective.Trim()
            || value.Title is not { Length: > 0 and <= 80 } || value.Title != value.Title.Trim()
            || value.ConfigurationHash is not { Length: 64 } || value.ConfigurationHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || value.Model is null || string.IsNullOrWhiteSpace(value.Model.CatalogKey) || string.IsNullOrWhiteSpace(value.Model.ProviderAlias) || string.IsNullOrWhiteSpace(value.Model.ModelId))
            throw new ArgumentException("Invalid exact background creation.");
        _ = value.Model.ToPin();
        var json = JsonSerializer.Serialize(new { operation = "background.start", permission = value.Permission, resourceType = "tenant", resourceId = value.TenantId.ToString("D"),
            background = value, expectedInstanceRevision = revision, expectedPersonaRevision = personaRevision });
        return new(operation, instance, null, revision, personaRevision, json, Tools.TenantWorkspaceMutationContract.Hash(json), BackgroundStart: value);
    }
}
