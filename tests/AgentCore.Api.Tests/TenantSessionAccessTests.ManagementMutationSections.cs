using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    private static async Task<AgentRun> AwaitManagementSuccessor(ITenantRunStore runs,Guid tenant,Guid session,Guid request,Guid original)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while(true)
        {
            var final=(await runs.ListTenantSessionRunsAsync(tenant,session,null,0,100,deadline.Token)).Items.SingleOrDefault(r=>r.ApprovalRequestId==request&&r.AgentRunId!=original);
            if(final?.IsTerminal==true)return final;await Task.Delay(25,deadline.Token);
        }
    }
    [Theory]
    [InlineData(false,"conversationPolicy","maxOutputTokens")] [InlineData(true,"conversationPolicy","maxOutputTokens")]
    [InlineData(false,"behaviorPolicy","avoidUnsupportedClaims")] [InlineData(true,"behaviorPolicy","avoidUnsupportedClaims")]
    [InlineData(false,"initiativePolicy","enabled")] [InlineData(true,"initiativePolicy","enabled")]
    [InlineData(false,"voice","speakingRate")] [InlineData(true,"voice","speakingRate")]
    [InlineData(false,"modelDefaults","catalogKey")] [InlineData(true,"modelDefaults","catalogKey")]
    [InlineData(false,"memoryPolicy","sessionMemory")] [InlineData(true,"memoryPolicy","sessionMemory")]
    [InlineData(false,"capabilities","selected")] [InlineData(true,"capabilities","selected")]
    [InlineData(false,"triggerPolicy","enabled")] [InlineData(true,"triggerPolicy","enabled")]
    [InlineData(false,"providerPreferences","languageModel")] [InlineData(true,"providerPreferences","languageModel")]
    [InlineData(false,"stateIsolationPolicy","homeAccess")] [InlineData(true,"stateIsolationPolicy","homeAccess")]
    public async Task Management_settings_sections_use_shared_constraints_and_exact_atomic_Automation_reconciliation(bool sqlite,string section,string field)
    {
        var root=Path.Combine(Path.GetTempPath(),"management-mutation-sections-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var model=new ManagementMutationModel("settings.patch");
            await using WebApplicationFactory<Program> factory=sqlite?new TenantApprovalSqliteFactory(root,model):new ApprovalExecutionFactory();
            await using var host=factory.WithWebHostBuilder(b=>b.ConfigureTestServices(s=>s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner=host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress=new("https://localhost"),AllowAutoRedirect=false });
            var (tenant,instance)=await SetupManagementMutation(host,owner);var api=$"/api/v2/tenants/{tenant}";Guid? automation=null;Automation? prior=null;
            if(section=="triggerPolicy")
            {
                var configured=await Json(await owner.PostAsJsonAsync(api+$"/agents/{instance}/automations",new { enabled=true,name="Retain future policy work",instructions="A future Synthetic task",requiresTools=false,
                    schedule=new { kind="oneShot",timeZone="UTC",atUtc=DateTimeOffset.UtcNow.AddHours(2) },expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=3 }));
                automation=configured.GetProperty("automationId").GetGuid();prior=await host.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(tenant,automation.Value);Assert.Equal(AutomationStatus.Active,prior!.Status);
            }
            var current=(await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant,instance))!.Instance;
            var state=(await host.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;
            var view=(await host.Services.GetRequiredService<TenantAgentConfigurationService>().ReadSettingsAsync(Assert.Single(state.Users).PrincipalId,tenant,instance)).Items.Single(s=>s.Section==section);
            var value=section=="stateIsolationPolicy"?JsonSerializer.SerializeToElement("ReadOnly"):section=="triggerPolicy"?JsonSerializer.SerializeToElement(false):JsonSerializer.SerializeToElement(view.Effective[field]);
            model.Arguments=JsonSerializer.Serialize(new { expectedInstanceRevision=current.Revision,section,set=new Dictionary<string,JsonElement> { [field]=value },clear=Array.Empty<string>() });
            var created=await Json(await owner.PostAsJsonAsync(api+"/sessions",new { agentInstanceId=instance,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=current.Revision }));var session=created.GetProperty("sessionId").GetGuid();
            var response=await owner.PostAsJsonAsync(api+$"/sessions/{session}/messages",new { messageId=Guid.NewGuid(),text="Inspect and save this exact setting through the shared configuration owner.",expectedRevision=await Revision(owner,tenant),expectedSessionRevision=1 });
            Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);var runId=(await Json(response)).GetProperty("runId").GetGuid();if(factory is ApprovalExecutionFactory memory)Volatile.Write(ref memory.Enabled,true);
            var runs=host.Services.GetRequiredService<ITenantRunStore>();var pending=await AwaitManagementRun(runs,tenant,runId,true);
            Assert.True(pending.Status==AgentRunStatus.WaitingForApproval,$"{pending.Status}: {pending.Failure?.Code}: "+string.Join("\n",model.Requests.SelectMany(r=>r.Messages).Where(m=>m.Name=="settings.patch").Select(m=>m.Text)));
            var approvals=host.Services.GetRequiredService<ITenantApprovalStore>();var request=(await approvals.FindApprovalAsync(tenant,pending.ApprovalRequestId!.Value))!;Assert.Equal(TenantOperationKind.ManagementMutation,request.Operation.Kind);
            if(automation is not null)Assert.Equal(AutomationStatus.Active,(await host.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(tenant,automation.Value))!.Status);
            var decided=await owner.PostAsJsonAsync(api+$"/approvals/{request.RequestId}/decision",new { decision="approve",actionHash=request.Operation.ActionHash,expectedRevision=await Revision(owner,tenant),expectedRequestRevision=request.Revision });
            Assert.True(decided.StatusCode==HttpStatusCode.OK,await decided.Content.ReadAsStringAsync());
            var final=await AwaitManagementSuccessor(runs,tenant,session,request.RequestId,runId);Assert.True(final.Status==AgentRunStatus.Completed,$"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}");
            var saved=(await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant,instance))!.Instance;Assert.Equal(current.Revision+1,saved.Revision);Assert.Equal(current.Revision,final.Admission.Configuration!.InstanceRevision);
            Assert.Single(TenantManagementReadReferences.Read(final.Checkpoint));
            var settings=await Json(await owner.GetAsync(api+$"/agents/{instance}/settings"));var item=settings.GetProperty("items").EnumerateArray().Single(s=>s.GetProperty("section").GetString()==section);
            Assert.Equal("instance",item.GetProperty("sources").GetProperty(field).GetString());Assert.Equal(value.GetRawText(),item.GetProperty("effective").GetProperty(field).GetRawText());
            if(automation is not null)
            {
                var reconciled=(await host.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(tenant,automation.Value))!;
                Assert.Equal(AutomationStatus.SuspendedPolicy,reconciled.Status);Assert.Equal(prior!.Revision+1,reconciled.Revision);Assert.Equal(prior.NextOccurrenceAtUtc,reconciled.NextOccurrenceAtUtc);Assert.Equal(prior.Instructions,reconciled.Instructions);
                Assert.DoesNotContain((await runs.ListTenantSessionRunsAsync(tenant,null,null,0,100,instanceId:instance)).Items,r=>r.Admission.ExecutionContext?.Origin==ExecutionOrigin.ScheduledOccurrence);
            }
        }
        finally { SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }
}
