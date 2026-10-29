using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    private sealed class ManagementSandboxReady : ISandboxReadiness
    { public bool IsAvailable=>true;public string State=>"Ready";public string SafeMessage=>"Synthetic management qualification"; }
    private sealed class ManagementSandboxVerification(ISandboxVerificationHost host,TimeProvider time) : ITenantSandboxVerifier
    {
        public int Calls; public string HostScope=>host.Scope;
        public ValueTask<SandboxVerificationOutcome> VerifyAsync(SandboxVerificationAttempt attempt,SandboxProfile profile,SandboxImageAuthorization proof,CancellationToken ct=default)
        {
            Calls++;
            return ValueTask.FromResult(new SandboxVerificationOutcome(new(profile.Id,profile.Revision,time.GetUtcNow(),true,"Synthetic profile prerequisite",
                ContractVersion:SandboxProfileVerification.CurrentContractVersion,TenantId:attempt.TenantId,IncludesInitialization:!attempt.ImageOnly,HostScope:host.Scope,
                InstanceId:attempt.InstanceId,InstanceRevision:attempt.InstanceRevision,RegistryCredentialId:attempt.RegistryCredentialId,RegistryCredentialRevision:attempt.RegistryCredentialRevision,RegistrySecretRevision:attempt.RegistrySecretRevision),true));
        }
        public ValueTask<bool> CleanupAsync(SandboxVerificationCleanup claim,CancellationToken ct=default)=>ValueTask.FromResult(true);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Management_Sandbox_profile_selection_reuses_verified_tenant_setup_and_secondary_raw_permissions_without_execution(bool sqlite)
    {
        var root=Path.Combine(Path.GetTempPath(),"management-sandbox-selection-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var model=new ManagementMutationModel("settings.patch");
            await using WebApplicationFactory<Program> factory=sqlite?new TenantApprovalSqliteFactory(root,model):new ApprovalExecutionFactory();
            await using var host=factory.WithWebHostBuilder(b=>b.ConfigureTestServices(s=> {
                s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model));s.Replace(ServiceDescriptor.Singleton<ISandboxReadiness,ManagementSandboxReady>());
                s.AddSingleton<ManagementSandboxVerification>();s.Replace(ServiceDescriptor.Singleton<ITenantSandboxVerifier>(p=>p.GetRequiredService<ManagementSandboxVerification>()));
            }));
            using var owner=host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress=new("https://localhost"),AllowAutoRedirect=false });await Login(owner,"owner@example.test","explicit-synthetic-test-password");
            var tenant=(await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();var api=$"/api/v2/tenants/{tenant}";
            var profile=await Json(await owner.PostAsJsonAsync(api+"/sandbox-profiles",new TenantSandboxProfileSaveRequest(await Revision(owner,tenant),0,"Management profile","Synthetic",true,"ghcr.io/acme/tool@sha256:"+new string('a',64))));
            var profileId=profile.GetProperty("profile").GetProperty("id").GetString()!;
            Assert.Equal(HttpStatusCode.OK,(await owner.PostAsJsonAsync(api+$"/sandbox-profiles/{profileId}/verify",new TenantSandboxVerificationRequest(await Revision(owner,tenant),1))).StatusCode);
            var draft=await Json(await owner.PostAsJsonAsync(api+"/agent-definitions",new { name="Sandbox configuration editor",expectedRevision=await Revision(owner,tenant) }));var draftId=draft.GetProperty("draftId").GetGuid();var definition=draft.GetProperty("definitionId").GetGuid();
            var candidate=JsonNode.Parse(draft.GetProperty("candidate").GetRawText())!;
            candidate["sandboxPolicy"]=JsonSerializer.SerializeToNode(new { defaultProfileId=profileId,allowedProfileIds=new[] { profileId } });
            candidate["executionPolicy"]=JsonSerializer.SerializeToNode(new { harness=Array.Empty<string>(),knowledgeSources=Array.Empty<string>(),toolAllowlist=Array.Empty<string>(),capabilities=new { mode="Selected",resolvedCapabilities=new[] { "settings.inspect","settings.patch" } },projection=new { alwaysCapabilities=Array.Empty<string>() } });
            candidate["selfManagement"]=JsonSerializer.SerializeToNode(new { mode="Managed",scopes=new[] { "Settings" },sources=Array.Empty<string>(),eligibleTools=Array.Empty<string>() });
            Assert.Equal(HttpStatusCode.OK,(await owner.PutAsJsonAsync(api+$"/definition-drafts/{draftId}",new { candidate,expectedRevision=await Revision(owner,tenant),expectedDraftRevision=1 })).StatusCode);
            Assert.Equal(HttpStatusCode.OK,(await owner.PostAsJsonAsync(api+$"/definition-drafts/{draftId}/publish",new { expectedRevision=await Revision(owner,tenant),expectedDraftRevision=2 })).StatusCode);
            var created=await Json(await owner.PostAsJsonAsync(api+"/agents/from-publication",new { definitionId=definition,version=1,expectedRevision=await Revision(owner,tenant) }));var instance=created.GetProperty("instance").GetProperty("instanceId").GetGuid();
            await GrantNativePrivateSpaceRead(owner, tenant, instance);var principal=created.GetProperty("principalId").GetGuid();
            var role=await Json(await owner.PostAsJsonAsync(api+"/roles",new { name="Sandbox configuration writer",permissions=new[] { "agents.read","agents.update","agents.chat","sessions.read","sandbox.read","sandbox.manage" },expectedRevision=await Revision(owner,tenant) }));
            Assert.Equal(HttpStatusCode.Created,(await owner.PostAsJsonAsync(api+"/role-assignments",new { principalId=principal,roleId=role.GetProperty("roleId").GetGuid(),expectedRevision=await Revision(owner,tenant) })).StatusCode);
            model.Arguments=JsonSerializer.Serialize(new { expectedInstanceRevision=1,section="sandboxPolicy",set=new { profileId },clear=Array.Empty<string>() });
            var session=(await Json(await owner.PostAsJsonAsync(api+"/sessions",new { retentionKind="saved", agentInstanceId=instance,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=1 }))).GetProperty("sessionId").GetGuid();
            var submitted=await owner.PostAsJsonAsync(api+$"/sessions/{session}/messages",new { messageId=Guid.NewGuid(),text="Select this exact previously verified profile without running setup or commands.",expectedRevision=await Revision(owner,tenant),expectedSessionRevision=1 });
            Assert.Equal(HttpStatusCode.Accepted,submitted.StatusCode);var runId=(await Json(submitted)).GetProperty("runId").GetGuid();if(factory is ApprovalExecutionFactory memory)Volatile.Write(ref memory.Enabled,true);
            var runs=host.Services.GetRequiredService<ITenantRunStore>();var pending=await AwaitManagementRun(runs,tenant,runId,true);
            Assert.True(pending.Status==AgentRunStatus.WaitingForApproval,$"{pending.Status}: {pending.Failure?.Code}: "+string.Join("\n",model.Requests.SelectMany(r=>r.Messages).Where(m=>m.Name=="settings.patch").Select(m=>m.Text)));
            var request=(await host.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant,pending.ApprovalRequestId!.Value))!;
            Assert.Equal(HttpStatusCode.OK,(await owner.PostAsJsonAsync(api+$"/approvals/{request.RequestId}/decision",new { decision="approve",actionHash=request.Operation.ActionHash,expectedRevision=await Revision(owner,tenant),expectedRequestRevision=request.Revision })).StatusCode);
            var final=await AwaitManagementSuccessor(runs,tenant,session,request.RequestId,runId);Assert.True(final.Status==AgentRunStatus.Completed,$"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}");
            var settings=await Json(await owner.GetAsync(api+$"/agents/{instance}/settings"));var saved=settings.GetProperty("items").EnumerateArray().Single(s=>s.GetProperty("section").GetString()=="sandboxPolicy");
            Assert.Equal(profileId,saved.GetProperty("effective").GetProperty("profileId").GetString());Assert.Equal("instance",saved.GetProperty("sources").GetProperty("profileId").GetString());
            Assert.Equal("profileId",Assert.Single(saved.GetProperty("overrides").EnumerateObject()).Name);Assert.Equal(1,host.Services.GetRequiredService<ManagementSandboxVerification>().Calls);
            Assert.Single(TenantManagementReadReferences.Read(final.Checkpoint));Assert.Equal(1,final.Admission.Configuration!.InstanceRevision);
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }
}
