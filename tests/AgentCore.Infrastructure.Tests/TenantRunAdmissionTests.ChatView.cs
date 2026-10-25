using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Public_conversation_view_and_management_hints_keep_chat_read_and_scoped_management_separate(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        var reader = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Read one Session", ["sessions.read"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, reader.RoleId, TenantSessionRules.Scope(setup.Tenant, setup.Session), await Revision());
        var metadata = await f.Sessions.ReadAsync(setup.Tenant, setup.Session, member);
        Assert.Equal(setup.Agent.Instance.Persona.Name, metadata.AgentName); Assert.Equal("examiner", metadata.AgentId); Assert.False(metadata.CanManage);
        var conversations = new TenantConversationService(f.Iam, (ITenantSessionStore)f.Iam, (ITenantConversationStore)f.Iam, f.Identity, f.Authorizer);
        await Assert.ThrowsAsync<AccessDeniedException>(() => conversations.ReadViewAsync(setup.Tenant, setup.Session, member).AsTask());
        var chat = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Chat with one agent", ["agents.chat"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, chat.RoleId, AccessScope.ForResource(setup.Tenant, "agent", setup.Agent.Instance.InstanceId.ToString("D")), await Revision());
        var view = await conversations.ReadViewAsync(setup.Tenant, setup.Session, member);
        Assert.Equal(metadata.AgentName, view.Agent.Name); Assert.False(view.Agent.VoiceAvailable); Assert.NotNull(view.Model);
        var manage = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Manage one Session", ["sessions.manage"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, manage.RoleId, TenantSessionRules.Scope(setup.Tenant, setup.Session), await Revision());
        Assert.False((await f.Authorizer.CheckAsync(member.PrincipalId, "sessions.manage", AccessScope.ForTenant(setup.Tenant))).Allowed);
        Assert.True((await f.Sessions.ReadAsync(setup.Tenant, setup.Session, member)).CanManage);
        Assert.True(Assert.Single((await f.Sessions.ListAsync(setup.Tenant, member)).Items).CanManage);
        var changed = await f.Sessions.ChangeAsync(setup.Tenant, member, new(setup.Session, 1, TenantSessionAction.Archive), await Revision()); Assert.True(changed!.CanManage);
        Assert.Equal(0, (await f.Sessions.ListAsync(setup.Tenant, member, includeArchived: false)).Total);
        Assert.Equal(1, (await f.Sessions.ListAsync(setup.Tenant, member, includeArchived: true)).Total);
        var selected = await f.Sessions.CreateAsync(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner, SessionMode.Text, await Revision(), 1, "dca30000-0000-4000-8000-000000000002");
        var selectedView = await conversations.ReadViewAsync(setup.Tenant, selected.Owner.SessionId, setup.Owner);
        Assert.Equal(ModelSelectionSource.User, selectedView.Model!.SelectionSource); Assert.Equal("dca30000-0000-4000-8000-000000000002", selectedView.Model.OfferingId);
        await f.Identity.SignOutAsync(member.AuthenticationSessionId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => conversations.ReadViewAsync(setup.Tenant, setup.Session, member).AsTask());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Run_cancel_hint_uses_exact_current_scope_and_never_authorizes_a_later_cancel(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "One Run control", ["agent-runs.read", "agent-runs.cancel"], await Revision());
        var read = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Session read", ["sessions.read"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, read.RoleId, TenantSessionRules.Scope(setup.Tenant, setup.Session), await Revision());
        var admitted = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Request", await Revision(), 1);
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, role.RoleId, TenantRunRules.Scope(setup.Tenant, admitted.RunId), await Revision());
        var saved = await f.RunService().ReadAsync(setup.Tenant, admitted.RunId, member); Assert.True(saved.CanCancel);
        Assert.False((await f.Authorizer.CheckAsync(member.PrincipalId, "agent-runs.cancel", AccessScope.ForTenant(setup.Tenant))).Allowed);
        await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, role.RoleId, role.Name, ["agent-runs.read"], await Revision());
        Assert.False((await f.RunService().ReadAsync(setup.Tenant, admitted.RunId, member)).CanCancel);
        var revision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.RunService().CancelAsync(setup.Tenant, admitted.RunId, member, revision, saved.Revision).AsTask());
    }
}
