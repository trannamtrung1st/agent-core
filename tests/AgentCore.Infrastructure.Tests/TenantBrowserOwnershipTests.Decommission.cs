using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Workspaces;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserOwnershipTests
{
    [TenantChromiumTheory] [InlineData(false)] [InlineData(true)]
    public async Task Decommission_closes_actual_owned_native_profile_after_deleted_Session_without_touching_live_sibling(bool sqlite)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true);var bootstrap=await f.Identity.BootstrapAsync("native-retire@example.test","Native retire owner","strong-native-retire-password","Native retirement","native-retirement");
        var login=(await f.Identity.SignInAsync("native-retire@example.test","strong-native-retire-password"))!;var human=new TenantSessionInitiator(bootstrap.AccountId,login.Session.SessionId,bootstrap.OwnerPrincipalId);var tenant=bootstrap.TenantId;
        async Task<long> Revision()=>(await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        async Task<(TenantAgent Agent,TenantSessionMetadata Session)> Agent() {var agent=await f.Agents.CreateAsync(human.PrincipalId,tenant,"examiner",1,await Revision(),initiator:human);return(agent,await f.Sessions.CreateAsync(tenant,agent.Instance.InstanceId,human,SessionMode.Text,await Revision(),1));}
        var first=await Agent();var second=await Agent();var browser=Native(Path.Combine(f.Root,"profiles"));await browser.StartAsync(CancellationToken.None);
        try {
            var owner=new TenantBrowserOwner(tenant,first.Agent.Instance.InstanceId,first.Agent.PrincipalId,first.Session.Owner.SessionId);var sibling=new TenantBrowserOwner(tenant,second.Agent.Instance.InstanceId,second.Agent.PrincipalId,second.Session.Owner.SessionId);
            await Open(browser,owner);await Open(browser,sibling);var page=Page(browser,owner);var other=Page(browser,sibling);await page.EvaluateAsync("() => localStorage.setItem('private','retire-only-this-profile')");
            await f.Sessions.ChangeAsync(tenant,human,new(owner.SessionId,first.Session.Revision,TenantSessionAction.Delete),await Revision());
            var archived=await f.Agents.SetLifecycleAsync(human.PrincipalId,tenant,owner.AgentInstanceId,AgentInstanceLifecycle.Archived,await Revision(),1,initiator:human);Assert.False(page.IsClosed);
            var workspace=new FileSpaceWorkspaceStore(Path.Combine(f.Root,"workspaces"),(ITenantSpaceStore)f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantAgentDecommissionStore)f.Iam, f.Sandboxes);
            var store=(ITenantAgentDecommissionStore)f.Iam;var cleanup=new TenantAgentDecommissionCleanup(store,browser,workspace,f.Ids,f.Clock);var service=new TenantAgentDecommissionService(f.Iam,store,f.Identity,f.Authorizer,f.Clock,new(),cleanup);
            var before=await service.ReadAsync(tenant,owner.AgentInstanceId,human);Assert.True(before.CanDecommission);var result=await service.DecommissionAsync(tenant,owner.AgentInstanceId,human,before.SecurityRevision,archived.Instance.Revision,f.Ids.NewId());
            Assert.Equal("completed",result.CleanupStatus);Assert.True(page.IsClosed);Assert.False(other.IsClosed);Assert.Null(browser.ContextFor(owner.SessionId));Assert.NotNull(browser.ContextFor(sibling.SessionId));
            Assert.False((await browser.ReadTenantProfileAsync(owner.Agent)).ProfilePresent);Assert.True((await browser.ReadTenantProfileAsync(sibling.Agent)).ProfilePresent);
        }finally{await browser.StopAsync(CancellationToken.None);}
    }
}
