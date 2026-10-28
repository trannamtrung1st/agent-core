using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Closed system policy for this one built-in, never inferred from user-editable metadata.
public static class ManagedGeneralAssistant
{
    public const string Kind = "GeneralAssistant";
    public const string DefinitionId = "general-assistant";
    public const string ProvisionAction = "system.general-assistant.provision";
    public const string AdoptAction = "system.general-assistant.adopt";
    public const string ResetAction = "agents.reset-defaults";
    public const string StarterRole = "general-assistant-conversation";
    public const string ContextRole = "general-assistant-own-context";
    public static readonly IReadOnlyList<string> ContextPermissions = Array.AsReadOnly(new[] { "agents.read", "skills.read", "resources.read" });
    public const string FilesRole = "general-assistant-private-files";
    public static readonly IReadOnlyList<string> ConversationPermissions = Array.AsReadOnly(new[] {
        "models.read", "sessions.create", "sessions.read", "sessions.manage", "agent-runs.create", "agent-runs.read",
        "attachments.read", "attachments.create", "artifacts.read", "artifacts.create" });
    public static readonly IReadOnlyList<string> FilePermissions = Array.AsReadOnly(new[] { "spaces.read", "workspaces.read", "workspaces.write" });

    public static async ValueTask RequireCurrentAsync(IBuiltInAgentDefinitionStore definitions, TenantAgent agent, CancellationToken ct)
    {
        if (agent.ManagedKind is null) return;
        Require(agent);
        var latest = await definitions.GetAsync(DefinitionId, cancellationToken: ct);
        if (latest is null || latest.Version != agent.Instance.ActiveVersion) throw new SecurityConflictException("assistant_upgrade_requires_attention");
    }
    public static IReadOnlyList<AgentDefinition> ProductTemplates(IReadOnlyList<AgentDefinition> templates)
    {
        var current = templates.Where(d => d.Id == DefinitionId).Select(d => d.Version).DefaultIfEmpty(0).Max();
        return templates.Where(d => d.Id != DefinitionId || d.Version == current).ToArray();
    }
    public static void RequireNewTemplate(string id, int version, IReadOnlyList<AgentDefinition> templates)
    {
        if (id == DefinitionId && !ProductTemplates(templates).Any(d => d.Id == id && d.Version == version))
            throw new AccessDeniedException("definition_unavailable");
    }
    public static void Require(TenantAgent agent)
    {
        if (agent.ManagedKind != Kind || agent.Instance.DefinitionId != DefinitionId || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active)
            throw new AccessDeniedException("system_managed_instance_required");
    }
    public static void Protect(TenantAgent agent)
    {
        if (agent.ManagedKind is not null) throw new SecurityConflictException("system_managed_instance");
    }
    public static void ValidateMarkerAdditions(TenantSecurityState before, TenantSecurityState next, Guid? provisioning = null)
    {
        if (next.SystemIdentities.Any(i => i.ManagedKind is not null && !before.SystemIdentities.Any(b => b.SystemIdentityId == i.SystemIdentityId)
            && (provisioning is null || i.AgentInstanceId != provisioning)))
            throw new ArgumentException("Only the system provisioning transaction may add a managed identity.");
    }
    public static void ValidateProvision(TenantSecurityState next, AgentInstance instance, AuthorizationAudit audit, bool system)
    {
        var owner = next.SystemIdentities.Single(i => i.AgentInstanceId == instance.InstanceId);
        if (!system)
        {
            if (owner.ManagedKind is not null || audit.ActorPrincipalId == Guid.Empty) throw new ArgumentException("Use trusted managed provisioning.");
            return;
        }
        if (owner.ManagedKind != Kind || instance.DefinitionId != DefinitionId || instance.PersonaCustomized || !instance.ShowInChat
            || audit.ActorPrincipalId != Guid.Empty || audit.Action != ProvisionAction || audit.Target != instance.InstanceId.ToString("D"))
            throw new ArgumentException("Invalid system General Assistant provisioning.");
        foreach (var (key, permissions) in new[] { (StarterRole, ConversationPermissions), (FilesRole, FilePermissions), (ContextRole, ContextPermissions) })
        {
            var role = next.Roles.SingleOrDefault(r => r.Key == key);
            if (role is null || !role.Permissions.Order().SequenceEqual(permissions.Order()) || role.HumanOnly)
                throw new ArgumentException("The system assistant requires its bounded starter roles.");
        }
    }
}
