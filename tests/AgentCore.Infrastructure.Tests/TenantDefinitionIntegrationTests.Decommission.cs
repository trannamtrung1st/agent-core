using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Decommissioned_owner_tombstone_fences_reuse_without_blocking_unreferenced_Definition_retirement(bool sqlite)
    {
        await using var f=await Fixture.Create(sqlite);var first=await f.Identity.BootstrapAsync("owner@example.test","Owner","strong-owner-password","Alpha","alpha");
        var login=(await f.Identity.SignInAsync("owner@example.test","strong-owner-password"))!;var human=new HumanSessionReference(first.AccountId,login.Session.SessionId,first.OwnerPrincipalId);
        var draft=await f.Service.CreateAsync(first.OwnerPrincipalId,first.TenantId,"Retirable Definition",1);var id=Guid.Parse(draft.DefinitionId);await f.Service.PublishAsync(first.OwnerPrincipalId,first.TenantId,draft.DraftId,2,1);
        var directory=new DirectoryInfo(AppContext.BaseDirectory);while(directory is not null&&!Directory.Exists(Path.Combine(directory.FullName,"agents")))directory=directory.Parent;
        var templates=new FileAgentDefinitionStore(Path.Combine(directory!.FullName,"agents"),SyntheticProviderAliases.Default);
        var agents=new TenantAgentService(f.Iam,(ITenantAgentStore)f.Iam,templates,f.Authorizer,f.Ids,TimeProvider.System,ownedDefinitions:f.Definitions);
        var agent=await agents.CreateFromPublicationAsync(human.PrincipalId,first.TenantId,id,1,3,initiator:human);await agents.SetLifecycleAsync(human.PrincipalId,first.TenantId,agent.Instance.InstanceId,AgentInstanceLifecycle.Archived,4,1,initiator:human);
        var witness=new AdminDefinitionDeleteWitness([],[new(1,1)]);Assert.Equal("definition_in_use",(await Assert.ThrowsAsync<SecurityConflictException>(()=>f.Service.DeleteAsync(human.PrincipalId,first.TenantId,id,witness,5).AsTask())).ReasonCode);
        var root=Path.Combine(Path.GetTempPath(),"retired-definition-owner-"+Guid.NewGuid().ToString("N"));var browser=new NativePlaywrightBrowser(new(){Enabled=false,ProfileRoot=Path.Combine(root,"profiles")},null);
        try {
            var workspace=new FileAgentInstanceWorkspaceStore(Path.Combine(root,"workspaces"),f.Instances,f.Sandbox,tenantAgents:(ITenantAgentStore)f.Iam);var store=(ITenantAgentDecommissionStore)f.Iam;
            var cleanup=new TenantAgentDecommissionCleanup(store,browser,workspace,f.Ids,TimeProvider.System);var service=new TenantAgentDecommissionService(f.Iam,store,f.Identity,f.Authorizer,TimeProvider.System,new(),cleanup);
            var before=await service.ReadAsync(first.TenantId,agent.Instance.InstanceId,human);await service.DecommissionAsync(first.TenantId,agent.Instance.InstanceId,human,before.SecurityRevision,before.InstanceRevision,f.Ids.NewId());
            await f.Service.DeleteAsync(human.PrincipalId,first.TenantId,id,witness,6);Assert.Null(await f.Definitions.FindDefinitionAsync(first.TenantId,id));Assert.True(await f.Instances.HasBackingInstanceAsync(agent.Instance.InstanceId));
            Assert.Null(await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(first.TenantId,agent.Instance.InstanceId));
            await Assert.ThrowsAsync<AccessDeniedException>(()=>agents.SetLifecycleAsync(human.PrincipalId,first.TenantId,agent.Instance.InstanceId,AgentInstanceLifecycle.Active,7,3,initiator:human).AsTask());
        }finally{await browser.StopAsync(CancellationToken.None);if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
