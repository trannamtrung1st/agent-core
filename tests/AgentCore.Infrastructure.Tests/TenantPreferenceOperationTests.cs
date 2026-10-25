using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Providers;
using AgentCore.Application.Models;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantPreferenceOperationTests
{
    private sealed record Environment(Guid Tenant, TenantSessionInitiator Owner, TenantAgent Agent, AgentRun Run);
    private static TenantPreferenceOperationService Service(Fixture f, IIamStore? store = null, ITenantPreferenceOperationStore? effects = null, ITenantAuthorizationAuditStore? audits = null)
    {
        var current = store ?? f.Iam;
        return new(effects ?? (ITenantPreferenceOperationStore)current, new(current, (ITenantRunStore)current, (ITenantAgentStore)current, new(current), new(audits ?? (ITenantAuthorizationAuditStore)f.Iam, f.Ids, f.Clock)),
            f.RunConfiguration(), ModelCatalogFactory.Synthetic(), SyntheticProviderAliases.Default, f.Ids, f.Clock, (ITenantApprovalStore)current);
    }
    private static async Task<Environment> Setup(Fixture f, bool permissions = true, bool update = true,
        HarnessManagementMode mode = HarnessManagementMode.Managed, bool autoApply = true, bool restrictedRequester = false)
    {
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, owner.TenantId, "examiner", 1, await Revision());
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!; var at = f.Clock.GetUtcNow();
        agent = (await ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
            new(agent.Instance.InstanceId, 1, HarnessManagement: new(new(mode, [HarnessManagementScope.Settings], [], [], ManagedAutoApply: autoApply ? [ManagementAutoApply.ConversationPreferences] : []), 2)), at,
            new(f.Ids.NewId(), owner.TenantId, human.PrincipalId, "agents.self-management.update", agent.Instance.InstanceId.ToString("D"), "allowed", "configuration_updated", at)))!;
        var ownSpace = (await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(owner.TenantId, agent.Instance.InstanceId))!;
        var context = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Read own context", ["spaces.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, agent.PrincipalId, context.RoleId, TenantSpaceService.Scope(owner.TenantId, ownSpace.SpaceId), await Revision());
        if (permissions)
        {
            var role = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Own preferences", update ? ["agents.read", "agents.update"] : ["agents.read"], await Revision());
            await f.Access.AssignRoleAsync(human.PrincipalId, owner.TenantId, agent.PrincipalId, role.RoleId,
                AccessScope.ForResource(owner.TenantId, "agent", agent.Instance.InstanceId.ToString("D")), await Revision());
        }
        if (restrictedRequester)
        {
            var account = await f.Identity.RegisterAsync("requester@example.test", "Requester", "strong-requester-password");
            var role = await f.Access.SaveRoleAsync(human.PrincipalId, owner.TenantId, null, "Chat requester", ["agents.read", "agents.chat", "sessions.create", "sessions.read"], await Revision());
            var invitation = await f.Access.InviteAsync(human.PrincipalId, owner.TenantId, account.LoginKey, role.RoleId, await Revision());
            var member = await f.Access.AcceptInvitationAsync(account.AccountId, owner.TenantId, invitation.Invitation.InvitationId, invitation.Token);
            var auth = (await f.Identity.SignInAsync(account.LoginKey, "strong-requester-password"))!.Session;
            human = new(account.AccountId, auth.SessionId, member.PrincipalId);
        }
        var session = await f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var admission = await f.RunService().SubmitTextAsync(owner.TenantId, session.Owner.SessionId, human, f.Ids.NewId(), "Update my allowed conversation preferences", await Revision(), 1);
        var queued = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.TenantId, admission.RunId))!;
        at = f.Clock.GetUtcNow();
        var run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentCore.Application.Execution.AgentRunCommand.Claim(queued.Revision, at, f.Ids.NewId(), at.AddMinutes(5)), f.Ids.NewId());
        return new(owner.TenantId, human, agent, run);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Allowed_managed_preferences_commit_with_one_direct_receipt_and_no_approval_then_replay_after_reopen(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var service = Service(f); var reference = new TenantRunReference(setup.Tenant, setup.Run.AgentRunId);
        var result = await service.ExecuteAsync(reference, setup.Run.Claim!.Generation, "preference-call", """{"set":{"language":"vi","responseLength":"concise"},"clear":[]}""");
        Assert.Equal(OperationDecisionKind.Allowed, result.Decision); Assert.NotNull(result.Receipt); Assert.Null(result.Receipt.RequestId);
        Assert.Equal("preference-call", result.Receipt.SourceCallId); Assert.NotNull(result.Receipt.ParametersHash);
        var current = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(setup.Tenant, setup.Agent.Instance.InstanceId))!;
        Assert.Equal(3, current.Instance.Revision); Assert.Equal(setup.Agent.Instance.Persona, current.Instance.Persona);
        Assert.Equal("vi", current.Instance.SettingsOverrides!.Language!.Value); Assert.Equal("concise", current.Instance.SettingsOverrides.ResponseLength!.Value);
        Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(setup.Tenant, null, null, new(0, 100))).Items);
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, clock: f.Clock) : f.Iam;
        var replay = await Service(f, reopened).ExecuteAsync(reference, setup.Run.Claim.Generation, "preference-call", """{"clear":[],"set":{"responseLength":"concise","language":"vi"}}""");
        Assert.Equal(result.Receipt, replay.Receipt);
        Assert.Equal(3, (await ((ITenantAgentStore)reopened).FindTenantAgentAsync(setup.Tenant, setup.Agent.Instance.InstanceId))!.Instance.Revision);
        Assert.Single(await reopened.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "operations.preferences.apply");
        ICoreEventStore events = f.Contexts is null ? ((InMemoryAgentInstanceStore)f.Instances).CoreEvents : new SqliteCoreEventStore(f.Contexts);
        Assert.DoesNotContain(await events.PendingAsync(), e => e.Event.Owner.AgentInstanceId == setup.Agent.Instance.InstanceId && e.Event.Key == "instance.config_changed");
        var run = (await ((ITenantRunStore)reopened).FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!;
        Assert.Null(run.Owner.ProfileIdOrNull); Assert.Equal(AgentRunSideEffectDisposition.Succeeded, run.SideEffect.Disposition);
    }
}
