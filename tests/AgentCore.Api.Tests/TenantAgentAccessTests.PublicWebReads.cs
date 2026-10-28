using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Theory]
    [InlineData(false,false,"allowed")] [InlineData(true,false,"allowed")]
    [InlineData(false,true,"allowed")] [InlineData(true,true,"allowed")]
    [InlineData(false,false,"policy")] [InlineData(true,false,"policy")]
    [InlineData(false,true,"policy")] [InlineData(true,true,"policy")]
    [InlineData(false,false,"missing")] [InlineData(true,false,"missing")]
    [InlineData(false,true,"missing")] [InlineData(true,true,"missing")]
    public async Task Tenant_Automation_public_research_uses_independent_current_reads_and_needs_no_default_review(bool sqlite,bool scheduled,string mode)
    {
        var clock=new FakeTimeProvider(DateTimeOffset.UtcNow); var model=new PublicResearchAutomationModel(); var native=new PublicResearchPort();
        void Configure(IServiceCollection services) { services.Replace(ServiceDescriptor.Singleton<IWebSearchProvider>(native)); services.Replace(ServiceDescriptor.Singleton<IPublicWebFetcher>(native)); }
        await using var factory=new RuntimeStateFactory(sqlite,clock,model,configure:Configure,nativeDefinitions:true);
        using var owner=factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress=new("https://localhost") }); await Login(owner,"owner@example.test","explicit-synthetic-test-password");
        foreach(var host in factory.Services.GetServices<IHostedService>().Where(s=>s is AgentCore.Api.AgentRunHostedService or AgentCore.Api.TriggerSchedulerHostedService)) await host.StopAsync(CancellationToken.None);
        var tenant=(await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api=$"/api/v2/tenants/{tenant}";
        var saved=await Json(await owner.PostAsJsonAsync(api+"/agents",new { templateId="general-assistant",version=36,expectedRevision=await Revision(owner,tenant) }));
        var instance=saved.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor=saved.GetProperty("principalId").GetGuid();
        var permissions=new List<string> { "agents.read","automations.read","automations.run","sessions.create","agent-runs.create" }; if(mode!="missing")permissions.Add("sessions.read");
        var role=await Json(await owner.PostAsJsonAsync(api+"/roles",new { name="Independent public research",permissions,expectedRevision=await Revision(owner,tenant) }));
        await Json(await owner.PostAsJsonAsync(api+"/role-assignments",new { principalId=executor,roleId=role.GetProperty("roleId").GetGuid(),expectedRevision=await Revision(owner,tenant) }));
        if(mode=="policy")Assert.Equal(HttpStatusCode.OK,(await owner.PutAsJsonAsync(api+"/operation-policies/sessions.read",new { mode="always",independentReview=false,expectedRevision=await Revision(owner,tenant),expectedPolicyRevision=0 })).StatusCode);
        var done=await ExperienceAutomation(factory,owner,clock,tenant,instance,1,scheduled,instructions:"Search the public source, read it, and report only the observed result.");
        Assert.True(done.Status==AgentRunStatus.Completed,$"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}; {string.Join(" | ",model.Requests.SelectMany(r=>r.Messages).Where(m=>m.Role==ModelRole.Tool).Select(m=>m.Text))}");
        Assert.Equal(scheduled?ExecutionOrigin.ScheduledOccurrence:ExecutionOrigin.ManualInvocation,done.Admission.ExecutionContext!.Origin);
        if(scheduled)Assert.Null(done.Admission.ExecutionContext.OriginalInitiatorPrincipalId); else Assert.NotNull(done.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
        Assert.Null(done.Owner.ProfileIdOrNull); Assert.Equal(mode=="allowed"?2:0,native.Calls);
        var results=model.Requests.Last().Messages.Where(m=>m.Role==ModelRole.Tool && TenantPublicWebReadContract.IsRead(m.Name ?? "")).ToArray(); Assert.NotEmpty(results);
        if(mode=="allowed") { Assert.Equal(2,results.Length); Assert.All(results,m=>Assert.Contains("\"untrusted\":true",m.Text)); Assert.Contains(results,m=>m.Text.Contains("AUTOMATION-PUBLIC-SOURCE",StringComparison.Ordinal)); }
        else { Assert.All(results,m=>Assert.Contains("error",m.Text)); Assert.DoesNotContain(model.Requests.SelectMany(r=>r.Messages),m=>m.Text.Contains("AUTOMATION-PUBLIC-SOURCE",StringComparison.Ordinal)); }
        Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant,null,null,new(0,100))).Items);
        Assert.Equal(0,factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
    }
    private sealed class PublicResearchPort : IWebSearchProvider, IPublicWebFetcher
    {
        public bool IsAvailable=>true; public int Calls;
        public ValueTask<WebSearchResult> SearchAsync(WebSearchRequest request,CancellationToken cancellationToken=default) { Interlocked.Increment(ref Calls); return ValueTask.FromResult(new WebSearchResult([new("Public source","https://public.example/source","AUTOMATION-PUBLIC-SOURCE")],false)); }
        public ValueTask<PublicWebFetchResult> FetchAsync(PublicWebFetchRequest request,CancellationToken cancellationToken=default) { Interlocked.Increment(ref Calls); return ValueTask.FromResult(new PublicWebFetchResult("https://public.example/source","text/plain","AUTOMATION-PUBLIC-SOURCE",false,null,null)); }
    }
    private sealed class PublicResearchAutomationModel : ILanguageModel
    {
        public ModelCapabilities Capabilities=>new(true,true,true); public ConcurrentQueue<ModelRequest> Requests { get; }=new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct=default)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested(); Requests.Enqueue(request); Assert.Contains(request.Tools!,t=>t.Name==ToolCatalog.WorkComplete);
            var results=request.Messages.Where(m=>m.Role==ModelRole.Tool && TenantPublicWebReadContract.IsRead(m.Name ?? "")).ToArray(); ModelToolCall call;
            if(results.Length==0) { Assert.Contains(request.Tools!,t=>t.Name==ToolCatalog.WebSearch); call=new("automation-public-search",ToolCatalog.WebSearch,"{\"query\":\"public source\",\"limit\":1}"); }
            else if(results.Length==1 && !results[0].Text.Contains("error",StringComparison.Ordinal)) { Assert.Contains(request.Tools!,t=>t.Name==ToolCatalog.WebFetch); call=new("automation-public-fetch",ToolCatalog.WebFetch,"{\"url\":\"https://public.example/source\"}"); }
            else call=new("automation-public-complete",ToolCatalog.WorkComplete,JsonSerializer.Serialize(new { outcome="Response",summary=results.Any(m=>m.Text.Contains("AUTOMATION-PUBLIC-SOURCE",StringComparison.Ordinal))?"Public research completed.":"Public research was withheld by current read authority.",attentionRequired=false }));
            yield return new ModelToolCallEvent(call); yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
