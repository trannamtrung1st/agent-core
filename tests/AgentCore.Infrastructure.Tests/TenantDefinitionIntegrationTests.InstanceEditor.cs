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
    public async Task Tenant_instance_editor_keeps_profile_settings_and_exact_access_on_one_read_witness(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var agent = await agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision()); var id = agent.Instance.InstanceId;
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        var editor = new TenantAgentReadService(f.Iam, (ITenantAgentStore)f.Iam, configuration, f.Identity, f.Authorizer, agents);
        var view = await editor.ReadEditorAsync(owner.TenantId, id, human);
        var controls = await editor.ReadControlsAsync(owner.TenantId, id, human);
        Assert.Equal(view.SecurityRevision, controls.SecurityRevision); Assert.True(controls.CanUpdate);
        Assert.Equal(view.Agent.Instance.Revision, controls.Budgets.InstanceRevision);
        Assert.Equal(view.Agent.Instance.Revision, controls.Management.Policy.InstanceRevision);
        Assert.Equal(view.SecurityRevision, controls.Budgets.SecurityRevision);
        Assert.Equal(view.SecurityRevision, controls.Management.SecurityRevision);
        Assert.True(view.CanUpdate); Assert.Equal(await Revision(), view.SecurityRevision);
        Assert.Equal("Alex", view.Agent.Instance.Persona.Name); Assert.NotEmpty(view.Settings);
        Assert.All(view.Settings, section => Assert.Equal(view.Agent.Instance.Revision, section.InstanceRevision));
        Assert.DoesNotContain(view.Settings, section => section.Section == "sandboxPolicy"); Assert.Contains("sandboxPolicy", view.UnavailableSettings);
        var changed = await configuration.UpdatePersonaAsync(owner.OwnerPrincipalId, owner.TenantId, id, agent.Instance.Persona with { Name = "Alex operations" }, view.SecurityRevision, 1, 1);
        await configuration.PatchSettingsAsync(owner.OwnerPrincipalId, owner.TenantId, id, "conversationPolicy", await Revision(), changed.Instance.Revision, Fields(new { language = "fr-FR" }), []);
        view = await editor.ReadEditorAsync(owner.TenantId, id, human);
        Assert.Equal("Alex operations", view.Agent.Instance.Persona.Name);
        Assert.Equal("fr-FR", view.Settings.Single(s => s.Section == "conversationPolicy").Effective["language"]?.ToString());
        var account = await f.Identity.RegisterAsync("reader@example.test", "Scoped reader", "strong-reader-password");
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var invitation = await f.Access.InviteAsync(owner.OwnerPrincipalId, owner.TenantId, account.LoginKey, state.Roles.Single(r => r.Key == "approver").RoleId, state.Tenant.Revision);
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, owner.TenantId, invitation.Invitation.InvitationId, invitation.Token);
        var role = await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, owner.TenantId, null, "Exact editor reader", ["agents.read"], await Revision());
        var grant = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, user.PrincipalId, role.RoleId, AccessScope.ForResource(owner.TenantId, "agent", id.ToString("D")), await Revision());
        login = (await f.Identity.SignInAsync("reader@example.test", "strong-reader-password"))!.Session;
        human = new(account.AccountId, login.SessionId, user.PrincipalId);
        Assert.False((await editor.ReadControlsAsync(owner.TenantId, id, human)).CanUpdate);
        Assert.False((await editor.ReadEditorAsync(owner.TenantId, id, human)).CanUpdate);
        await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, grant.AssignmentId, await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(() => editor.ReadEditorAsync(owner.TenantId, id, human).AsTask());
    }

    [Theory]
    [InlineData(false, "persona")]
    [InlineData(true, "persona")]
    [InlineData(false, "security")]
    [InlineData(true, "security")]
    [InlineData(false, "logout")]
    [InlineData(true, "logout")]
    public async Task Tenant_instance_editor_rejects_mixed_configuration_and_retired_human_during_definition_IO(bool sqlite, string change)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var agent = await agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision()); var id = agent.Instance.InstanceId;
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        var gated = ConfigurationServices(f, wrapBuiltIns: inner => new AfterEditorDefinition(inner, async () =>
        {
            if (change == "persona") await configuration.UpdatePersonaAsync(owner.OwnerPrincipalId, owner.TenantId, id, agent.Instance.Persona with { Name = "Concurrent persona" }, await Revision(), 1, 1);
            else if (change == "security") await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, owner.TenantId, null, "Concurrent role", ["agents.read"], await Revision());
            else await f.Identity.SignOutAsync(login.SessionId);
        }));
        var editor = new TenantAgentReadService(f.Iam, (ITenantAgentStore)f.Iam, gated.Configuration, f.Identity, f.Authorizer, agents);
        if (change == "logout")
        {
            var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => editor.ReadEditorAsync(owner.TenantId, id, human).AsTask());
            Assert.Equal("initiator_unavailable", denied.ReasonCode);
        }
        else
        {
            var conflict = await Assert.ThrowsAsync<SecurityConflictException>(() => editor.ReadEditorAsync(owner.TenantId, id, human).AsTask());
            Assert.Equal("stale_revision", conflict.ReasonCode);
            var current = await editor.ReadEditorAsync(owner.TenantId, id, human);
            Assert.Equal(change == "persona" ? "Concurrent persona" : "Alex", current.Agent.Instance.Persona.Name);
            Assert.All(current.Settings, item => Assert.Equal(current.Agent.Instance.Revision, item.InstanceRevision));
        }
    }

    private sealed class AfterEditorDefinition(IBuiltInAgentDefinitionStore inner, Func<Task> after) : IBuiltInAgentDefinitionStore
    {
        private Func<Task>? pending = after;
        public async ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default)
        {
            var values = await inner.ListAsync(cancellationToken);
            if (Interlocked.Exchange(ref pending, null) is { } action) await action();
            return values;
        }
        public async ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default)
        {
            var value = await inner.GetAsync(id, version, cancellationToken);
            if (Interlocked.Exchange(ref pending, null) is { } action) await action();
            return value;
        }
    }
}
