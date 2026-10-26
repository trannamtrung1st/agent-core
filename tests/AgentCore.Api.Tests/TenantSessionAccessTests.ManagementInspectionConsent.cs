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
    [InlineData(false,"harness.inspect","skills.read","approve")] [InlineData(true,"harness.inspect","skills.read","approve")]
    [InlineData(false,"harness.inspect","resources.read","reject")] [InlineData(true,"harness.inspect","resources.read","reject")]
    [InlineData(false,"configuration.inspect","credentials.read","approve")] [InlineData(true,"configuration.inspect","credentials.read","approve")]
    [InlineData(false,"configuration.inspect","credentials.read","reject")] [InlineData(true,"configuration.inspect","credentials.read","reject")]
    [InlineData(false,"configuration.inspect","skills.read","approve")] [InlineData(true,"configuration.inspect","skills.read","approve")]
    [InlineData(false,"configuration.inspect","agents.read","reject")] [InlineData(true,"configuration.inspect","agents.read","reject")]
    public async Task Canonical_management_inspection_compound_read_policy_withholds_values_until_exact_saved_Review(bool sqlite,string tool,string permission,string decision)
    {
        var root=Path.Combine(Path.GetTempPath(),"management-inspection-consent-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var model=new ManagementReadModel(tool);
            await using WebApplicationFactory<Program> factory=sqlite?new TenantApprovalSqliteFactory(root,model):new ApprovalExecutionFactory();
            await using var host=factory.WithWebHostBuilder(b=>b.ConfigureTestServices(s=>s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner=host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress=new("https://localhost"),AllowAutoRedirect=false });
            var (tenant,instance)=await SetupManagementInspection(host,owner);var api=$"/api/v2/tenants/{tenant}";
            Assert.Equal(HttpStatusCode.OK,(await owner.PutAsJsonAsync(api+"/operation-policies/"+permission,new { mode="always",independentReview=false,expectedRevision=await Revision(owner,tenant),expectedPolicyRevision=0 })).StatusCode);
            var session=(await Json(await owner.PostAsJsonAsync(api+"/sessions",new { retentionKind="saved", agentInstanceId=instance,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=6 }))).GetProperty("sessionId").GetGuid();
            var submitted=await owner.PostAsJsonAsync(api+$"/sessions/{session}/messages",new { messageId=Guid.NewGuid(),text="Inspect only after exact compound read consent.",expectedRevision=await Revision(owner,tenant),expectedSessionRevision=1 });
            Assert.Equal(HttpStatusCode.Accepted,submitted.StatusCode);var runId=(await Json(submitted)).GetProperty("runId").GetGuid();if(factory is ApprovalExecutionFactory memory)Volatile.Write(ref memory.Enabled,true);
            var runs=host.Services.GetRequiredService<ITenantRunStore>();var pending=await AwaitManagementRun(runs,tenant,runId,true);
            Assert.True(pending.Status==AgentRunStatus.WaitingForApproval,$"{pending.Status}: {pending.Failure?.Code}: "+string.Join("\n",model.Requests.SelectMany(r=>r.Messages).Where(m=>m.Name==tool).Select(m=>m.Text)));
            var privateProcedures=permission is not ("skills.read" or "agents.read");
            Assert.Empty(AssertManagementPreludeAndGetInspections(pending,privateProcedures));Assert.DoesNotContain(model.Requests.SelectMany(r=>r.Messages),m=>m.Text.Contains("\"tenantManagementRead\":true",StringComparison.Ordinal));
            var approvals=host.Services.GetRequiredService<ITenantApprovalStore>();var request=(await approvals.FindApprovalAsync(tenant,pending.ApprovalRequestId!.Value))!;
            Assert.Equal(ApprovalKind.Review,request.Kind);Assert.Equal("agents.read",request.Operation.Permission);Assert.NotEmpty(TenantManagementInspectionSources.Read(request.Operation.ManagementRead!.SourcesJson));
            var detail=await Json(await owner.GetAsync(api+$"/approvals/{request.RequestId}"));Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
            foreach(var hidden in new[] { "PRIVATE-INSPECTION-PROCEDURE-SENTINEL","PRIVATE-INSPECTION-RESOURCE-BYTES-SENTINEL","PRIVATE-INSPECTION-SECRET-SENTINEL" })Assert.DoesNotContain(hidden,detail.GetRawText());
            var decided=await owner.PostAsJsonAsync(api+$"/approvals/{request.RequestId}/decision",new { decision,actionHash=request.Operation.ActionHash,expectedRevision=await Revision(owner,tenant),expectedRequestRevision=request.Revision });
            Assert.True(decided.StatusCode==HttpStatusCode.OK,await decided.Content.ReadAsStringAsync());
            var final=await AwaitManagementSuccessor(runs,tenant,session,request.RequestId,runId);Assert.True(final.Status==AgentRunStatus.Completed,$"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}");
            var inspections=AssertManagementPreludeAndGetInspections(final,privateProcedures);
            Assert.Equal(decision=="approve"?1:0,inspections.Count);
            if(decision=="approve")Assert.Equal(tool,Assert.Single(inspections).ToolName);
            var receipt=await host.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant,request.Operation.OperationId);
            if(decision=="approve") { Assert.Equal(TenantManagementReadRules.Completed,receipt!.ReasonCode);await host.Services.GetRequiredService<ITenantManagementStore>().RequireTenantManagementReadSourcesAsync(new(tenant,final.AgentRunId),TenantManagementReadReferences.Read(final.Checkpoint),null); }
            else Assert.Null(receipt);
            Assert.Null(await approvals.FindOperationGrantAsync(tenant,request.RequestId));Assert.Equal(6,(await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant,instance))!.Instance.Revision);
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }
}
