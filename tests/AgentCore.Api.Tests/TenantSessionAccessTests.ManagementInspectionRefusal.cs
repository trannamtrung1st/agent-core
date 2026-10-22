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
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Canonical_management_inspection_rejected_diagnosis_cannot_escape_its_semantic_root_by_goal_wording(bool sqlite)
    {
        var root=Path.Combine(Path.GetTempPath(),"management-inspection-refusal-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var model=new ManagementReadModel("configuration.inspect") { ArgumentsOverride="""{"goal":"Use concise responses"}""" };
            await using WebApplicationFactory<Program> factory=sqlite?new TenantApprovalSqliteFactory(root,model):new ApprovalExecutionFactory();
            await using var host=factory.WithWebHostBuilder(b=>b.ConfigureTestServices(s=>s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner=host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress=new("https://localhost"),AllowAutoRedirect=false });
            var (tenant,instance)=await SetupManagementInspection(host,owner);var api=$"/api/v2/tenants/{tenant}";
            Assert.Equal(HttpStatusCode.OK,(await owner.PutAsJsonAsync(api+"/operation-policies/agents.read",new { mode="always",independentReview=false,expectedRevision=await Revision(owner,tenant),expectedPolicyRevision=0 })).StatusCode);
            var session=(await Json(await owner.PostAsJsonAsync(api+"/sessions",new { agentInstanceId=instance,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=6 }))).GetProperty("sessionId").GetGuid();
            var submitted=await owner.PostAsJsonAsync(api+$"/sessions/{session}/messages",new { messageId=Guid.NewGuid(),text="Inspect the exact concise-response remedy only if the read is authorized.",expectedRevision=await Revision(owner,tenant),expectedSessionRevision=1 });
            Assert.Equal(HttpStatusCode.Accepted,submitted.StatusCode);var runId=(await Json(submitted)).GetProperty("runId").GetGuid();if(factory is ApprovalExecutionFactory memory)Volatile.Write(ref memory.Enabled,true);
            var runs=host.Services.GetRequiredService<ITenantRunStore>();var pending=await AwaitManagementRun(runs,tenant,runId,true);Assert.Equal(AgentRunStatus.WaitingForApproval,pending.Status);
            var approvals=host.Services.GetRequiredService<ITenantApprovalStore>();var request=(await approvals.FindApprovalAsync(tenant,pending.ApprovalRequestId!.Value))!;
            Assert.Equal(HttpStatusCode.OK,(await owner.PostAsJsonAsync(api+$"/approvals/{request.RequestId}/decision",new { decision="reject",actionHash=request.Operation.ActionHash,expectedRevision=await Revision(owner,tenant),expectedRequestRevision=request.Revision })).StatusCode);
            var final=await AwaitManagementSuccessor(runs,tenant,session,request.RequestId,runId);Assert.Equal(AgentRunStatus.Completed,final.Status);
            var alternative=await host.Services.GetRequiredService<ITenantManagementStore>().ObserveTenantManagementReadAsync(new(tenant,runId),null,"configuration.inspect","""{"goal":"Keep the next responses brief"}""");
            var operation=TenantApprovalRules.PrepareManagementRead(Guid.NewGuid(),instance,alternative.Value,6,1);
            Assert.NotEqual(request.Operation.ActionHash,operation.ActionHash);Assert.Equal(request.Operation.OperationFingerprint,operation.OperationFingerprint);
            var prior=await approvals.FindRootApprovalAsync(tenant,request.RootActivationId,request.ExecutorPrincipalId,operation);
            Assert.NotNull(prior);Assert.Equal(request.RequestId,prior.RequestId);Assert.Equal(TenantApprovalStatus.Rejected,prior.Status);
            Assert.Equal(1,(await Json(await owner.GetAsync(api+"/approvals?pageSize=100"))).GetProperty("total").GetInt32());Assert.Empty(TenantManagementReadReferences.Read(final.Checkpoint));
            Assert.Null(await approvals.FindOperationGrantAsync(tenant,request.RequestId));Assert.Null(await host.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant,request.Operation.OperationId));
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }
}
