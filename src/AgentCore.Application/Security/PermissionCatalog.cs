using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record PermissionDefinition(string Key, string ResourceType, bool HumanOnly = false,
    bool Delegatable = true, bool SystemOnly = false, PermissionApprovalClass ApprovalClass = PermissionApprovalClass.Mutation)
{
    public IReadOnlyList<string> ResourceTypes => Key is "sandbox.read" or "sandbox.manage"
        ? [ResourceType, "sandbox-profile"] : Key is "artifacts.create" or "attachments.create" ? [ResourceType, "session"]
        : Key.StartsWith("tasks.", StringComparison.Ordinal) ? [ResourceType, "session", "agent"]
        : Key.StartsWith("agent-messages.", StringComparison.Ordinal) ? [ResourceType, "task", "session", "agent"] : [ResourceType];
    // Access inspection/assignment and exact-action decisions govern the target resource itself.
    // Ordinary operation permissions apply only to their catalog-owned resource family.
    public bool AppliesToResource(string? resourceType) => !SystemOnly && (resourceType is not null && ResourceTypes.Contains(resourceType, StringComparer.Ordinal)
        || Key is "iam.roles.read" or "iam.roles.assign" or "approvals.approve" or "approvals.grant_exception");
}

public static class PermissionCatalog
{
    // Closed catalog: adding a capability/tool never implicitly creates or grants an IAM permission.
    public static IReadOnlyList<PermissionDefinition> All { get; } = Build();
    private static readonly IReadOnlyDictionary<string, PermissionDefinition> ByKey =
        All.ToDictionary(p => p.Key, StringComparer.Ordinal);
    public static bool TryGet(string key, out PermissionDefinition definition) => ByKey.TryGetValue(key, out definition!);
    public static PermissionDefinition Get(string key) => TryGet(key, out var value) ? value : throw new ArgumentException("Unknown permission.", nameof(key));

