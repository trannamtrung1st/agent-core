using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// A readable child key is unique only inside its actual owning Instance. This reference is an
// IAM address, never an ownership assertion; stores resolve its complete current backing graph.
public static class TenantAgentChildScopes
{
    public static AccessScope Scope(Guid tenant, Guid instance, string type, string key)
    {
        var scope = AccessScope.ForResource(tenant, type, instance.ToString("D") + ":" + key);
        if (tenant == Guid.Empty || !TryParse(scope, out _, out _)) throw new AccessDeniedException("resource_unavailable");
        return scope;
    }

    public static bool TryParse(AccessScope scope, out Guid instance, out string key)
    {
        instance = Guid.Empty; key = "";
        if (scope.Kind != AccessScopeKind.Resource || scope.TenantId is not { } tenant || tenant == Guid.Empty
            || scope.ResourceType is not ("skill" or "resource") || scope.ResourceId is not { Length: > 37 } value
            || value[36] != ':' || !Guid.TryParseExact(value[..36], "D", out instance) || instance == Guid.Empty
            || value[..36] != instance.ToString("D")) return false;
        key = value[37..];
        if (scope.ResourceType == "skill") return SkillKeys.IsValid(key);
        var offset = key.StartsWith("instance:", StringComparison.Ordinal) ? 9 : key.StartsWith("definition:", StringComparison.Ordinal) ? 11 : -1;
        return offset > 0 && Guid.TryParseExact(key[offset..], "D", out var id) && id != Guid.Empty && key[offset..] == id.ToString("D");
    }

    public static bool BelongsTo(AccessScope scope, Guid instance) => TryParse(scope, out var owner, out _) && owner == instance;

    public static IReadOnlySet<AccessScope> Catalog(Guid tenant, TenantAgentConfigurationSnapshot snapshot,
        AgentDefinition? definition, IReadOnlyList<AgentDefinitionPublicationResource> resources)
    {
        var id = snapshot.Agent.Instance.InstanceId;
        var skills = snapshot.Skills.InstanceSkills.Select(s => "instance:" + s.SkillId)
            .Concat((definition?.SkillList ?? []).Select(s => "definition:" + s.Id))
            .Concat(ManagementBuiltInSkills.Catalog.Select(s => s.Key));
        var files = snapshot.Resources.InstanceResources.Select(r => "instance:" + r.ResourceId.ToString("D"))
            .Concat(resources.Select(r => "definition:" + r.ResourceId.ToString("D")));
        return skills.Select(key => Scope(tenant, id, "skill", key)).Concat(files.Select(key => Scope(tenant, id, "resource", key))).ToHashSet();
    }

    public static TenantAgentConfigurationSnapshot After(TenantAgentConfigurationSnapshot snapshot, TenantAgentConfigurationChange change)
    {
        var skill = change.Skill; var resource = change.Resource;
        return snapshot with
        {
            Agent = snapshot.Agent with { Instance = snapshot.Agent.Instance with { ActiveVersion = change.ActiveVersion ?? snapshot.Agent.Instance.ActiveVersion } },
            Skills = new(snapshot.Skills.DefinitionStates,
                snapshot.Skills.InstanceSkills.Where(s => s.SkillId != skill?.DeleteSkillId && s.SkillId != skill?.InstanceSkill?.SkillId)
                    .Concat(skill?.InstanceSkill is { } local ? [local] : []).ToArray()),
            Resources = new(snapshot.Resources.DefinitionStates,
                snapshot.Resources.InstanceResources.Where(r => r.ResourceId != resource?.DeleteResourceId && r.ResourceId != resource?.Resource?.ResourceId)
                    .Concat(resource?.Resource is { } file ? [file] : []).ToArray())
        };
    }

    public static TenantSecurityState RetainAvailable(TenantSecurityState state, Guid instance, IReadOnlySet<AccessScope> catalog) => state with
    {
        Assignments = state.Assignments.Where(a => !BelongsTo(a.Scope, instance) || catalog.Contains(a.Scope)).ToArray()
    };
}
