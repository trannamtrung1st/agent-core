using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, "web.search", "allowed")] [InlineData(true, "web.search", "allowed")]
    [InlineData(false, "web.fetch", "allowed")] [InlineData(true, "web.fetch", "allowed")]
    [InlineData(false, "web.search", "missing")] [InlineData(true, "web.search", "missing")]
    [InlineData(false, "web.fetch", "missing")] [InlineData(true, "web.fetch", "missing")]
    [InlineData(false, "web.search", "review")] [InlineData(true, "web.search", "review")]
    [InlineData(false, "web.fetch", "review")] [InlineData(true, "web.fetch", "review")]
    [InlineData(false, "web.search", "revoked")] [InlineData(true, "web.search", "revoked")]
    [InlineData(false, "web.fetch", "revoked")] [InlineData(true, "web.fetch", "revoked")]
    [InlineData(false, "web.search", "revoke-output")] [InlineData(true, "web.search", "revoke-output")]
    [InlineData(false, "web.fetch", "revoke-output")] [InlineData(true, "web.fetch", "revoke-output")]
    [InlineData(false, "web.search", "invalid")] [InlineData(true, "web.search", "invalid")]
    [InlineData(false, "web.fetch", "invalid")] [InlineData(true, "web.fetch", "invalid")]
    [InlineData(false, "web.search", "failure")] [InlineData(true, "web.search", "failure")]
    [InlineData(false, "web.fetch", "failure")] [InlineData(true, "web.fetch", "failure")]
    [InlineData(false, "web.search", "unavailable")] [InlineData(true, "web.search", "unavailable")]
    [InlineData(false, "web.search", "journey")] [InlineData(true, "web.search", "journey")]
    [InlineData(false, "web.search", "bounded")] [InlineData(true, "web.fetch", "bounded")]
    [InlineData(false, "web.search", "history")] [InlineData(true, "web.search", "history")]
    public async Task Tenant_public_web_reads_use_current_executor_authority_and_never_release_revoked_observations(bool sqlite, string tool, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-public-web-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var native = new TenantPublicWebProvider { Hold = mode == "revoked", Fail = mode == "failure", Available = mode != "unavailable", Large = mode == "bounded" };
            var model = new TenantPublicWebModel(tool, mode);
            void Configure(IServiceCollection services) { services.AddLogging(logging => logging.AddProvider(new PublicReadFailureLog(model))); services.Replace(ServiceDescriptor.Singleton<IWebSearchProvider>(native)); services.Replace(ServiceDescriptor.Singleton<IPublicWebFetcher>(native)); }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId="general-assistant", version=32, expectedRevision=await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId=instance, expectedRevision=await Revision(owner, tenant), expectedInstanceRevision=1 }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Public observation executor", mode == "missing" ? ["agents.read"] : ["agents.read", "sessions.read"], await Revision(owner, tenant));
            var assignment = await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (mode == "review") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/sessions.read", new { mode="always", independentReview=false, expectedRevision=await Revision(owner, tenant), expectedPolicyRevision=0 })).StatusCode);
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId=Guid.NewGuid(), text="Research the public source", expectedRevision=await Revision(owner, tenant), expectedSessionRevision=1 }));
            var id = sent.GetProperty("runId").GetGuid(); var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            if (mode is "revoked" or "revoke-output")
            {
                await (mode == "revoked" ? native.Entered.Task : model.OutputEntered.Task).WaitAsync(TimeSpan.FromSeconds(20));
                try { await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, await Revision(owner, tenant)); }
                finally { native.Release.TrySetResult(); model.OutputRelease.TrySetResult(); }
            }
            var done = await AwaitBrowserRestartRun(runs, tenant, id, false);
            if (mode == "revoke-output") Assert.Equal(AgentRunStatus.Failed, done.Status);
            else Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}; {string.Join(" | ",model.Failures)}; {string.Join(" | ", model.Requests.Last().Messages.Where(m=>m.Role==ModelRole.Tool).Select(m=>m.Text))}");
            var expectedCalls = mode is "missing" or "review" or "invalid" or "unavailable" ? 0 : mode == "journey" ? 2 : 1;
            Assert.Equal(expectedCalls, native.Calls);
            var results = model.Requests.Last().Messages.Where(m=>m.Role==ModelRole.Tool && TenantPublicWebReadContract.IsRead(m.Name ?? "")).ToArray();
            var accepted = mode is "allowed" or "journey" or "bounded" or "revoke-output" or "history";
            if (accepted)
            {
                Assert.NotEmpty(results);
                foreach (var message in results)
                {
                    using var json = JsonDocument.Parse(message.Text); var data=json.RootElement;
                    Assert.True(data.TryGetProperty("tenantPublicWebRead", out var marker), message.Text[..Math.Min(512, message.Text.Length)]);
                    Assert.True(marker.GetBoolean()); Assert.True(data.GetProperty("untrusted").GetBoolean()); Assert.Equal(64, data.GetProperty("readCommandHash").GetString()!.Length);
                    Assert.True(Encoding.UTF8.GetByteCount(message.Text) <= ToolLimits.MaxOutputBytes);
                    if (mode == "bounded") Assert.True(data.GetProperty("truncated").GetBoolean());
                }
                if (mode != "bounded") Assert.Contains(TenantPublicWebProvider.Witness, string.Join(" ", results.Select(r=>r.Text)));
            }
            else
            {
                Assert.DoesNotContain(model.Requests.SelectMany(r=>r.Messages), m=>m.Text.Contains(TenantPublicWebProvider.Witness, StringComparison.Ordinal));
                if (mode != "unavailable") Assert.All(results, r => Assert.Contains("error", r.Text));
            }
            Assert.DoesNotContain(model.Requests.SelectMany(r=>r.Messages), m=>m.Text.Contains("NATIVE-PRIVATE-EXCEPTION-WITNESS",StringComparison.Ordinal));
            var history = (await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items").EnumerateArray().ToArray();
            if (mode == "history")
            {
                Assert.Contains(TenantPublicWebProvider.Witness, done.Checkpoint!.PayloadJson);
                Assert.DoesNotContain(history, h => h.GetProperty("text").GetString()!.Contains(TenantPublicWebProvider.Witness, StringComparison.Ordinal));
                Assert.All(history, h => Assert.Contains(h.GetProperty("role").GetString(), new[] { "user", "assistant" }));
                Assert.Equal(2, model.Requests.Count);
            }
            if (!accepted || mode == "revoke-output") Assert.DoesNotContain(history, h => h.GetProperty("text").GetString()!.Contains(TenantPublicWebProvider.Witness, StringComparison.Ordinal));
            Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0,100))).Items);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    }
    private sealed class TenantPublicWebProvider : IWebSearchProvider, IPublicWebFetcher
    {
        public const string Witness="PUBLIC-WEB-SOURCE-WITNESS";
        public bool Available { get; init; }=true; public bool IsAvailable=>Available; public bool Hold { get; init; } public bool Noncooperative { get; init; } public bool Fail { get; init; } public bool Large { get; init; } public int Calls;
        public TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private async Task Begin(CancellationToken ct) { Interlocked.Increment(ref Calls); Entered.TrySetResult(); if (Hold) { if (Noncooperative) await Release.Task; else await Release.Task.WaitAsync(ct); } if (Fail) throw new IOException("NATIVE-PRIVATE-EXCEPTION-WITNESS"); Returned.TrySetResult(); }
        public async ValueTask<WebSearchResult> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken=default)
        { await Begin(cancellationToken); return new(Enumerable.Range(0,Large?20:request.Limit).Select(i=>new WebSearchItem("Public source", "https://public.example/source", Large?new string('試',12000):Witness)).ToArray(),false); }
        public async ValueTask<PublicWebFetchResult> FetchAsync(PublicWebFetchRequest request, CancellationToken cancellationToken=default)
        { await Begin(cancellationToken); return new("https://public.example/final", "text/plain", Large?new string('試',300000):Witness,false,null,null); }
    }
    private sealed class PublicReadFailureLog(TenantPublicWebModel model) : ILoggerProvider
    {
        public ILogger CreateLogger(string name)=>new Capture(model); public void Dispose() { }
        private sealed class Capture(TenantPublicWebModel model) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
            public bool IsEnabled(LogLevel level)=>level>=LogLevel.Error;
            public void Log<TState>(LogLevel level,EventId eventId,TState state,Exception? exception,Func<TState,Exception?,string> formatter) { if(exception is not null)model.Failures.Enqueue(exception.ToString()); }
        }
    }
    private sealed class TenantPublicWebModel(string tool, string mode) : ILanguageModel
    {
        public ModelCapabilities Capabilities=>new(true,true,true); public ConcurrentQueue<ModelRequest> Requests { get; }=new(); public ConcurrentQueue<string> Failures { get; }=new();
        public TaskCompletionSource OutputEntered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource OutputRelease { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct=default)
        {
            Requests.Enqueue(request); var results=request.Messages.Where(m=>m.Role==ModelRole.Tool && TenantPublicWebReadContract.IsRead(m.Name ?? "")).ToArray();
            var current=results.Length==1 && mode=="journey"?ToolCatalog.WebFetch:tool;
            if (results.Length<(mode=="journey"?2:1) && request.Tools!.Any(t=>t.Name==current))
            {
                var args=current==ToolCatalog.WebSearch ? mode=="invalid"?"{\"query\":\"public\",\"principalId\":\"forged\"}":"{\"query\":\"public source\",\"limit\":2}" : mode=="invalid"?"{\"url\":\"http://127.0.0.1/private\"}":"{\"url\":\"https://public.example/source\"}";
                yield return new ModelToolCallEvent(new("public-read-"+results.Length,current,args)); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (mode=="revoke-output") { OutputEntered.TrySetResult(); await OutputRelease.Task.WaitAsync(ct); }
            yield return new ModelTextDelta(JsonSerializer.Serialize(new { disposition="Complete", action=new { kind="chat.respond" }, displayText=mode=="history"?"Finished controlled research.":results.Any(r=>r.Text.Contains(TenantPublicWebProvider.Witness,StringComparison.Ordinal))?TenantPublicWebProvider.Witness:"No public observation was released.", speech=new { mode="none" }, memory=Array.Empty<object>() }));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
}
