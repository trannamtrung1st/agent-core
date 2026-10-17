using System.Text;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_child_editors_return_coherent_paged_facts_and_exact_existing_write_witnesses(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var agent = await agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision()); var id = agent.Instance.InstanceId;
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        var reads = new TenantAgentReadService(f.Iam, (ITenantAgentStore)f.Iam, configuration, f.Identity, f.Authorizer, agents);
        var skill = await configuration.WriteSkillAsync(owner.OwnerPrincipalId, owner.TenantId, id, "create", await Revision(), 1,
            input: new("Zulu review", "Review regional totals", "Compare supplied totals.", SkillProjection.OnDemand, true, ["chat.respond"], "zulu"));
        var file = await configuration.UpsertResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), skill.InstanceRevision,
            new(null, null, "regional/zulu.txt", AgentDefinitionResourceKind.Reference, "text/plain", Encoding.UTF8.GetBytes("Retained evidence.")));
        var before = await Revision(); var audit = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        var skills = await reads.ReadSkillEditorAsync(owner.TenantId, id, human, pageSize: 1, search: "REGIONAL");
        var resources = await reads.ReadResourceEditorAsync(owner.TenantId, id, human, pageSize: 1, search: "zULu");
        Assert.True(skills.CanCreate); Assert.True(resources.CanCreate); Assert.True(skills.AllReadable); Assert.True(resources.AllReadable);
        Assert.Equal(before, skills.SecurityRevision); Assert.Equal(before, resources.SecurityRevision);
        Assert.Equal(file.InstanceRevision, skills.Agent.Instance.Revision); Assert.Equal(file.InstanceRevision, resources.Agent.Instance.Revision);
        var exact = await reads.ReadSkillDetailAsync(owner.TenantId, id, human, skill.Skill.Key);
        Assert.Equal(skills.SecurityRevision, exact.SecurityRevision); Assert.Equal(skills.Agent.Instance.Revision, exact.Agent.Instance.Revision);
        Assert.Equal(skill.Skill.Procedure, exact.Item.Skill.Procedure); Assert.True(exact.Item.CanManage);
        var s = Assert.Single(skills.Items); Assert.True(s.CanManage); Assert.False(s.CanCustomize); Assert.Equal(skill.Skill.Key, s.Skill.Key);
        var r = Assert.Single(resources.Items); Assert.True(r.CanManage); Assert.True(r.CanDelete); Assert.False(r.CanCopy); Assert.Equal(file.Resource!.Key, r.Resource.Key);
        Assert.Equal(1, skills.Total); Assert.Equal(1, resources.Total);
        Assert.Empty((await reads.ReadSkillEditorAsync(owner.TenantId, id, human, 1, 1, "regional")).Items);
        Assert.Empty((await reads.ReadResourceEditorAsync(owner.TenantId, id, human, 1, 1, "zulu")).Items);
        Assert.Equal(before, await Revision()); Assert.Equal(audit, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        await configuration.WriteSkillAsync(owner.OwnerPrincipalId, owner.TenantId, id, "set_enabled", skills.SecurityRevision,
            skills.Agent.Instance.Revision, s.Skill.Key, s.Skill.Revision, enabled: false, initiator: human);
        await Assert.ThrowsAsync<SecurityConflictException>(() => configuration.ChangeResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id,
            r.Resource.Key, false, false, resources.SecurityRevision, resources.Agent.Instance.Revision, r.Resource.Revision).AsTask());
        resources = await reads.ReadResourceEditorAsync(owner.TenantId, id, human);
        await configuration.ChangeResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, r.Resource.Key, false, false,
            resources.SecurityRevision, resources.Agent.Instance.Revision, r.Resource.Revision);
        Assert.False((await reads.ReadResourceEditorAsync(owner.TenantId, id, human)).Items.Single(item => item.Resource.Key == r.Resource.Key).Resource.Enabled);
        Assert.False((await reads.ReadSkillEditorAsync(owner.TenantId, id, human, search: "regional")).Items.Single().Skill.Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_child_editors_filter_before_search_counts_and_separate_item_manage_delete_from_sibling_creation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Child editor source", await Revision());
        var bound = await f.Service.BindResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), draft.Revision,
            [new(null, "visible.txt", AgentDefinitionResourceKind.Reference, "text/plain", Encoding.UTF8.GetBytes("Read-only source bytes.")),
             new(null, "private-sentinel.txt", AgentDefinitionResourceKind.Reference, "text/plain", Encoding.UTF8.GetBytes("Hidden source bytes."))]);
        draft = await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, draft.Candidate with { Skills = [
            new("visible", "Visible review", "Check visible totals", "Visible procedure.", SkillProjection.OnDemand, true, ["chat.respond"], ["visible.txt"]),
            new("private-sentinel", "Private sentinel", "Hidden description", "Hidden procedure.", SkillProjection.OnDemand, false, ["chat.respond"], ["visible.txt"]) ] }, await Revision(), bound.Revision);
        await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), draft.Revision);
        var agent = await agents.CreateFromPublicationAsync(owner.OwnerPrincipalId, owner.TenantId, Guid.Parse(draft.DefinitionId), 1, await Revision()); var id = agent.Instance.InstanceId;
        async Task<long> Ir() => (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, id))!.Instance.Revision;
        var local = await configuration.UpsertResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, await Revision(), await Ir(),
            new(null, null, "local.txt", AgentDefinitionResourceKind.Reference, "text/plain", Encoding.UTF8.GetBytes("Local bytes.")));
        var account = await f.Identity.RegisterAsync("child-reader@example.test", "Child reader", "strong-reader-password");
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var invitation = await f.Access.InviteAsync(owner.OwnerPrincipalId, owner.TenantId, account.LoginKey, state.Roles.Single(r => r.Key == "approver").RoleId, await Revision());
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, owner.TenantId, invitation.Invitation.InvitationId, invitation.Token);
        async Task<Guid> Role(string name, params string[] permissions) => (await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, owner.TenantId, null, name, permissions, await Revision())).RoleId;
        async Task<RoleAssignment> Grant(Guid role, AccessScope scope) => await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, user.PrincipalId, role, scope, await Revision());
        var parent = await Grant(await Role("Exact parent read", "agents.read"), AccessScope.ForResource(owner.TenantId, "agent", id.ToString("D")));
        var skillScope = TenantAgentChildScopes.Scope(owner.TenantId, id, "skill", "definition:visible");
        var sourceKey = "definition:" + bound.Resources.Single(r => r.LogicalPath == "visible.txt").ResourceId.ToString("D");
        var sourceScope = TenantAgentChildScopes.Scope(owner.TenantId, id, "resource", sourceKey);
        await Grant(await Role("Exact Skill manager", "skills.read", "skills.manage"), skillScope);
        await Grant(await Role("Exact source reader", "resources.read"), sourceScope);
        await Grant(await Role("Exact local deleter", "resources.read", "resources.delete"), TenantAgentChildScopes.Scope(owner.TenantId, id, "resource", local.Resource!.Key));
        var login = (await f.Identity.SignInAsync("child-reader@example.test", "strong-reader-password"))!.Session;
        var human = new TenantSessionInitiator(account.AccountId, login.SessionId, user.PrincipalId);
        var reads = new TenantAgentReadService(f.Iam, (ITenantAgentStore)f.Iam, configuration, f.Identity, f.Authorizer, agents);
        var skills = await reads.ReadSkillEditorAsync(owner.TenantId, id, human, pageSize: 1);
        Assert.Equal(1, skills.Total); Assert.False(skills.AllReadable); Assert.False(skills.CanCreate);
        var skill = Assert.Single(skills.Items); Assert.True(skill.CanManage); Assert.False(skill.CanCustomize);
        Assert.Empty((await reads.ReadSkillEditorAsync(owner.TenantId, id, human, search: "private-sentinel")).Items);
        var resources = await reads.ReadResourceEditorAsync(owner.TenantId, id, human);
        Assert.Equal(2, resources.Total); Assert.False(resources.AllReadable); Assert.False(resources.CanCreate);
        var inherited = resources.Items.Single(r => r.Resource.Key == sourceKey); Assert.False(inherited.CanManage); Assert.False(inherited.CanCopy); Assert.False(inherited.CanDelete);
        Assert.Equal(new[] { "definition:visible" }, inherited.Resource.Dependencies);
        var deletable = resources.Items.Single(r => r.Resource.Key == local.Resource.Key); Assert.False(deletable.CanManage); Assert.True(deletable.CanDelete);
        Assert.Empty((await reads.ReadResourceEditorAsync(owner.TenantId, id, human, search: "private-sentinel")).Items);
        Assert.DoesNotContain("private-sentinel", System.Text.Json.JsonSerializer.Serialize(resources));
        await Assert.ThrowsAsync<AccessDeniedException>(() => configuration.WriteSkillAsync(user.PrincipalId, owner.TenantId, id, "customize",
            skills.SecurityRevision, skills.Agent.Instance.Revision, skill.Skill.Key, skill.Skill.Revision, initiator: human).AsTask());
        var revision = await Revision(); var instanceRevision = await Ir();
        await Assert.ThrowsAsync<AccessDeniedException>(() => configuration.UpsertResourceAsync(user.PrincipalId, owner.TenantId, id, revision, instanceRevision,
            new(null, null, "forbidden-sibling.txt", AgentDefinitionResourceKind.Reference, "text/plain", Encoding.UTF8.GetBytes("Denied.")), initiator: human).AsTask());
        // Tenant manage alone does not grant tenant read or permission to create siblings.
        await Grant(await Role("Tenant child manage", "skills.manage", "resources.manage"), AccessScope.ForTenant(owner.TenantId));
        Assert.False((await reads.ReadSkillEditorAsync(owner.TenantId, id, human)).CanCreate);
        Assert.False((await reads.ReadResourceEditorAsync(owner.TenantId, id, human)).CanCreate);
        await Grant(await Role("Tenant child read", "skills.read", "resources.read"), AccessScope.ForTenant(owner.TenantId));
        skills = await reads.ReadSkillEditorAsync(owner.TenantId, id, human); resources = await reads.ReadResourceEditorAsync(owner.TenantId, id, human);
        Assert.True(skills.CanCreate); Assert.True(skills.AllReadable); Assert.True(resources.CanCreate); Assert.True(resources.AllReadable);
        Assert.True(skills.Items.Single(s => s.Skill.Key == "definition:visible").CanCustomize);
        inherited = resources.Items.Single(r => r.Resource.Key == sourceKey); Assert.True(inherited.CanCopy); Assert.True(inherited.CanManage); Assert.False(inherited.CanDelete);
        await configuration.CopyResourceAsync(user.PrincipalId, owner.TenantId, id, sourceKey, "copied.txt", resources.SecurityRevision, resources.Agent.Instance.Revision, initiator: human);
        var copy = (await reads.ReadResourceEditorAsync(owner.TenantId, id, human, search: "copied.txt")).Items.Single();
        Assert.Equal("Read-only source bytes.", Encoding.UTF8.GetString((await reads.ReadResourceContentAsync(owner.TenantId, id, human, copy.Resource.Key)).Bytes));
        Assert.False(copy.CanDelete); // Tenant manage never implies delete.
        await agents.SetLifecycleAsync(owner.OwnerPrincipalId, owner.TenantId, id, AgentInstanceLifecycle.Archived, await Revision(), await Ir());
        skills = await reads.ReadSkillEditorAsync(owner.TenantId, id, human); resources = await reads.ReadResourceEditorAsync(owner.TenantId, id, human);
        Assert.False(skills.CanCreate); Assert.False(resources.CanCreate);
        Assert.All(skills.Items, item => { Assert.False(item.CanManage); Assert.False(item.CanCustomize); });
        Assert.All(resources.Items, item => { Assert.False(item.CanManage); Assert.False(item.CanDelete); Assert.False(item.CanCopy); });
        await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, parent.AssignmentId, await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.ReadSkillEditorAsync(owner.TenantId, id, human).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.ReadResourceEditorAsync(owner.TenantId, id, human).AsTask());
    }
}
