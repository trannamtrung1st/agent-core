using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class PermissionApprovalFloorTests
{
    // Exact pre-refactor Core catalog: order, scope, principal eligibility and risk are policy.
    public static IEnumerable<object[]> ExpectedCatalog =>
    [
        ["system.tenants.create", "tenant", PermissionApprovalClass.Mutation, true, false, true],
        ["system.tenants.read", "tenant", PermissionApprovalClass.ReadOnly, true, false, true],
        ["system.tenants.manage", "tenant", PermissionApprovalClass.Mutation, true, false, true],
        ["system.accounts.manage", "account", PermissionApprovalClass.Mutation, true, false, true],
        ["system.governance.manage", "system", PermissionApprovalClass.Mutation, true, false, true],
        ["members.read", "member", PermissionApprovalClass.ReadOnly, false, true, false],
        ["members.invite", "member", PermissionApprovalClass.Mutation, true, false, false],
        ["members.update", "member", PermissionApprovalClass.Mutation, true, false, false],
        ["members.remove", "member", PermissionApprovalClass.Mutation, true, false, false],
        ["iam.roles.read", "role", PermissionApprovalClass.ReadOnly, false, true, false],
        ["iam.roles.manage", "role", PermissionApprovalClass.Mutation, true, false, false],
        ["iam.roles.assign", "role", PermissionApprovalClass.Mutation, true, false, false],
        ["agents.read", "agent", PermissionApprovalClass.ReadOnly, false, true, false],
        ["agents.chat", "agent", PermissionApprovalClass.Mutation, false, true, false],
        ["agents.create", "agent", PermissionApprovalClass.Mutation, false, true, false],
        ["agents.update", "agent", PermissionApprovalClass.Mutation, false, true, false],
        ["agents.delete", "agent", PermissionApprovalClass.Sensitive, false, true, false],
        ["agent-definitions.read", "definition", PermissionApprovalClass.ReadOnly, false, true, false],
        ["agent-definitions.create", "definition", PermissionApprovalClass.Mutation, false, true, false],
        ["agent-definitions.update", "definition", PermissionApprovalClass.Mutation, false, true, false],
        ["agent-definitions.publish", "definition", PermissionApprovalClass.Mutation, false, true, false],
        ["agent-definitions.delete", "definition", PermissionApprovalClass.Sensitive, false, true, false],
        ["agent-runs.read", "agent-run", PermissionApprovalClass.ReadOnly, false, true, false],
        ["agent-runs.create", "agent-run", PermissionApprovalClass.Mutation, false, true, false],
        ["agent-runs.cancel", "agent-run", PermissionApprovalClass.Mutation, false, true, false],
        ["sessions.read", "session", PermissionApprovalClass.ReadOnly, false, true, false],
        ["sessions.create", "session", PermissionApprovalClass.Mutation, false, true, false],
        ["sessions.manage", "session", PermissionApprovalClass.Mutation, false, true, false],
        ["sessions.delete", "session", PermissionApprovalClass.Sensitive, false, true, false],
        ["credentials.read", "credential", PermissionApprovalClass.ReadOnly, false, true, false],
        ["credentials.bind", "credential", PermissionApprovalClass.Sensitive, false, true, false],
        ["credentials.use", "credential", PermissionApprovalClass.Sensitive, false, true, false],
        ["credentials.manage", "credential", PermissionApprovalClass.Sensitive, false, true, false],
        ["resources.read", "resource", PermissionApprovalClass.ReadOnly, false, true, false],
        ["resources.manage", "resource", PermissionApprovalClass.Mutation, false, true, false],
        ["resources.delete", "resource", PermissionApprovalClass.Sensitive, false, true, false],
        ["skills.read", "skill", PermissionApprovalClass.ReadOnly, false, true, false],
        ["skills.manage", "skill", PermissionApprovalClass.Mutation, false, true, false],
        ["automations.read", "automation", PermissionApprovalClass.ReadOnly, false, true, false],
        ["automations.create", "automation", PermissionApprovalClass.Mutation, false, true, false],
        ["automations.update", "automation", PermissionApprovalClass.Mutation, false, true, false],
        ["automations.delete", "automation", PermissionApprovalClass.Sensitive, false, true, false],
        ["automations.run", "automation", PermissionApprovalClass.Mutation, false, true, false],
        ["core-events.read", "event", PermissionApprovalClass.ReadOnly, false, true, false],
        ["core-events.manage", "event", PermissionApprovalClass.Mutation, false, true, false],
        ["webhooks.read", "webhook", PermissionApprovalClass.ReadOnly, false, true, false],
        ["webhooks.manage", "webhook", PermissionApprovalClass.Mutation, false, true, false],
        ["memory.read", "agent", PermissionApprovalClass.ReadOnly, false, true, false],
        ["memory.manage", "agent", PermissionApprovalClass.Mutation, false, true, false],
        ["workspaces.read", "workspace", PermissionApprovalClass.ReadOnly, false, true, false],
        ["workspaces.write", "workspace", PermissionApprovalClass.Mutation, false, true, false],
        ["workspaces.delete", "workspace", PermissionApprovalClass.Sensitive, false, true, false],
        ["artifacts.read", "artifact", PermissionApprovalClass.ReadOnly, false, true, false],
        ["artifacts.create", "artifact", PermissionApprovalClass.Mutation, false, true, false],
        ["artifacts.delete", "artifact", PermissionApprovalClass.Sensitive, false, true, false],
        ["attachments.read", "attachment", PermissionApprovalClass.ReadOnly, false, true, false],
        ["attachments.create", "attachment", PermissionApprovalClass.Mutation, false, true, false],
        ["attachments.delete", "attachment", PermissionApprovalClass.Sensitive, false, true, false],
        ["email.read", "credential", PermissionApprovalClass.ReadOnly, false, true, false],
        ["email.draft", "credential", PermissionApprovalClass.Mutation, false, true, false],
        ["email.send", "credential", PermissionApprovalClass.Sensitive, false, true, false],
        ["drive.read", "credential", PermissionApprovalClass.ReadOnly, false, true, false],
        ["drive.write", "credential", PermissionApprovalClass.Mutation, false, true, false],
        ["drive.delete", "credential", PermissionApprovalClass.Sensitive, false, true, false],
        ["browser.read", "agent", PermissionApprovalClass.ReadOnly, false, false, false],
        ["browser.use", "agent", PermissionApprovalClass.Sensitive, false, false, false],
        ["browser.manage", "agent", PermissionApprovalClass.Sensitive, false, false, false],
        ["sandbox.read", "agent", PermissionApprovalClass.ReadOnly, false, false, false],
        ["sandbox.execute", "agent", PermissionApprovalClass.Sensitive, false, false, false],
        ["sandbox.manage", "agent", PermissionApprovalClass.Sensitive, false, false, false],
        ["approvals.read", "approval", PermissionApprovalClass.ReadOnly, false, true, false],
        ["approvals.cancel", "approval", PermissionApprovalClass.Mutation, false, true, false],
        ["approvals.approve", "approval", PermissionApprovalClass.Mutation, true, false, false],
        ["approvals.grant_exception", "approval", PermissionApprovalClass.Mutation, true, false, false],
        ["security.audit.read", "tenant", PermissionApprovalClass.ReadOnly, true, false, false],
        ["tenant.governance.read", "tenant", PermissionApprovalClass.ReadOnly, false, true, false],
        ["tenant.governance.manage", "tenant", PermissionApprovalClass.Mutation, true, false, false],
    ];

    [Theory] [MemberData(nameof(ExpectedCatalog))]
    public void Every_Core_permission_retains_its_explicit_risk_scope_and_principal_reserve(string key, string resource, PermissionApprovalClass risk, bool humanOnly, bool delegatable, bool systemOnly)
    {
        var permission = PermissionCatalog.Get(key);
        Assert.Equal(resource, permission.ResourceType); Assert.Equal(risk, permission.ApprovalClass);
        Assert.Equal(humanOnly, permission.HumanOnly); Assert.Equal(delegatable, permission.Delegatable); Assert.Equal(systemOnly, permission.SystemOnly);
        var floor = OperationReviewPolicy.Minimum(permission.ApprovalClass);
        Assert.Equal(risk switch { PermissionApprovalClass.ReadOnly => OperationApprovalMode.None, PermissionApprovalClass.Mutation => OperationApprovalMode.WhenRequesterLacksPermission, _ => OperationApprovalMode.Always }, floor.Mode);
        Assert.False(floor.IndependentReview);
        Assert.True(floor.IsAtLeast(floor));
        if (risk != PermissionApprovalClass.ReadOnly) Assert.False(new OperationReviewPolicy(OperationApprovalMode.None, false).IsAtLeast(floor));
        if (risk == PermissionApprovalClass.Sensitive) Assert.False(new OperationReviewPolicy(OperationApprovalMode.WhenRequesterLacksPermission, true).IsAtLeast(floor));
    }

    [Fact]
    public void Core_catalog_and_builtin_role_order_remain_closed_and_human_reserves_never_enter_operational_roles()
    {
        Assert.Equal(ExpectedCatalog.Select(r => (string)r[0]), PermissionCatalog.All.Select(p => p.Key));
        Assert.Equal(PermissionCatalog.All.Count, PermissionCatalog.All.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.False(PermissionCatalog.TryGet("skills.arbitrary_new_action", out _));
        var roles = BuiltinAccessRoles.ForTenant(Guid.NewGuid(), Guid.NewGuid);
        foreach (var role in roles.Where(r => !r.HumanOnly))
            Assert.All(role.Permissions, key => Assert.False(PermissionCatalog.Get(key).HumanOnly || PermissionCatalog.Get(key).SystemOnly));
        Assert.All(PermissionCatalog.All.Where(p => p.HumanOnly || p.SystemOnly), p => Assert.False(p.Delegatable));
    }
}