    private static IReadOnlyList<PermissionDefinition> Build()
    {
        // Risk is an explicit Core decision for every catalog entry, never inferred from a name.
        var result = new List<PermissionDefinition>();
        void Add(string family, string resource, string actions, PermissionApprovalClass classification, bool human = false, bool system = false, bool delegatable = true)
        {
            foreach (var action in actions.Split(' '))
                result.Add(new($"{family}.{action}", resource, human, delegatable && !human, system, classification));
        }
        Add("system.tenants", "tenant", "create", PermissionApprovalClass.Mutation, true, true);
        Add("system.tenants", "tenant", "read", PermissionApprovalClass.ReadOnly, true, true);
        Add("system.tenants", "tenant", "manage", PermissionApprovalClass.Mutation, true, true);
        Add("system.accounts", "account", "manage", PermissionApprovalClass.Mutation, true, true);
        Add("system.governance", "system", "manage", PermissionApprovalClass.Mutation, true, true);
        Add("system.model-presets", "model-preset", "read", PermissionApprovalClass.ReadOnly, true, true);
        Add("system.model-presets", "model-preset", "manage", PermissionApprovalClass.Mutation, true, true);
        Add("scripts", "script", "read", PermissionApprovalClass.ReadOnly);
        Add("scripts", "script", "manage", PermissionApprovalClass.Mutation, true);
        Add("scripts", "script", "execute", PermissionApprovalClass.Mutation);
        Add("evaluations", "evaluation-suite", "read", PermissionApprovalClass.ReadOnly);
        Add("evaluations", "evaluation-suite", "manage review", PermissionApprovalClass.Mutation, true);
        Add("evaluations", "evaluation-suite", "run", PermissionApprovalClass.Mutation);
        Add("models", "model-offering", "read", PermissionApprovalClass.ReadOnly);
        Add("models", "model-offering", "manage", PermissionApprovalClass.Mutation, true);
        Add("model-connections", "model-connection", "read", PermissionApprovalClass.ReadOnly, true);
        Add("model-connections", "model-connection", "manage test", PermissionApprovalClass.Sensitive, true);
        Add("members", "member", "read", PermissionApprovalClass.ReadOnly);
        Add("members", "member", "invite update remove", PermissionApprovalClass.Mutation, true);
        Add("iam.roles", "role", "read", PermissionApprovalClass.ReadOnly);
        Add("iam.roles", "role", "manage assign", PermissionApprovalClass.Mutation, true);
        Add("agents", "agent", "read", PermissionApprovalClass.ReadOnly);
        Add("agents", "agent", "chat create update", PermissionApprovalClass.Mutation);
        Add("agents", "agent", "delete", PermissionApprovalClass.Sensitive);
        Add("agent-definitions", "definition", "read", PermissionApprovalClass.ReadOnly);
        Add("agent-definitions", "definition", "create update publish", PermissionApprovalClass.Mutation);
        Add("agent-definitions", "definition", "delete", PermissionApprovalClass.Sensitive);
        Add("agent-runs", "agent-run", "read", PermissionApprovalClass.ReadOnly);
        Add("agent-runs", "agent-run", "create cancel", PermissionApprovalClass.Mutation);
        Add("tasks", "task", "read", PermissionApprovalClass.ReadOnly);
        Add("tasks", "task", "delegate cancel", PermissionApprovalClass.Mutation);
        Add("agent-messages", "agent-thread", "read", PermissionApprovalClass.ReadOnly);
        Add("agent-messages", "agent-thread", "send", PermissionApprovalClass.Mutation);
        Add("goals", "session", "read", PermissionApprovalClass.ReadOnly);
        Add("goals", "session", "manage", PermissionApprovalClass.Mutation);
        Add("goals", "session", "accept", PermissionApprovalClass.Mutation, true);
        Add("sessions", "session", "read", PermissionApprovalClass.ReadOnly);
        Add("sessions", "session", "create manage", PermissionApprovalClass.Mutation);
        Add("sessions", "session", "delete", PermissionApprovalClass.Sensitive);
        Add("environments", "space", "read", PermissionApprovalClass.ReadOnly);
        Add("environments", "space", "manage activate", PermissionApprovalClass.Mutation);
        Add("variables", "space", "read", PermissionApprovalClass.ReadOnly);
        Add("variables", "space", "manage", PermissionApprovalClass.Mutation);
        Add("variables", "space", "resolve", PermissionApprovalClass.ReadOnly);
        Add("secrets", "secret", "read", PermissionApprovalClass.ReadOnly);
        Add("secrets", "secret", "manage use", PermissionApprovalClass.Sensitive);
        Add("spaces", "space", "read", PermissionApprovalClass.ReadOnly);
        Add("spaces", "space", "create manage", PermissionApprovalClass.Mutation);
        Add("spaces", "space", "delete", PermissionApprovalClass.Sensitive);
        Add("space-memberships", "space", "read", PermissionApprovalClass.ReadOnly);
        Add("space-memberships", "space", "manage", PermissionApprovalClass.Mutation);
        Add("credentials", "credential", "read", PermissionApprovalClass.ReadOnly);
        Add("credentials", "credential", "bind use manage", PermissionApprovalClass.Sensitive);
        Add("resources", "resource", "read", PermissionApprovalClass.ReadOnly);
        Add("resources", "resource", "manage", PermissionApprovalClass.Mutation);
        Add("resources", "resource", "delete", PermissionApprovalClass.Sensitive);
        Add("skills", "skill", "read", PermissionApprovalClass.ReadOnly);
        Add("skills", "skill", "manage", PermissionApprovalClass.Mutation);
        Add("automations", "automation", "read", PermissionApprovalClass.ReadOnly);
        Add("automations", "automation", "create update", PermissionApprovalClass.Mutation);
        Add("automations", "automation", "delete", PermissionApprovalClass.Sensitive);
        Add("automations", "automation", "run", PermissionApprovalClass.Mutation);
        Add("core-events", "event", "read", PermissionApprovalClass.ReadOnly);
        Add("core-events", "event", "manage", PermissionApprovalClass.Mutation);
        Add("webhooks", "webhook", "read", PermissionApprovalClass.ReadOnly);
        Add("webhooks", "webhook", "manage", PermissionApprovalClass.Mutation);
        Add("memory", "agent", "read", PermissionApprovalClass.ReadOnly);
        Add("memory", "agent", "manage", PermissionApprovalClass.Mutation);
        Add("workspaces", "workspace", "read", PermissionApprovalClass.ReadOnly);
        Add("workspaces", "workspace", "write", PermissionApprovalClass.Mutation);
        Add("workspaces", "workspace", "delete", PermissionApprovalClass.Sensitive);
        Add("artifacts", "artifact", "read", PermissionApprovalClass.ReadOnly);
        Add("artifacts", "artifact", "create", PermissionApprovalClass.Mutation);
        Add("artifacts", "artifact", "delete", PermissionApprovalClass.Sensitive);
        Add("attachments", "attachment", "read", PermissionApprovalClass.ReadOnly);
        Add("attachments", "attachment", "create", PermissionApprovalClass.Mutation);
        Add("attachments", "attachment", "delete", PermissionApprovalClass.Sensitive);
        Add("email", "credential", "read", PermissionApprovalClass.ReadOnly);
        Add("email", "credential", "draft", PermissionApprovalClass.Mutation);
        Add("email", "credential", "send", PermissionApprovalClass.Sensitive);
        Add("drive", "credential", "read", PermissionApprovalClass.ReadOnly);
        Add("drive", "credential", "write", PermissionApprovalClass.Mutation);
        Add("drive", "credential", "delete", PermissionApprovalClass.Sensitive);
        Add("browser", "agent", "read", PermissionApprovalClass.ReadOnly, delegatable: false);
        Add("browser", "agent", "use manage", PermissionApprovalClass.Sensitive, delegatable: false);
        Add("sandbox", "agent", "read", PermissionApprovalClass.ReadOnly, delegatable: false);
        Add("sandbox", "agent", "execute manage", PermissionApprovalClass.Sensitive, delegatable: false);
        Add("approvals", "approval", "read", PermissionApprovalClass.ReadOnly);
        Add("approvals", "approval", "cancel", PermissionApprovalClass.Mutation);
        Add("approvals", "approval", "approve grant_exception", PermissionApprovalClass.Mutation, true);
        Add("security.audit", "tenant", "read", PermissionApprovalClass.ReadOnly, true);
        Add("tenant.governance", "tenant", "read", PermissionApprovalClass.ReadOnly);
        Add("tenant.governance", "tenant", "manage", PermissionApprovalClass.Mutation, true);
        Add("http", "agent", "request", PermissionApprovalClass.Sensitive);
        return result.AsReadOnly();
    }
}

