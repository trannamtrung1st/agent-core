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
    [Theory] [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public async Task Tenant_Automation_public_HTTP_requires_exact_review_and_resumes_its_actual_independent_Run(bool sqlite,bool scheduled)
    {
        var clock=new FakeTimeProvider(DateTimeOffset.UtcNow); var model=new PublicHttpAutomationModel(); var native=new PublicHttpAutomationPort();
        void Configure(IServiceCollection services)=>services.Replace(ServiceDescriptor.Singleton<IHttpRequestClient>(native));
        await using var factory=new RuntimeStateFactory(sqlite,clock,model,configure:Configure,nativeDefinitions:true);
        using var owner=factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress=new("https://localhost") }); await Login(owner,"owner@example.test","explicit-synthetic-test-password");
        foreach(var host in factory.Services.GetServices<IHostedService>().Where(s=>s is AgentCore.Api.AgentRunHostedService or AgentCore.Api.TriggerSchedulerHostedService)) await host.StopAsync(CancellationToken.None);
        var tenant=(await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api=$"/api/v2/tenants/{tenant}";
        var saved=await Json(await owner.PostAsJsonAsync(api+"/agents",new { templateId="general-assistant",version=32,expectedRevision=await Revision(owner,tenant) }));
        var instance=saved.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor=saved.GetProperty("principalId").GetGuid();
        var permissions=new[] { "agents.read","sessions.read","http.request","automations.read","automations.run","sessions.create","agent-runs.create" };
        var role=await Json(await owner.PostAsJsonAsync(api+"/roles",new { name="Independent public research",permissions,expectedRevision=await Revision(owner,tenant) }));
        await Json(await owner.PostAsJsonAsync(api+"/role-assignments",new { principalId=executor,roleId=role.GetProperty("roleId").GetGuid(),expectedRevision=await Revision(owner,tenant) }));
        var waiting=await ExperienceAutomation(factory,owner,clock,tenant,instance,1,scheduled,instructions:"Send the exact public HTTP request, then report its observed result.",waitForApproval:true);
        Assert.True(waiting.Status==AgentRunStatus.WaitingForApproval,$"{waiting.Status}: {waiting.Failure?.Code}; {string.Join(";",model.Requests.SelectMany(r=>r.Messages).Where(m=>m.Role==ModelRole.Tool).Select(m=>m.Text))}");Assert.Equal(0,native.Calls);
        var request=(await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant,waiting.ApprovalRequestId!.Value))!;
        Assert.Equal(AgentCore.Domain.Security.ApprovalKind.Review,request.Kind);Assert.NotNull(request.Operation.HttpRequest);
        Assert.Equal(scheduled,request.OriginalInitiatorPrincipalId is null);
        var detail=await Json(await owner.GetAsync(api+$"/approvals/{request.RequestId}"));
        Assert.Equal(HttpStatusCode.OK,(await owner.PostAsJsonAsync(api+$"/approvals/{request.RequestId}/decision",new { decision="approve",actionHash=request.Operation.ActionHash,expectedRevision=detail.GetProperty("securityRevision").GetInt64(),expectedRequestRevision=request.Revision })).StatusCode);
        var done=await ExperienceMaintenanceFinal(factory,tenant,waiting.AgentRunId);
        Assert.True(done.Status==AgentRunStatus.Completed,$"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");Assert.Equal(1,native.Calls);
        Assert.Null(done.Owner.ProfileIdOrNull);Assert.Contains(model.Requests.Last().Messages,m=>m.Role==ModelRole.Tool && m.Name==ToolCatalog.RequestAuthorization && m.Text.Contains("AUTOMATION-HTTP-RESPONSE",StringComparison.Ordinal));
        var receipt=await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant,request.Operation.OperationId);
        Assert.Equal(AgentCore.Domain.Security.TenantOperationOutcome.Completed,receipt!.Outcome);Assert.Equal(202,receipt.Http!.StatusCode);
        Assert.Equal(0,factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
    }
    private sealed class PublicHttpAutomationPort:IHttpRequestClient
    {
        public bool SupportsTenantDispatchChecks=>true;public int Calls;
        public async ValueTask<HttpToolResponse> SendAsync(HttpToolRequest request,CancellationToken ct=default)
        { Calls++;await request.AuthorizeDispatch!(ct);return new(202,request.Url.AbsoluteUri,"text/plain","AUTOMATION-HTTP-RESPONSE",false,true,null,null,null); }
    }
    private sealed class PublicHttpAutomationModel:ILanguageModel
    {
        public ModelCapabilities Capabilities=>new(true,true,true);public ConcurrentQueue<ModelRequest> Requests { get; }=new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct=default)
        {
            await Task.CompletedTask;ct.ThrowIfCancellationRequested();Requests.Enqueue(request);Assert.Contains(request.Tools!,t=>t.Name==ToolCatalog.WorkComplete);
            ModelToolCall call;
            if(!request.Messages.Any(m=>m.Role==ModelRole.Tool && m.Name==ToolCatalog.RequestAuthorization))
            {
                var auth=Assert.Single(request.Tools!,t=>t.Name==ToolCatalog.RequestAuthorization);Assert.Contains("send_http_request",auth.ParametersJson);
                call=new("automation-http-review",ToolCatalog.RequestAuthorization,"{\"operation\":\"send_http_request\",\"request\":{\"method\":\"POST\",\"url\":\"https://public.example/task\",\"body\":\"exact Automation proposal\"}}");
            }
            else call=new("automation-http-complete",ToolCatalog.WorkComplete,"{\"outcome\":\"Response\",\"summary\":\"Observed the exact HTTP response.\",\"attentionRequired\":false}");
            yield return new ModelToolCallEvent(call);yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
