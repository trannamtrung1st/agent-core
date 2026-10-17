using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record PermissionDefinition(string Key, string ResourceType, bool HumanOnly = false,
    bool Delegatable = true, bool SystemOnly = false, PermissionApprovalClass ApprovalClass = PermissionApprovalClass.Mutation)
{
    public IReadOnlyList<string> ResourceTypes => Key is "sandbox.read" or "sandbox.manage"
        ? [ResourceType, "sandbox-profile"] : [ResourceType];
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
        var result = new List<PermissionDefinition>();
        void Add(string family, string resource, string actions, bool human = false, bool system = false, bool delegatable = true)
        {
            foreach (var action in actions.Split(' '))
                result.Add(new($"{family}.{action}", resource, human, delegatable && !human, system,
                    action == "read" ? PermissionApprovalClass.ReadOnly
                        : action is "delete" or "send" or "execute" || family is "credentials" or "browser" or "sandbox"
                            ? PermissionApprovalClass.Sensitive : PermissionApprovalClass.Mutation));
        }
        Add("system.tenants", "tenant", "create read manage", true, true);
        Add("system.accounts", "account", "manage", true, true);
        Add("system.governance", "system", "manage", true, true);
        Add("members", "member", "read");
        Add("members", "member", "invite update remove", true);
        Add("iam.roles", "role", "read");
        Add("iam.roles", "role", "manage assign", true);
        Add("agents", "agent", "read chat create update delete");
        Add("agent-definitions", "definition", "read create update publish delete");
        Add("agent-runs", "agent-run", "read create cancel");
        Add("sessions", "session", "read create manage delete");
        Add("credentials", "credential", "read bind use manage");
        Add("resources", "resource", "read manage delete");
        Add("skills", "skill", "read manage");
        Add("automations", "automation", "read create update delete run");
        Add("core-events", "event", "read manage");
        Add("webhooks", "webhook", "read manage");
        Add("memory", "agent", "read manage");
        Add("workspaces", "workspace", "read write delete");
        Add("artifacts", "artifact", "read create delete");
        Add("attachments", "attachment", "read create delete");
        Add("email", "credential", "read draft send");
        Add("drive", "credential", "read write delete");
        Add("browser", "agent", "read use manage", delegatable: false);
        Add("sandbox", "agent", "read execute manage", delegatable: false);
        Add("approvals", "approval", "read cancel");
        Add("approvals", "approval", "approve grant_exception", true);
        Add("security.audit", "tenant", "read", true);
        Add("tenant.governance", "tenant", "read");
        Add("tenant.governance", "tenant", "manage", true);
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
        AccessRole Role(string key, string name, bool human, IEnumerable<PermissionDefinition> permissions) =>
            new(id(), tenantId, key, name, true, human, permissions.Select(p => p.Key).ToArray());
        return [
            Role(Owner, "Owner", true, tenant),
            Role("access-administrator", "Access Administrator", true, tenant.Where(p => p.Key.StartsWith("members.", StringComparison.Ordinal) || p.Key.StartsWith("iam.roles.", StringComparison.Ordinal) || p.Key == "security.audit.read")),
            Role("contributor", "Contributor", false, tenant.Where(p => !p.HumanOnly && !p.Key.StartsWith("approvals.", StringComparison.Ordinal) && !p.Key.StartsWith("tenant.governance.", StringComparison.Ordinal))),
            Role("reader", "Reader", false, tenant.Where(p => !p.HumanOnly && p.Key.EndsWith(".read", StringComparison.Ordinal))),
            Role("agent-operator", "Agent Operator", false, tenant.Where(p => p.Key is "agents.read" or "agents.chat" or "agent-runs.read" or "agent-runs.cancel" or "sessions.read" or "sessions.create" or "sessions.manage")),
            Role("agent-administrator", "Agent Administrator", false, tenant.Where(p => !p.HumanOnly && (p.Key.StartsWith("agents.", StringComparison.Ordinal) || p.Key.StartsWith("agent-definitions.", StringComparison.Ordinal) || p.Key.StartsWith("skills.", StringComparison.Ordinal)))),
            Role("approver", "Approver", true, tenant.Where(p => p.Key is "approvals.read" or "approvals.approve"))
        ];
    }

    public static AccessRole ForSystem(Func<Guid> id) => new(id(), null, SystemOperator, "Platform Operator", true, true,
        PermissionCatalog.All.Where(p => p.SystemOnly).Select(p => p.Key).ToArray());
}