public static class BuiltinAccessRoles
{
    public const string Owner = "owner";
    public const string SystemOperator = "system-operator";

    public static IReadOnlyList<AccessRole> ForTenant(Guid tenantId, Func<Guid> id)
    {
        var tenant = PermissionCatalog.All.Where(p => !p.SystemOnly).ToArray();
        // New cooperation rights require explicit assignment to ordinary Agent roles.
        var ordinary = tenant.Where(p => !p.Key.StartsWith("tasks.", StringComparison.Ordinal)
            && !p.Key.StartsWith("agent-messages.", StringComparison.Ordinal)).ToArray();
        AccessRole Role(string key, string name, bool human, IEnumerable<PermissionDefinition> permissions) =>
            new(id(), tenantId, key, name, true, human, permissions.Select(p => p.Key).ToArray());
        return [
            Role(Owner, "Owner", true, tenant),
            Role(ManagedGeneralAssistant.StarterRole, "Assistant conversation", false, ManagedGeneralAssistant.ConversationPermissions.Select(PermissionCatalog.Get)),
            Role(ManagedGeneralAssistant.ContextRole, "Assistant own context", false, ManagedGeneralAssistant.ContextPermissions.Select(PermissionCatalog.Get)),
            Role(ManagedGeneralAssistant.FilesRole, "Assistant private files", false, ManagedGeneralAssistant.FilePermissions.Select(PermissionCatalog.Get)),
            Role("access-administrator", "Access Administrator", true, tenant.Where(p => p.Key.StartsWith("members.", StringComparison.Ordinal) || p.Key.StartsWith("iam.roles.", StringComparison.Ordinal) || p.Key == "security.audit.read")),
            Role("contributor", "Contributor", false, ordinary.Where(p => !p.HumanOnly && !p.Key.StartsWith("secrets.", StringComparison.Ordinal) && p.Key != "http.request" && p.Key != "evaluations.run" && p.Key != "scripts.execute" && !p.Key.StartsWith("approvals.", StringComparison.Ordinal) && !p.Key.StartsWith("tenant.governance.", StringComparison.Ordinal))),
            Role("reader", "Reader", false, ordinary.Where(p => !p.HumanOnly && p.Key.EndsWith(".read", StringComparison.Ordinal))),
            Role("agent-operator", "Agent Operator", false, ordinary.Where(p => p.Key is "agents.read" or "agents.chat" or "agent-runs.read" or "agent-runs.cancel" or "sessions.read" or "sessions.create" or "sessions.manage")),
            Role("agent-administrator", "Agent Administrator", false, ordinary.Where(p => !p.HumanOnly && (p.Key.StartsWith("agents.", StringComparison.Ordinal) || p.Key.StartsWith("agent-definitions.", StringComparison.Ordinal) || p.Key.StartsWith("skills.", StringComparison.Ordinal)))),
            Role("approver", "Approver", true, tenant.Where(p => p.Key is "approvals.read" or "approvals.approve"))
        ];
    }

    public static AccessRole ForSystem(Func<Guid> id) => new(id(), null, SystemOperator, "Platform Operator", true, true,
        PermissionCatalog.All.Where(p => p.SystemOnly).Select(p => p.Key).ToArray());
}
