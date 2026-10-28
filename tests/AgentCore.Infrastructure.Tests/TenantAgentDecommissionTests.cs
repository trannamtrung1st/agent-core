using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Workspaces;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed class TenantAgentDecommissionTests
{
    private sealed record Setup(Guid Tenant, TenantSessionInitiator Human, TenantAgent Agent);
    private static async Task<Setup> Prepare(Fixture f)
    {
        var first = await f.Identity.BootstrapAsync("retire@example.test", "Retiring owner", "strong-retirement-password", "Retiring organization", "retiring-organization");
        var login = (await f.Identity.SignInAsync("retire@example.test", "strong-retirement-password"))!;
        var human = new TenantSessionInitiator(first.AccountId, login.Session.SessionId, first.OwnerPrincipalId);
        var agent = await f.Agents.CreateAsync(human.PrincipalId, first.TenantId, "examiner", 1, (await f.Iam.ReadTenantAsync(first.TenantId))!.Tenant.Revision, initiator: human);
        return new(first.TenantId, human, agent);
    }
    private static ValueTask<TenantSecurityState?> State(Fixture f, Setup s) => f.Iam.ReadTenantAsync(s.Tenant);
    private static NativePlaywrightBrowser Browser(Fixture f) => new(new() { Enabled = false, ProfileRoot = Path.Combine(f.Root, "profiles") }, null);
    private static FileSpaceWorkspaceStore Workspace(Fixture f) => new(Path.Combine(f.Root, "workspaces"), (ITenantSpaceStore)f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantAgentDecommissionStore)f.Iam, f.Sandboxes);
    private static TenantAgentDecommissionCleanup Cleanup(Fixture f, IBrowser browser, ITenantAgentWorkspaceCleanup workspace, ITenantAgentDecommissionStore? store = null) =>
        new(store ?? (ITenantAgentDecommissionStore)f.Iam, browser, workspace, f.Ids, f.Clock);
    private static TenantAgentDecommissionService Service(Fixture f, IBrowser browser, ITenantAgentWorkspaceCleanup workspace, ITenantAgentDecommissionStore? store = null) =>
        new(f.Iam, store ?? (ITenantAgentDecommissionStore)f.Iam, f.Identity, f.Authorizer, f.Clock, new(), Cleanup(f, browser, workspace));
    private static Task<TenantAgent> Archive(Fixture f, Setup s) => f.Agents.SetLifecycleAsync(s.Human.PrincipalId, s.Tenant, s.Agent.Instance.InstanceId,
        AgentInstanceLifecycle.Archived, State(f,s).Result!.Tenant.Revision, ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant,s.Agent.Instance.InstanceId).Result!.Instance.Revision, initiator: s.Human).AsTask();
    private static string PrivateHome(Fixture f, Guid tenant, Guid instance) => AgentWorkspacePhysicalPaths.SpaceHomeDirectory(Path.Combine(f.Root, "workspaces"), tenant,
        ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(tenant, instance).GetAwaiter().GetResult()!.SpaceId);
    private static string Profile(Fixture f, Setup s) => Path.Combine(f.Root,"profiles","tenants",s.Tenant.ToString("D"),"agents",s.Agent.Instance.InstanceId.ToString("D"));
    private static string Seed(string directory, string content = "private retained state") { Directory.CreateDirectory(directory); var file=Path.Combine(directory,"sentinel"); File.WriteAllText(file,content);return file; }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Decommission_retires_current_human_owned_identity_grants_and_bytes_without_reusing_the_owner(bool sqlite)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true);var s=await Prepare(f);var browser=Browser(f);var workspace=Workspace(f);var service=Service(f,browser,workspace);
        try {
            var view=await service.ReadAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human);Assert.False(view.CanDecommission);Assert.Equal("archive_required",view.Blocker);
            var privateSpaceId=(await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(s.Tenant,s.Agent.Instance.InstanceId))!.SpaceId;
            var role=await f.Access.SaveRoleAsync(s.Human.PrincipalId,s.Tenant,null,"Retirement scoped role",["agents.read","agents.chat","workspaces.read"],(await State(f,s))!.Tenant.Revision);
            await f.Access.AssignRoleAsync(s.Human.PrincipalId,s.Tenant,s.Agent.PrincipalId,role.RoleId,AccessScope.ForTenant(s.Tenant),(await State(f,s))!.Tenant.Revision);
            await f.Access.AssignRoleAsync(s.Human.PrincipalId,s.Tenant,s.Human.PrincipalId,role.RoleId,TenantWorkspaceRules.Home(s.Tenant,privateSpaceId),(await State(f,s))!.Tenant.Revision);
            var credential=await f.Service.CreateAsync(s.Human.PrincipalId,s.Tenant,new("Retired binding",AgentCore.Domain.Credentials.CredentialKind.Password,AgentCore.Domain.Credentials.CredentialPurpose.General,null,[],"private-password"),(await State(f,s))!.Tenant.Revision);
            await f.BindingService.BindAsync(s.Human.PrincipalId,s.Tenant,s.Agent.Instance.InstanceId,credential.Credential.CredentialId,"retirement",(await State(f,s))!.Tenant.Revision,1,credential.Credential.Revision);
            var profile=Seed(Profile(f,s));var home=Seed(PrivateHome(f, s.Tenant, s.Agent.Instance.InstanceId));
            var sibling=Seed(Path.Combine(f.Root,"profiles","tenants",Guid.NewGuid().ToString("D"),"agents",Guid.NewGuid().ToString("D")),"preserve sibling");
            var outside=Seed(Path.Combine(f.Root,"outside"),"preserve link target");Directory.CreateSymbolicLink(Path.Combine(Path.GetDirectoryName(home)!,"linked-child"),Path.GetDirectoryName(outside)!);
            await Archive(f,s);view=await service.ReadAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human);Assert.True(view.CanDecommission);var operation=f.Ids.NewId();
            var result=await service.DecommissionAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human,view.SecurityRevision,view.InstanceRevision,operation);
            Assert.Equal("decommissioned",result.Status);Assert.Equal("completed",result.CleanupStatus);Assert.False(File.Exists(profile));Assert.False(File.Exists(home));Assert.Equal("preserve sibling",File.ReadAllText(sibling));Assert.Equal("preserve link target",File.ReadAllText(outside));
            var state=(await State(f,s))!;Assert.Equal(IdentityStatus.Removed,state.SystemIdentities.Single(i=>i.PrincipalId==s.Agent.PrincipalId).Status);Assert.Equal(IdentityStatus.Removed,state.Principals.Single(p=>p.PrincipalId==s.Agent.PrincipalId).Status);
            Assert.DoesNotContain(state.Assignments,a=>a.PrincipalId==s.Agent.PrincipalId||a.Scope==TenantWorkspaceRules.Home(s.Tenant,privateSpaceId));
            Assert.Null(await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant,s.Agent.Instance.InstanceId));Assert.Empty((await ((ITenantAgentStore)f.Iam).ListTenantAgentsAsync(s.Tenant,null,0,30)).Items);Assert.True(await f.Instances.HasBackingInstanceAsync(s.Agent.Instance.InstanceId));
            Assert.False((await f.Authorizer.CheckAsync(s.Human.PrincipalId,"agents.read",TenantAgentDecommissionRules.Scope(s.Tenant,s.Agent.Instance.InstanceId))).Allowed);
            var snapshot=(await ((ITenantAgentDecommissionStore)f.Iam).ReadAgentDecommissionAsync(s.Tenant,s.Agent.Instance.InstanceId))!;Assert.Equal(operation,snapshot.Receipt!.AuditId);Assert.Equal(s.Human.PrincipalId,snapshot.Receipt.ActorPrincipalId);
            if (f.Legacy is InMemoryCredentialStore memoryCredentials) Assert.Empty(memoryCredentials.ReadTenantBindings(s.Tenant,s.Agent.Instance.InstanceId));
            else { await using var db=f.Contexts!.CreateDbContext(); Assert.False(await db.AgentCredentialBindings.AnyAsync(b=>b.AgentInstanceId==s.Agent.Instance.InstanceId.ToString("D"))); }
            var again=await service.DecommissionAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human,view.SecurityRevision,view.InstanceRevision,operation);Assert.Equal(result,again);Assert.Single(await f.Iam.ReadAuditAsync(s.Tenant,200),a=>a.Action==TenantAgentDecommissionRules.Action);
            await Assert.ThrowsAnyAsync<Exception>(()=>f.Agents.SetLifecycleAsync(s.Human.PrincipalId,s.Tenant,s.Agent.Instance.InstanceId,AgentInstanceLifecycle.Active,state.Tenant.Revision,snapshot.Agent.Instance.Revision,initiator:s.Human).AsTask());
            var forged=state with {SystemIdentities=state.SystemIdentities.Select(i=>i.PrincipalId==s.Agent.PrincipalId?i with{Status=IdentityStatus.Active}:i).ToArray()};Assert.Throws<ArgumentException>(()=>IamStateRules.ValidateAgentContinuity(state.SystemIdentities,forged));
        } finally {await browser.StopAsync(CancellationToken.None);}
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Committed_decommission_recovers_uncertain_cleanup_after_logout_and_store_reopen(bool sqlite)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true);var s=await Prepare(f);var browser=Browser(f);var workspace=Workspace(f);var service=Service(f,browser,workspace);
        try {
            var profile=Seed(Profile(f,s));var home=Seed(PrivateHome(f, s.Tenant, s.Agent.Instance.InstanceId));await Archive(f,s);
            browser.TenantProfileDeleteProbe=_=>throw new IOException("controlled private deletion failure");browser.TenantProfileBeforeDeleteProbe=async()=>await f.Identity.SignOutAsync(s.Human.AuthenticationSessionId);
            var view=await service.ReadAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human);var operation=f.Ids.NewId();var result=await service.DecommissionAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human,view.SecurityRevision,view.InstanceRevision,operation);
            Assert.Equal("pending",result.CleanupStatus);Assert.True(File.Exists(profile));Assert.True(File.Exists(home));await Assert.ThrowsAsync<AccessDeniedException>(()=>service.ReadAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human).AsTask());
            IIamStore reopened=sqlite?new SqliteIamStore(f.Contexts!,builtIns:f.BuiltIns,clock:f.Clock):f.Iam;var store=(ITenantAgentDecommissionStore)reopened;
            await browser.StopAsync(CancellationToken.None);var cold=Browser(f);try {
                var cleanup=Cleanup(f,cold,workspace,store);Assert.Equal(1,await cleanup.RecoverAsync());Assert.False(File.Exists(profile));Assert.False(File.Exists(home));Assert.Equal(1,await cleanup.RecoverAsync());
                Assert.Single(await reopened.ReadAuditAsync(s.Tenant,200),a=>a.Action==TenantAgentDecommissionRules.Action);
                Assert.False(await store.IsAgentDecommissionedAsync(new(s.Tenant,s.Agent.Instance.InstanceId,Guid.NewGuid())));
            }finally{await cold.StopAsync(CancellationToken.None);}
        }finally{await browser.StopAsync(CancellationToken.None);}
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Decommission_keeps_reference_blockers_and_retained_deleted_session_ownership(bool sqlite)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true);var s=await Prepare(f);var browser=Browser(f);var service=Service(f,browser,Workspace(f));
        try {
            var session=await f.Sessions.CreateAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human,SessionMode.Text,(await State(f,s))!.Tenant.Revision,1);await Archive(f,s);
            var view=await service.ReadAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human);Assert.Equal("agent_in_use",view.Blocker);
            var error=await Assert.ThrowsAsync<SecurityConflictException>(()=>service.DecommissionAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human,view.SecurityRevision,view.InstanceRevision,f.Ids.NewId()).AsTask());Assert.Equal("agent_in_use",error.ReasonCode);
            await f.Sessions.ChangeAsync(s.Tenant,s.Human,new(session.Owner.SessionId,session.Revision,TenantSessionAction.Delete),(await State(f,s))!.Tenant.Revision);
            view=await service.ReadAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human);Assert.True(view.CanDecommission);
            await service.DecommissionAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human,view.SecurityRevision,view.InstanceRevision,f.Ids.NewId());
            Assert.Null(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(s.Tenant,session.Owner.SessionId));
            if(f.Memory is InMemoryMemoryStore memory) {Assert.True(memory.TenantSessionDeleted(s.Tenant,session.Owner.SessionId));Assert.NotNull(memory.RetainedTenantSessionOwnerSnapshot(session.Owner.SessionId));}
            else {await using var db=f.Contexts!.CreateDbContext();Assert.True(await db.Sessions.AnyAsync(row=>row.SessionId==session.Owner.SessionId.ToString("D")&&row.DurablyDeletedAtUtc!=null));}
        } finally {await browser.StopAsync(CancellationToken.None);}
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Decommission_retires_Private_context_and_protected_refs_while_Shared_files_and_other_members_survive(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var browser = Browser(f); var workspace = Workspace(f);
        try
        {
            async Task<long> Revision() => (await State(f, s))!.Tenant.Revision;
            var spaceStore = (ITenantSpaceStore)f.Iam;
            var spaces = new TenantSpaceService(f.Iam, spaceStore, (ITenantAgentStore)f.Iam, f.Authorizer, f.RunConfiguration(),
                (ITenantCredentialBindingStore)f.Iam, (ITenantCredentialStore)f.Iam, f.Ids, f.Clock);
            var own = (await spaceStore.FindPrivateSpaceAsync(s.Tenant, s.Agent.Instance.InstanceId))!;
            var sibling = await f.Agents.CreateAsync(s.Human.PrincipalId, s.Tenant, "examiner", 1, await Revision(), initiator: s.Human);
            var shared = await spaces.CreateAsync(s.Human.PrincipalId, s.Tenant, f.Ids.NewId(), "Shared room", "", "", await Revision());
            await spaces.SetMemberAsync(s.Human.PrincipalId, s.Tenant, shared.SpaceId, s.Agent.Instance.InstanceId, "", "", [], [], await Revision(), 1, 0);
            await spaces.SetMemberAsync(s.Human.PrincipalId, s.Tenant, shared.SpaceId, sibling.Instance.InstanceId, "", "", [], [], await Revision(), 2, 0);
            var environments = new TenantEnvironmentService(f.Iam, spaceStore, (ITenantEnvironmentStore)f.Iam, (ITenantSecretStore)f.Iam,
                (ITenantCredentialStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock);
            var secrets = new TenantSecretService(f.Iam, (ITenantSecretStore)f.Iam, f.Authorizer, new AgentCore.Infrastructure.Credentials.LocalCredentialProtector(Path.Combine(f.Root, "secret-keys")), f.Ids, f.Clock, f.Identity);
            var secret = await secrets.CreateAsync(s.Human.PrincipalId, s.Tenant, "Private test token", "", "protected-retirement-test-value", await Revision(), s.Human);
            var profile = await environments.SaveAsync(s.Human.PrincipalId, s.Tenant, own.SpaceId, f.Ids.NewId(), "Test", "", await Revision(), own.Revision, 0, s.Human);
            await environments.SaveVariableAsync(s.Human.PrincipalId, s.Tenant, own.SpaceId, profile.EnvironmentId, "TOKEN", AgentCore.Domain.Spaces.EnvironmentVariableKind.SecretReference,
                "", null, secret.SecretId, null, await Revision(), own.Revision, 1, 0, s.Human);
            await spaces.UpdateAsync(s.Human.PrincipalId, s.Tenant, own.SpaceId, own.Name, own.Description, own.Instructions, true, await Revision(), own.Revision);
            var privateFile = Seed(PrivateHome(f, s.Tenant, s.Agent.Instance.InstanceId));
            var sharedFile = Seed(AgentWorkspacePhysicalPaths.SpaceHomeDirectory(Path.Combine(f.Root, "workspaces"), s.Tenant, shared.SpaceId), "preserve Shared bytes");
            await Archive(f, s); var service = Service(f, browser, workspace); var view = await service.ReadAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Human);
            Assert.Equal("completed", (await service.DecommissionAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Human, view.SecurityRevision, view.InstanceRevision, f.Ids.NewId())).CleanupStatus);
            Assert.False(File.Exists(privateFile)); Assert.Equal("preserve Shared bytes", File.ReadAllText(sharedFile));
            Assert.Null(await spaceStore.FindSpaceAsync(s.Tenant, own.SpaceId)); Assert.Empty(await spaceStore.ReadMembershipsAsync(s.Tenant, own.SpaceId));
            Assert.Equal(sibling.Instance.InstanceId, Assert.Single(await spaceStore.ReadMembershipsAsync(s.Tenant, shared.SpaceId)).InstanceId);
            Assert.Equal(4, (await spaceStore.FindSpaceAsync(s.Tenant, shared.SpaceId))!.Revision);
            Assert.Empty(await ((ITenantEnvironmentStore)f.Iam).ListEnvironmentsAsync(s.Tenant, own.SpaceId));
            await secrets.DeleteAsync(s.Human.PrincipalId, s.Tenant, secret.SecretId, await Revision(), secret.Revision, s.Human);
            Assert.Null(await ((ITenantSecretStore)f.Iam).FindSecretAsync(s.Tenant, secret.SecretId));
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    private sealed class FailingWorkspaceCleanup(ITenantAgentWorkspaceCleanup inner) : ITenantAgentWorkspaceCleanup
    {
        internal bool Fail = true;
        public ValueTask PurgeDecommissionedAgentAsync(TenantBrowserAgentOwner owner, Func<CancellationToken, ValueTask<bool>> still, CancellationToken ct = default) =>
            Fail ? ValueTask.FromException(new IOException("Controlled owned workspace purge failure")) : ((ITenantAgentWorkspaceCleanup)inner).PurgeDecommissionedAgentAsync(owner, still, ct);

    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Decommission_workspace_purge_failure_recovers_exact_retirement_by_retry_or_cold_sweep_without_touching_active_owner(bool sqlite, bool sweep)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f); var browser = Browser(f);
        try
        {
            var workspace = new FailingWorkspaceCleanup(Workspace(f)); var service = Service(f, browser, workspace);
            var sibling = await f.Agents.CreateAsync(s.Human.PrincipalId, s.Tenant, "examiner", 1, (await State(f,s))!.Tenant.Revision, initiator:s.Human);
            var home = Seed(PrivateHome(f, s.Tenant, s.Agent.Instance.InstanceId));
            var siblingFile = Seed(PrivateHome(f, s.Tenant, sibling.Instance.InstanceId), "preserve active owner");
            await Archive(f,s); var view = await service.ReadAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human); var operation = f.Ids.NewId();
            var result = await service.DecommissionAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human,view.SecurityRevision,view.InstanceRevision,operation);
            Assert.Equal("pending",result.CleanupStatus); Assert.Equal("private retained state",File.ReadAllText(home));
            var store = (ITenantAgentDecommissionStore)f.Iam;
            Assert.True(await store.IsAgentDecommissionedAsync(new(s.Tenant,s.Agent.Instance.InstanceId,s.Agent.PrincipalId)));
            Assert.Null(await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant,s.Agent.Instance.InstanceId));
            workspace.Fail = false;
            if (sweep)
            {
                IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!,builtIns:f.BuiltIns,clock:f.Clock) : f.Iam;
                var cold = Browser(f);
                try { Assert.Equal(1,await Cleanup(f,cold,Workspace(f),(ITenantAgentDecommissionStore)reopened).RecoverAsync()); }
                finally { await cold.StopAsync(CancellationToken.None); }
            }
            else Assert.Equal("completed",(await service.DecommissionAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human,view.SecurityRevision,view.InstanceRevision,operation)).CleanupStatus);
            Assert.False(File.Exists(home)); Assert.Equal("preserve active owner",File.ReadAllText(siblingFile));
            var cleanup = Cleanup(f,browser,Workspace(f));
            Assert.False(await cleanup.ReconcileAsync(new(s.Tenant,sibling.Instance.InstanceId,sibling.PrincipalId)));
            Assert.False(await cleanup.ReconcileAsync(new(s.Tenant,s.Agent.Instance.InstanceId,Guid.NewGuid())));
            Assert.True(await cleanup.ReconcileAsync(new(s.Tenant,s.Agent.Instance.InstanceId,s.Agent.PrincipalId)));
            Assert.Equal("preserve active owner",File.ReadAllText(siblingFile));
            var receipt = Assert.Single(await f.Iam.ReadAuditAsync(s.Tenant,200),a=>a.Action==TenantAgentDecommissionRules.Action);
            Assert.Equal(operation,receipt.AuditId);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    private sealed class CommitBoundary(ITenantAgentDecommissionStore inner, Func<Task> before, Func<int,Task>? duringRead=null) : ITenantAgentDecommissionStore
    {
        private int reads;
        public async ValueTask<TenantAgentDecommissionSnapshot?> ReadAgentDecommissionAsync(Guid tenant, Guid instance, CancellationToken ct=default)
        {var value=await inner.ReadAgentDecommissionAsync(tenant,instance,ct);if(duringRead is not null)await duringRead(++reads);return value;}
        public async ValueTask<bool> TryDecommissionAgentAsync(Guid tenant,Guid instance,HumanSessionReference human,long security,long revision,AuthorizationAudit audit,CancellationToken ct=default)
        {await before();return await inner.TryDecommissionAgentAsync(tenant,instance,human,security,revision,audit,ct);}
        public ValueTask<TenantAgentDecommissionPage> ListAgentDecommissionCleanupAsync(Guid? after,int limit,CancellationToken ct=default)=>inner.ListAgentDecommissionCleanupAsync(after,limit,ct);
        public ValueTask<TenantWorkspaceReference.SpaceHome?> ReadDecommissionedPrivateWorkspaceAsync(TenantBrowserAgentOwner owner, CancellationToken ct = default) => inner.ReadDecommissionedPrivateWorkspaceAsync(owner, ct);
        public ValueTask<bool> CanPurgeDecommissionedAgentWorkspaceAsync(TenantBrowserAgentOwner owner,CancellationToken ct=default)=>inner.CanPurgeDecommissionedAgentWorkspaceAsync(owner,ct);
        public ValueTask<bool> IsAgentDecommissionedAsync(TenantBrowserAgentOwner owner,CancellationToken ct=default)=>inner.IsAgentDecommissionedAsync(owner,ct);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Decommission_preflight_rechecks_original_session_after_its_last_metadata_read(bool sqlite)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true);var s=await Prepare(f);var browser=Browser(f);
        try {
            var store=new CommitBoundary((ITenantAgentDecommissionStore)f.Iam,()=>Task.CompletedTask,async count=>{if(count==2)await f.Identity.SignOutAsync(s.Human.AuthenticationSessionId);});
            var service=Service(f,browser,Workspace(f),store);await Assert.ThrowsAsync<AccessDeniedException>(()=>service.ReadAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human).AsTask());
        }finally{await browser.StopAsync(CancellationToken.None);}
    }
    [Theory] [InlineData(false,"logout")] [InlineData(true,"logout")] [InlineData(false,"password")] [InlineData(true,"password")] [InlineData(false,"revision")] [InlineData(true,"revision")]
    public async Task Decommission_rechecks_original_login_and_security_inside_the_owning_commit(bool sqlite,string boundary)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true);var s=await Prepare(f);var browser=Browser(f);var workspace=Workspace(f);
        try {
            await Archive(f,s);var home=Seed(PrivateHome(f, s.Tenant, s.Agent.Instance.InstanceId));
            var store=new CommitBoundary((ITenantAgentDecommissionStore)f.Iam,async()=>{
                if(boundary=="logout")await f.Identity.SignOutAsync(s.Human.AuthenticationSessionId);
                else if(boundary=="password")await f.Identity.ChangePasswordAsync(s.Human.AccountId,"strong-retirement-password","new-strong-retirement-password");
                else await f.Access.SaveRoleAsync(s.Human.PrincipalId,s.Tenant,null,"Concurrent role",["agents.read"],(await State(f,s))!.Tenant.Revision);
            });
            var service=Service(f,browser,workspace,store);var view=await service.ReadAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human);
            await Assert.ThrowsAnyAsync<Exception>(()=>service.DecommissionAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human,view.SecurityRevision,view.InstanceRevision,f.Ids.NewId()).AsTask());
            Assert.Equal("private retained state", File.ReadAllText(home)); var kept = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(s.Tenant,s.Agent.Instance.InstanceId))!; Assert.Equal(AgentInstanceLifecycle.Archived, kept.Instance.Lifecycle); Assert.DoesNotContain(await f.Iam.ReadAuditAsync(s.Tenant,200),a=>a.Action==TenantAgentDecommissionRules.Action);
        }finally{await browser.StopAsync(CancellationToken.None);}
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Decommission_reader_and_agent_identity_cannot_substitute_for_the_current_human_deleter(bool sqlite)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true);var s=await Prepare(f);var browser=Browser(f);var service=Service(f,browser,Workspace(f));
        try {
            var reader=await f.Identity.RegisterAsync("retirement-reader@example.test","Reader","strong-reader-password");var state=(await State(f,s))!;
            var invitation=await f.Access.InviteAsync(s.Human.PrincipalId,s.Tenant,reader.LoginKey,state.Roles.Single(r=>r.Key=="reader").RoleId,state.Tenant.Revision);
            var member=await f.Access.AcceptInvitationAsync(reader.AccountId,s.Tenant,invitation.Invitation.InvitationId,invitation.Token);var login=(await f.Identity.SignInAsync(reader.LoginKey,"strong-reader-password"))!;
            var human=new HumanSessionReference(reader.AccountId,login.Session.SessionId,member.PrincipalId);await Archive(f,s);
            var view=await service.ReadAsync(s.Tenant,s.Agent.Instance.InstanceId,human);Assert.False(view.CanDecommission);
            await Assert.ThrowsAsync<AccessDeniedException>(()=>service.DecommissionAsync(s.Tenant,s.Agent.Instance.InstanceId,human,view.SecurityRevision,view.InstanceRevision,f.Ids.NewId()).AsTask());
            await Assert.ThrowsAsync<AccessDeniedException>(()=>service.ReadAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human with{PrincipalId=s.Agent.PrincipalId}).AsTask());
            Assert.Empty((await ((ITenantAgentDecommissionStore)f.Iam).ListAgentDecommissionCleanupAsync(null,100)).Items);
        }finally{await browser.StopAsync(CancellationToken.None);}
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Decommission_workspace_cleanup_checks_hidden_canonical_mounts_before_purging_bytes(bool sqlite)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true);var s=await Prepare(f);var browser=Browser(f);var workspace=Workspace(f);var service=Service(f,browser,workspace);
        try {
            Seed(Profile(f,s));var home=Seed(PrivateHome(f, s.Tenant, s.Agent.Instance.InstanceId));await Archive(f,s);
            browser.TenantProfileDeleteProbe=_=>throw new IOException("controlled first cleanup failure");var view=await service.ReadAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human);
            Assert.Equal("pending",(await service.DecommissionAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human,view.SecurityRevision,view.InstanceRevision,f.Ids.NewId())).CleanupStatus);
            // Controlled durable orphan from a pre-cutover lease: legacy enumeration hides this
            // owned Instance. This is a mount-fact fence test, not a real Docker allocation.
            var lease=new SandboxLease(Guid.NewGuid(),s.Agent.Instance.InstanceId,Guid.NewGuid(),1,new("old-orphan",1,"Old mount","",true,"sha256:"+new string('a',64)),SandboxLeaseState.NeedsReconciliation,"private-mount",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddMinutes(5));
            async Task Save(SandboxLease value) {
                if(f.Sandboxes is InMemorySandboxStore memory) {lock(memory.TenantGate)memory.SaveTenantLease(value);}
                else {await using var db=f.Contexts!.CreateDbContext();var old=await db.Set<SandboxLeaseRow>().SingleOrDefaultAsync(l=>l.SessionId==value.SessionId.ToString("D"));if(old is null)db.Add(SandboxRuntimePersistence.Row(value));else db.Entry(old).CurrentValues.SetValues(SandboxRuntimePersistence.Row(value));await db.SaveChangesAsync();}
            }
            await Save(lease);Assert.Empty(await f.Sandboxes.ListLeasesAsync());browser.TenantProfileDeleteProbe=null;
            var store=(ITenantAgentDecommissionStore)f.Iam;var owner=new TenantBrowserAgentOwner(s.Tenant,s.Agent.Instance.InstanceId,s.Agent.PrincipalId);Assert.False(await store.CanPurgeDecommissionedAgentWorkspaceAsync(owner));
            var cleanup=Cleanup(f,browser,workspace);Assert.False(await cleanup.ReconcileAsync(owner));Assert.True(File.Exists(home));
            await Save(lease with{Revision=2,State=SandboxLeaseState.Terminated});Assert.True(await store.CanPurgeDecommissionedAgentWorkspaceAsync(owner));Assert.True(await cleanup.ReconcileAsync(owner));Assert.False(File.Exists(home));
        }finally{await browser.StopAsync(CancellationToken.None);}
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Pending_profile_reset_blocks_decommission_until_factual_settlement(bool sqlite)
    {
        await using var f=await Fixture.Create(sqlite,builtInAuthority:true);var s=await Prepare(f);var browser=Browser(f);var service=Service(f,browser,Workspace(f));
        try {
            var at=DateTimeOffset.FromUnixTimeMilliseconds(f.Clock.GetUtcNow().ToUnixTimeMilliseconds());var reset=new AgentBrowserProfileReset(f.Ids.NewId(),s.Human.PrincipalId,Guid.NewGuid(),new('a',64),"pending","browser_profile_reset_started",at);var owner=new TenantBrowserAgentOwner(s.Tenant,s.Agent.Instance.InstanceId,s.Agent.PrincipalId);var profiles=(ITenantBrowserProfileStore)f.Iam;
            Assert.NotNull(await profiles.TryBeginTenantProfileResetAsync(owner,s.Human,(await State(f,s))!.Tenant.Revision,1,reset,new(reset.OperationId,s.Tenant,s.Human.PrincipalId,"browser.profile.reset",s.Agent.Instance.InstanceId.ToString("D"),"allowed",reset.ReasonCode,at)));
            await Archive(f,s);var view=await service.ReadAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human);Assert.False(view.CanDecommission);Assert.Equal("browser_profile_busy",view.Blocker);
            Assert.Equal("browser_profile_busy",(await Assert.ThrowsAsync<SecurityConflictException>(()=>service.DecommissionAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human,view.SecurityRevision,view.InstanceRevision,f.Ids.NewId()).AsTask())).ReasonCode);
            Assert.NotNull(await profiles.TrySettleTenantProfileResetAsync(owner,reset.OperationId,"unknown","browser_profile_reset_unknown",at,f.Ids.NewId()));Assert.True((await service.ReadAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human)).CanDecommission);
        }finally{await browser.StopAsync(CancellationToken.None);}
    }
    [Fact]
    public async Task Decommission_empty_schema_down_up_keeps_model_parity()
    {
        await using var f=await Fixture.Create(true,builtInAuthority:true);
        await using var db=new AgentCoreDbContext(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={Path.Combine(f.Root, "decommission-roundtrip.db")}").Options);
        await db.GetService<IMigrator>().MigrateAsync("20261020090000_TenantAgentDecommission");
        await db.GetService<IMigrator>().MigrateAsync("20261020080000_SystemBrowserPrivacyAuthority");await db.Database.MigrateAsync();Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Decommission_sqlite_guards_reject_identity_revival_and_unsafe_downgrade()
    {
        await using var f=await Fixture.Create(true,builtInAuthority:true);var s=await Prepare(f);var browser=Browser(f);var service=Service(f,browser,Workspace(f));
        try {
            await Archive(f,s);var view=await service.ReadAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human);await service.DecommissionAsync(s.Tenant,s.Agent.Instance.InstanceId,s.Human,view.SecurityRevision,view.InstanceRevision,f.Ids.NewId());
            await using var db=f.Contexts!.CreateDbContext();Assert.False(db.Database.HasPendingModelChanges());
            foreach(var (sql,key) in new (string,object)[]{ ("UPDATE SystemIdentities SET Status=0 WHERE PrincipalId={0}",s.Agent.PrincipalId),("UPDATE Principals SET Status=0 WHERE PrincipalId={0}",s.Agent.PrincipalId),("UPDATE AgentInstances SET Lifecycle='Active' WHERE InstanceId={0}",s.Agent.Instance.InstanceId.ToString("D")),("DELETE FROM SystemIdentities WHERE PrincipalId={0}",s.Agent.PrincipalId),("UPDATE SystemIdentities SET AgentInstanceId=NULL WHERE PrincipalId={0}",s.Agent.PrincipalId) })
                await Assert.ThrowsAsync<SqliteException>(()=>db.Database.ExecuteSqlRawAsync(sql,key));
            await Assert.ThrowsAnyAsync<Exception>(()=>db.GetService<IMigrator>().MigrateAsync("20261020080000_SystemBrowserPrivacyAuthority"));
            Assert.True(await ((ITenantAgentDecommissionStore)f.Iam).IsAgentDecommissionedAsync(new(s.Tenant,s.Agent.Instance.InstanceId,s.Agent.PrincipalId)));
        }finally{await browser.StopAsync(CancellationToken.None);}
    }
}
