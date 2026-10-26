using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, "changed")] [InlineData(true, "changed")]
    [InlineData(false, "freeze")] [InlineData(true, "freeze")]
    [InlineData(false, "read_revoked")] [InlineData(true, "read_revoked")]
    [InlineData(false, "write_revoked")] [InlineData(true, "write_revoked")]
    public async Task Management_command_changed_or_revoked_consent_preserves_cancellation_without_a_configuration_effect(bool sqlite, string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "management-mutation-boundary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ManagementMutationModel("settings.patch") { Arguments = ManagementCommand("settings.patch", "") };
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var (tenant, instance) = await SetupManagementMutation(host, owner); var api = $"/api/v2/tenants/{tenant}";
            var created = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 3 })); var session = created.GetProperty("sessionId").GetGuid();
            var response = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Request this exact settings change before a boundary changes.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); var runId = (await Json(response)).GetProperty("runId").GetGuid();
            if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            var pending = await AwaitManagementRun(host.Services.GetRequiredService<ITenantRunStore>(), tenant, runId, true); Assert.Equal(AgentRunStatus.WaitingForApproval,pending.Status);
            var approvals = host.Services.GetRequiredService<ITenantApprovalStore>(); var request = (await approvals.FindApprovalAsync(tenant,pending.ApprovalRequestId!.Value))!;
            if (boundary == "changed")
                Assert.Equal(HttpStatusCode.OK,(await owner.PatchAsJsonAsync(api+$"/agents/{instance}/settings/instructions",new { expectedRevision=await Revision(owner,tenant), expectedInstanceRevision=3, set=new { agentInstructions="Current authorized Admin edit" },clear=Array.Empty<string>() })).StatusCode);
            else if (boundary == "freeze")
                Assert.Equal(HttpStatusCode.OK,(await owner.PostAsJsonAsync(api+$"/agents/{instance}/self-management/freeze",new { expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=3 })).StatusCode);
            else
            {
                var state = (await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var role = state.Roles.Single(r => r.Name == "Exact management reader");
                await host.Services.GetRequiredService<TenantAccessService>().SaveRoleAsync(Assert.Single(state.Users).PrincipalId,tenant,role.RoleId,role.Name,
                    boundary == "read_revoked" ? ["agents.update"] : ["agents.read"],state.Tenant.Revision);
            }
            var detail = await Json(await owner.GetAsync(api+$"/approvals/{request.RequestId}"));
            Assert.False(detail.GetProperty("review").GetProperty("canDecide").GetBoolean()); Assert.True(detail.GetProperty("review").GetProperty("canCancel").GetBoolean());
            var decision = await owner.PostAsJsonAsync(api+$"/approvals/{request.RequestId}/decision",new { decision="approve",actionHash=request.Operation.ActionHash, expectedRevision=await Revision(owner,tenant),expectedRequestRevision=request.Revision });
            Assert.Contains(decision.StatusCode,new[] { HttpStatusCode.Forbidden,HttpStatusCode.Conflict });
            Assert.Equal(HttpStatusCode.OK,(await owner.PostAsJsonAsync(api+$"/approvals/{request.RequestId}/cancel",new { actionHash=request.Operation.ActionHash, expectedRevision=await Revision(owner,tenant),expectedRequestRevision=request.Revision })).StatusCode);
            Assert.Equal(TenantApprovalStatus.Cancelled,(await approvals.FindApprovalAsync(tenant,request.RequestId))!.Status);
            Assert.Null(await approvals.FindOperationGrantAsync(tenant,request.RequestId)); Assert.Null(await host.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant,request.Operation.OperationId));
            var saved=(await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant,instance))!.Instance;
            Assert.Equal(boundary is "changed" or "freeze" ? 4 : 3,saved.Revision); Assert.NotEqual(ManagementInstructions,saved.SettingsOverrides?.AgentInstructions?.Value);
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }
    [Theory] [InlineData(false,false,false)] [InlineData(true,false,false)] [InlineData(false,true,false)] [InlineData(true,true,false)]
    [InlineData(false,false,true)] [InlineData(true,false,true)] [InlineData(false,true,true)] [InlineData(true,true,true)]
    public async Task Management_settings_preference_wrapper_reuses_direct_or_exact_review_receipts_and_revision_guards(bool sqlite,bool review,bool reset)
    {
        var root=Path.Combine(Path.GetTempPath(),"management-preference-wrapper-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var model=new ManagementMutationModel("settings.patch") { Arguments="""{"expectedInstanceRevision":3,"section":"conversationPolicy","set":{"language":"vi"},"clear":[]}""" };
            await using WebApplicationFactory<Program> factory=sqlite?new TenantApprovalSqliteFactory(root,model):new ApprovalExecutionFactory();
            await using var host=factory.WithWebHostBuilder(b=>b.ConfigureTestServices(s=>s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner=host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress=new("https://localhost"),AllowAutoRedirect=false });
            var (tenant,instance)=await SetupManagementMutation(host,owner);var api=$"/api/v2/tenants/{tenant}";
            var beforeRevision=reset?4:3;
            if(reset)
            {
                Assert.Equal(HttpStatusCode.OK,(await owner.PatchAsJsonAsync(api+$"/agents/{instance}/settings/conversationPolicy",new { expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=3,set=new { language="fr" },clear=Array.Empty<string>() })).StatusCode);
                model.Arguments="""{"expectedInstanceRevision":4,"section":"conversationPolicy","set":{},"clear":["language"]}""";
            }
            if(review) Assert.Equal(HttpStatusCode.OK,(await owner.PutAsJsonAsync(api+"/operation-policies/agents.update",new { mode="always",independentReview=false,expectedRevision=await Revision(owner,tenant),expectedPolicyRevision=0 })).StatusCode);
            var created=await Json(await owner.PostAsJsonAsync(api+"/sessions",new { retentionKind="saved", agentInstanceId=instance,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=beforeRevision }));var session=created.GetProperty("sessionId").GetGuid();
            var response=await owner.PostAsJsonAsync(api+$"/sessions/{session}/messages",new { messageId=Guid.NewGuid(),text="Use Vietnamese responses through the exact settings interface.",expectedRevision=await Revision(owner,tenant),expectedSessionRevision=1 });
            Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);var runId=(await Json(response)).GetProperty("runId").GetGuid();if(factory is ApprovalExecutionFactory memory)Volatile.Write(ref memory.Enabled,true);
            var runs=host.Services.GetRequiredService<ITenantRunStore>();var run=await AwaitManagementRun(runs,tenant,runId,review);
            if(review)
            {
                Assert.True(run.Status==AgentRunStatus.WaitingForApproval,string.Join("\n",model.Requests.SelectMany(r=>r.Messages).Where(m=>m.Name is "settings.patch" or "iam.request_authorization").Select(m=>m.Name+": "+m.Text)));var request=(await host.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant,run.ApprovalRequestId!.Value))!;
                Assert.Equal(TenantOperationKind.ConversationPreferences,request.Operation.Kind);
                Assert.Equal(HttpStatusCode.OK,(await owner.PostAsJsonAsync(api+$"/approvals/{request.RequestId}/decision",new { decision="approve",actionHash=request.Operation.ActionHash,expectedRevision=await Revision(owner,tenant),expectedRequestRevision=request.Revision })).StatusCode);
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while(true) { var final=(await runs.ListTenantSessionRunsAsync(tenant,session,null,0,100,deadline.Token)).Items.FirstOrDefault(r=>r.ApprovalRequestId==request.RequestId&&r.AgentRunId!=runId);if(final?.IsTerminal==true){run=final;break;}await Task.Delay(25,deadline.Token); }
            }
            Assert.True(run.Status==AgentRunStatus.Completed,$"{run.Status}: {run.Failure?.Code}: {run.Failure?.Summary}");
            var saved=(await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant,instance))!.Instance;Assert.True(saved.Revision==beforeRevision+1,string.Join("\n",model.Requests.SelectMany(r=>r.Messages).Where(m=>m.Name is "settings.patch" or "iam.request_authorization").Select(m=>m.Name+": "+m.Text)));if(reset)Assert.Null(saved.SettingsOverrides?.Language);else Assert.Equal("vi",saved.SettingsOverrides!.Language!.Value);
            Assert.Single(TenantManagementReadReferences.Read(run.Checkpoint));Assert.Equal(beforeRevision,run.Admission.Configuration!.InstanceRevision);if(reset)Assert.Equal("fr",run.Admission.Configuration.Definition.ConversationPolicy.Language);
            if(!review) { var receipt=await host.Services.GetRequiredService<ITenantPreferenceOperationStore>().FindDirectOperationReceiptAsync(tenant,runId,"execute-management");Assert.NotNull(receipt);Assert.Null(receipt.RequestId);Assert.Equal("operation_completed",receipt.ReasonCode); }
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }
}
