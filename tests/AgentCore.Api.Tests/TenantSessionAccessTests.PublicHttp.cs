using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false,"review")] [InlineData(true,"review")]
    [InlineData(false,"get")] [InlineData(true,"get")]
    [InlineData(false,"head")] [InlineData(true,"head")]
    [InlineData(false,"put")] [InlineData(true,"put")]
    [InlineData(false,"patch")] [InlineData(true,"patch")]
    [InlineData(false,"delete")] [InlineData(true,"delete")]
    [InlineData(false,"source-review")] [InlineData(true,"source-review")]
    [InlineData(false,"stale")] [InlineData(true,"stale")]
    [InlineData(false,"grant")] [InlineData(true,"grant")]
    [InlineData(false,"reject")] [InlineData(true,"reject")]
    [InlineData(false,"direct")] [InlineData(true,"direct")]
    [InlineData(false,"invalid")] [InlineData(true,"invalid")]
    [InlineData(false,"unknown")] [InlineData(true,"unknown")]
    [InlineData(false,"revoke-before")] [InlineData(true,"revoke-before")]
    [InlineData(false,"revoke-after")] [InlineData(true,"revoke-after")]
    [InlineData(false,"missing-read")] [InlineData(true,"missing-read")]
    public async Task Tenant_public_http_model_requires_exact_consent_and_never_repeats_dispatch(bool sqlite,string mode)
    {
        var root=Path.Combine(Path.GetTempPath(),"tenant-http-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var model=new TenantHttpModel(mode);var native=new TenantHttpPort(mode);
            var proof=!sqlite && mode=="review" && System.Environment.GetEnvironmentVariable("AGENTCORE_HTTP_UI_PROOF")=="1";
            void Configure(IServiceCollection services)
            {
                if(proof)((AgentCore.Api.Security.IdentityHttpOptions)services.Single(d=>d.ServiceType==typeof(AgentCore.Api.Security.IdentityHttpOptions)).ImplementationInstance!).AllowedOrigins=["http://127.0.0.1:5179"];
                services.Replace(ServiceDescriptor.Singleton<IHttpRequestClient>(native));
            }
            await using WebApplicationFactory<Program> factory=sqlite?new NativeTenantSqliteFactory(root,model,Configure):new NativeTenantMemoryFactory(root,model,Configure);
            if(proof)factory.UseKestrel(int.TryParse(System.Environment.GetEnvironmentVariable("AGENTCORE_HTTP_UI_PORT"),out var proofPort)?proofPort:0);
            using var owner=factory.CreateClient();await Login(owner,"owner@example.test","explicit-synthetic-test-password");
            var tenant=(await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();var api=$"/api/v2/tenants/{tenant}";
            var human=(await Json(await owner.GetAsync(api+"/access/me"))).GetProperty("principalId").GetGuid();
            var agent=await Json(await owner.PostAsJsonAsync(api+"/agents",new { templateId="general-assistant",version=32,expectedRevision=await Revision(owner,tenant) }));
            var instance=agent.GetProperty("instance").GetProperty("instanceId").GetGuid();var executor=agent.GetProperty("principalId").GetGuid();
            var session=(await Json(await owner.PostAsJsonAsync(api+"/sessions",new { retentionKind="saved", agentInstanceId=instance,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=1 }))).GetProperty("sessionId").GetGuid();
            var access=factory.Services.GetRequiredService<TenantAccessService>();
            var keys=new List<string> { "agents.read" };if(mode!="missing-read")keys.Add("sessions.read");if(mode!="grant")keys.Add("http.request");
            var role=await access.SaveRoleAsync(human,tenant,null,"Public HTTP test",keys,await Revision(owner,tenant));
            var assignment=await access.AssignRoleAsync(human,tenant,executor,role.RoleId,AccessScope.ForTenant(tenant),await Revision(owner,tenant));
            if(mode=="source-review")Assert.Equal(HttpStatusCode.OK,(await owner.PutAsJsonAsync(api+"/operation-policies/sessions.read",new { mode="always",independentReview=false,expectedRevision=await Revision(owner,tenant),expectedPolicyRevision=0 })).StatusCode);
            var sent=await Json(await owner.PostAsJsonAsync(api+$"/sessions/{session}/messages",new { messageId=Guid.NewGuid(),text="Send the exact public HTTP proposal",expectedRevision=await Revision(owner,tenant),expectedSessionRevision=1 }));
            var id=sent.GetProperty("runId").GetGuid();var runs=factory.Services.GetRequiredService<ITenantRunStore>();
            TenantApprovalRequest? approval=null;
            if(mode is not ("direct" or "invalid" or "missing-read"))
            {
                var waiting=await AwaitBrowserRestartRun(runs,tenant,id,true);Assert.Equal(0,native.Calls);
                Assert.True(waiting.ApprovalRequestId is not null,$"{waiting.Status}: {waiting.Failure?.Code}: {waiting.Failure?.Summary}; Tools: {string.Join(" | ",model.Requests.Last().Messages.Where(m=>m.Role==ModelRole.Tool).Select(m=>m.Name+":"+m.Text))}");
                approval=(await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant,waiting.ApprovalRequestId!.Value))!;
                Assert.NotNull(approval.Operation.HttpRequest);Assert.Equal(mode=="grant"?ApprovalKind.ExceptionalGrant:ApprovalKind.Review,approval.Kind);
                if(mode=="stale")
                {
                    await access.RevokeAssignmentAsync(human,tenant,assignment.AssignmentId,await Revision(owner,tenant));
                    var stale=await Json(await owner.GetAsync(api+$"/approvals/{approval.RequestId}"));Assert.False(stale.GetProperty("review").GetProperty("canDecide").GetBoolean());
                    Assert.Equal(HttpStatusCode.OK,(await owner.PostAsJsonAsync(api+$"/approvals/{approval.RequestId}/cancel",new { actionHash=approval.Operation.ActionHash,expectedRevision=stale.GetProperty("securityRevision").GetInt64(),expectedRequestRevision=approval.Revision })).StatusCode);
                }
                var detail=await Json(await owner.GetAsync(api+$"/approvals/{approval.RequestId}"));if(mode!="stale")Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean(),detail.ToString());
                Assert.Equal("send_http_request",detail.GetProperty("preparedAction").GetProperty("operation").GetString());
                Assert.Contains(detail.GetProperty("review").GetProperty("changes").EnumerateArray(),c=>c.GetProperty("field").GetString()=="httpBody" && c.GetProperty("after").GetString()==(mode is "get" or "head"?"":"<literal-request>試"));
                if(proof)
                {
                    await File.WriteAllTextAsync("/private/tmp/agent-core-http-ui.json",JsonSerializer.Serialize(new { baseUrl=owner.BaseAddress!.ToString(),tenantId=tenant,requestId=approval.RequestId,sessionId=session }));
                    using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(20));
                    while(!File.Exists("/private/tmp/agent-core-http-ui-approved"))await Task.Delay(100,deadline.Token);
                }
                else if(mode!="stale")Assert.Equal(HttpStatusCode.OK,(await owner.PostAsJsonAsync(api+$"/approvals/{approval.RequestId}/decision",new { decision=mode=="reject"?"reject":"approve",actionHash=approval.Operation.ActionHash,expectedRevision=detail.GetProperty("securityRevision").GetInt64(),expectedRequestRevision=approval.Revision })).StatusCode);
                if(mode.StartsWith("revoke",StringComparison.Ordinal))
                {
                    await native.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    try { await access.RevokeAssignmentAsync(human,tenant,assignment.AssignmentId,await Revision(owner,tenant)); }
                    finally { native.Release.TrySetResult(); }
                }
            }
            var done=await AwaitBrowserRestartRun(runs,tenant,id,false);
            Assert.True(done.Status is AgentRunStatus.Completed or AgentRunStatus.Failed,$"{done.Status}: {done.Failure?.Code}");
            Assert.Equal(mode is "direct" or "invalid" or "missing-read" or "reject" or "stale"?0:1,native.Calls);
            if(mode is "review" or "grant" or "source-review" or "get" or "head" or "put" or "patch" or "delete")
            {
                Assert.Equal(AgentRunStatus.Completed,done.Status);Assert.Equal(1,native.BytesSent);
                Assert.Contains(model.Requests.Last().Messages,m=>m.Role==ModelRole.Tool && m.Name==ToolCatalog.RequestAuthorization && m.Text.Contains("HTTP-RESPONSE-WITNESS",StringComparison.Ordinal));
                var receipt=await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant,approval!.Operation.OperationId);
                Assert.Equal(TenantOperationOutcome.Completed,receipt!.Outcome);Assert.Equal(409,receipt.Http!.StatusCode);
                Assert.DoesNotContain("HTTP-RESPONSE-WITNESS",JsonSerializer.Serialize(receipt));
                var duplicate=await factory.Services.GetRequiredService<TenantHttpEffectService>().ExecuteApprovedAsync(new(tenant,receipt.RunId),approval.RequestId,receipt.OperationId,Guid.NewGuid(),receipt.ActionHash);
                Assert.Equal(receipt,duplicate);Assert.Equal(1,native.Calls);
                Assert.Equal(0,await factory.Services.GetRequiredService<TenantHttpEffectService>().RecoverAsync());
            }
            if(mode.StartsWith("revoke",StringComparison.Ordinal))
            {
                Assert.Equal(mode=="revoke-before"?0:1,native.BytesSent);
                Assert.DoesNotContain(model.Requests.Last().Messages,m=>m.Role==ModelRole.Tool && m.Text.Contains("HTTP-RESPONSE-WITNESS",StringComparison.Ordinal));
            }
            if(mode=="unknown")
            {
                var receipt=await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant,approval!.Operation.OperationId);
                Assert.Equal(TenantOperationOutcome.Indeterminate,receipt!.Outcome);Assert.Null(receipt.Http);Assert.Equal(0,await factory.Services.GetRequiredService<TenantHttpEffectService>().RecoverAsync());
            }
            Assert.Equal(0,factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            if(proof)
            {
                await File.WriteAllTextAsync("/private/tmp/agent-core-http-ui-done",JsonSerializer.Serialize(new { status=done.Status.ToString(),nativeCalls=native.Calls }));
                using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(20));
                while(!File.Exists("/private/tmp/agent-core-http-ui-checked"))await Task.Delay(100,deadline.Token);
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true); }
    }
    private sealed class TenantHttpPort(string mode):IHttpRequestClient
    {
        public bool SupportsTenantDispatchChecks=>true;public int Calls,BytesSent;
        public TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<HttpToolResponse> SendAsync(HttpToolRequest request,CancellationToken cancellationToken=default)
        {
            Interlocked.Increment(ref Calls);Assert.NotNull(request.AuthorizeDispatch);
            Assert.Equal(mode is "get" or "head" or "put" or "patch" or "delete"?mode.ToUpperInvariant():"POST",request.Method);Assert.Equal(mode is "get" or "head",request.FollowRedirects);
            if(mode=="revoke-before") { Entered.TrySetResult();await Release.Task.WaitAsync(cancellationToken); }
            await request.AuthorizeDispatch!(cancellationToken);Interlocked.Increment(ref BytesSent);
            if(mode=="unknown")throw new IOException("Connection lost after write");
            if(mode=="revoke-after") { Entered.TrySetResult();await Release.Task.WaitAsync(cancellationToken); }
            return new(409,request.Url.AbsoluteUri,"text/plain","HTTP-RESPONSE-WITNESS",false,true,null,null,null);
        }
    }
    private sealed class TenantHttpModel(string mode):ILanguageModel
    {
        private readonly ScriptedLanguageModel inner=new();public ModelCapabilities Capabilities=>inner.Capabilities;
        public ConcurrentQueue<ModelRequest> Requests { get; }=new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken cancellationToken=default)
        {
            Requests.Enqueue(request);var tool=mode=="direct"?ToolCatalog.HttpRequest:ToolCatalog.RequestAuthorization;
            if(!request.Messages.Any(m=>m.Role==ModelRole.Tool && m.Name==tool))
            {
                var args=new { method=mode is "get" or "head" or "put" or "patch" or "delete"?mode.ToUpperInvariant():"POST",url=mode=="invalid"?"http://127.0.0.1/private":"https://public.example/submit",headers=new Dictionary<string,string> { ["Content-Type"]="text/plain" },body=mode is "get" or "head"?"":"<literal-request>試" };
                Assert.Contains(request.Tools!,t=>t.Name==tool);
                yield return new ModelToolCallEvent(new("exact-public-http",tool,mode=="direct"?JsonSerializer.Serialize(args):JsonSerializer.Serialize(new { operation="send_http_request",request=args })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);yield break;
            }
            await foreach(var item in inner.GenerateAsync(request,cancellationToken))yield return item;
        }
    }
}
