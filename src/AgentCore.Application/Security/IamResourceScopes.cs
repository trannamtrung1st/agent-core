using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Member/role/agent ownership is in security state; Definitions, Credentials and qualified
// children resolve through their actual stores. An arbitrary string is never an owner.
public static class IamResourceScopes
{
    public static AccessScope Create(Guid tenantId, string? resourceType, string? resourceId)
    {
        if (resourceType is null && resourceId is null) return AccessScope.ForTenant(tenantId);
        if (resourceType == "sandbox-profile")
        {
            var profile = TenantSandboxProfileRules.Scope(tenantId, resourceId ?? "");
            if (!TenantSandboxProfileRules.TryId(profile, out _)) throw new AccessDeniedException("resource_unavailable");
            return profile;
        }
        if (resourceType == "workspace")
        {
            var workspace = AccessScope.ForResource(tenantId, resourceType, resourceId ?? "");
            if (!TenantWorkspaceRules.TryParse(workspace, out _, out _)) throw new AccessDeniedException("resource_unavailable");
            return workspace;
        }
        if (resourceType is "skill" or "resource")
        {
            var child = AccessScope.ForResource(tenantId, resourceType, resourceId ?? "");
            if (!TenantAgentChildScopes.TryParse(child, out _, out _) && !TenantDefinitionResourceScopes.TryParse(child, out _) && !TenantResourceScopes.TrySpace(child, out _, out _)) throw new AccessDeniedException("resource_unavailable");
            return child;
        }
        if (tenantId == Guid.Empty || resourceType is not ("secret" or "space" or "member" or "role" or "agent" or "definition" or "credential" or "session" or "agent-run" or "approval" or "artifact" or "attachment" or "automation" or "webhook" or "event" or "model-connection" or "model-offering")
            || !Guid.TryParse(resourceId, out var id) || id == Guid.Empty)
            throw new AccessDeniedException("resource_unavailable");
        return AccessScope.ForResource(tenantId, resourceType, id.ToString("D"));
    }

    public static bool ExternalShape(AccessScope scope) => scope.ResourceType == "definition"
        ? Guid.TryParseExact(scope.ResourceId, "D", out var id) && id != Guid.Empty && scope.ResourceId == id.ToString("D")
        : AgentCore.Application.Models.TenantModelRules.TryId(scope, out _) || TenantSecretService.TryId(scope, out _) || TenantSpaceService.TryId(scope, out _) || TenantEventAdmissionRules.TryId(scope, out _) || TenantWebhookRules.TryId(scope, out _) || TenantAutomationRules.TryId(scope, out _) || TenantWorkspaceRules.TryParse(scope, out _, out _) || TenantArtifactRules.TryId(scope, out _) || TenantAttachmentRules.TryId(scope, out _) || TenantSandboxProfileRules.TryId(scope, out _) || TenantApprovalRules.TryApprovalId(scope, out _) || TenantRunRules.TryId(scope, out _) || TenantSessionRules.TryId(scope, out _) || TenantCredentialRules.TryId(scope, out _) || TenantAgentChildScopes.TryParse(scope, out _, out _) || TenantDefinitionResourceScopes.TryParse(scope, out _) || TenantResourceScopes.TrySpace(scope, out _, out _);

    public static bool Exists(TenantSecurityState state, AccessScope scope)
    {
        if (scope.Kind != AccessScopeKind.Resource || scope.TenantId != state.Tenant.TenantId
            || !Guid.TryParse(scope.ResourceId, out var id) || id == Guid.Empty
            || scope.ResourceId != id.ToString("D")) return false;
        return scope.ResourceType switch
        {
            "member" => state.Users.Any(u => u.UserId == id),
            "role" => state.Roles.Any(r => r.RoleId == id),
            "agent" => state.SystemIdentities.Any(i => i.Kind == SystemIdentityKind.Agent && i.Status != IdentityStatus.Removed && i.AgentInstanceId == id),
            _ => false
        };
    }
}
