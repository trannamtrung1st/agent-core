using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData("web.search","allowed")] [InlineData("web.fetch","allowed")]
    [InlineData("web.search","revoked")] [InlineData("web.fetch","revoked")]
    [InlineData("web.search","policy")] [InlineData("web.fetch","policy")]
    public async Task Tenant_public_web_retry_reopens_exact_completed_observations_without_refetch_and_rechecks_authority(string tool,string mode)
    {
        var root=Path.Combine(Path.GetTempPath(),"tenant-public-web-reopen-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var clock=new FakeTimeProvider(DateTimeOffset.UtcNow); var native=new TenantPublicWebProvider();
            void Configure(IServiceCollection services) { services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)); services.Replace(ServiceDescriptor.Singleton<IWebSearchProvider>(native)); services.Replace(ServiceDescriptor.Singleton<IPublicWebFetcher>(native)); }
            Guid tenant,session,runId,assignment; DateTimeOffset retryAt; string checkpoint;
            await using(var first=new NativeTenantSqliteFactory(root,new RetriedPublicWebModel(tool,true),Configure))
            {
                using var owner=first.CreateClient(); await Login(owner,"owner@example.test","explicit-synthetic-test-password");
                tenant=(await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api=$"/api/v2/tenants/{tenant}";
                var human=(await Json(await owner.GetAsync(api+"/access/me"))).GetProperty("principalId").GetGuid();
                var agent=await Json(await owner.PostAsJsonAsync(api+"/agents",new { templateId="general-assistant",version=32,expectedRevision=await Revision(owner,tenant) })); var instance=agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor=agent.GetProperty("principalId").GetGuid();
                session=(await Json(await owner.PostAsJsonAsync(api+"/sessions",new { agentInstanceId=instance,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=1 }))).GetProperty("sessionId").GetGuid();
                var access=first.Services.GetRequiredService<TenantAccessService>(); var role=await access.SaveRoleAsync(human,tenant,null,"Retained public observations",["agents.read","sessions.read"],await Revision(owner,tenant));
                assignment=(await access.AssignRoleAsync(human,tenant,executor,role.RoleId,AccessScope.ForTenant(tenant),await Revision(owner,tenant))).AssignmentId;
                runId=(await Json(await owner.PostAsJsonAsync(api+$"/sessions/{session}/messages",new { messageId=Guid.NewGuid(),text="Research and retain the public observation",expectedRevision=await Revision(owner,tenant),expectedSessionRevision=1 }))).GetProperty("runId").GetGuid();
                await first.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
                var runs=first.Services.GetRequiredService<ITenantRunStore>(); AgentRun? waiting=null;
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
                while(!timeout.IsCancellationRequested)
                {
                    waiting=await runs.FindTenantRunAsync(tenant,runId);
                    if(waiting!.Status==AgentRunStatus.WaitingToRetry || waiting.IsTerminal)break;
                    clock.Advance(TimeSpan.FromMilliseconds(250));
                    await Task.Delay(25,timeout.Token);
                }
                Assert.Equal(AgentRunStatus.WaitingToRetry,waiting!.Status); Assert.Equal(1,native.Calls);
                Assert.True(AgentRunToolCallCheckpoint.TryRead(waiting.Checkpoint,out var messages)); Assert.Contains(messages!,m=>m.Role==ModelRole.Tool && m.Name==tool && m.Text.Contains("\"tenantPublicWebRead\":true",StringComparison.Ordinal));
                checkpoint=waiting.Checkpoint!.PayloadJson; retryAt=waiting.NextRetryAtUtc!.Value;
            }
            var model=new RetriedPublicWebModel(tool,false);
            await using var second=new NativeTenantSqliteFactory(root,model,Configure); using var again=second.CreateClient(); await Login(again,"owner@example.test","explicit-synthetic-test-password"); var nextApi=$"/api/v2/tenants/{tenant}";
            Assert.Equal(checkpoint,(await second.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant,runId))!.Checkpoint!.PayloadJson);
            if(mode=="revoked")
            {
                var human=(await Json(await again.GetAsync(nextApi+"/access/me"))).GetProperty("principalId").GetGuid();
                await second.Services.GetRequiredService<TenantAccessService>().RevokeAssignmentAsync(human,tenant,assignment,await Revision(again,tenant));
            }
            if(mode=="policy")await Json(await again.PutAsJsonAsync(nextApi+"/operation-policies/sessions.read",new { mode="always",independentReview=false,expectedRevision=await Revision(again,tenant),expectedPolicyRevision=0 }));
            if(retryAt>clock.GetUtcNow())clock.Advance(retryAt-clock.GetUtcNow());
            await second.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
            var done=await AwaitBrowserRestartRun(second.Services.GetRequiredService<ITenantRunStore>(),tenant,runId,false);
            Assert.Equal(mode=="allowed"?AgentRunStatus.Completed:AgentRunStatus.Failed,done.Status); Assert.Equal(1,native.Calls);
            if(mode=="allowed") { Assert.NotEmpty(model.Requests); Assert.Contains(model.Requests.First().Messages,m=>m.Name==tool && m.Text.Contains(TenantPublicWebProvider.Witness,StringComparison.Ordinal)); }
            else Assert.Empty(model.Requests);
            Assert.Equal(0,second.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    }
    private sealed class RetriedPublicWebModel(string tool,bool fail) : ILanguageModel
    {
        public ModelCapabilities Capabilities=>new(true,true,true); public ConcurrentQueue<ModelRequest> Requests { get; }=new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct=default)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            if(!request.Messages.Any(m=>m.Role==ModelRole.Tool && m.Name==tool))
            {
                yield return new ModelToolCallEvent(new("retained-public",tool,tool==ToolCatalog.WebSearch?"{\"query\":\"public source\"}":"{\"url\":\"https://public.example/source\"}")); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if(fail) { yield return new ModelFailed(new(ProviderErrorCode.Unavailable,"Controlled transient model failure")); yield break; }
            yield return new ModelSemanticResponseReady(new(TenantPublicWebProvider.Witness,new(ModelSpeechMode.None,null),[])); yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
}
