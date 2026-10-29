using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Context;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using AgentCore.Application.Decisions;
using AgentCore.Infrastructure.Decisions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class BackgroundRoutingBackend : IDecisionBackend
    {
        public int Calls; public string Id => "synthetic-background-routing"; public string Version => "v1"; public bool PreAdmissionSupported => true;
        public ValueTask<DecisionStatus> ReadinessAsync(CancellationToken ct) => ValueTask.FromResult(DecisionStatus.Ready);
        public ValueTask<DecisionResult> EvaluateAsync(DecisionRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); var chosen = request.Candidates[0].Id;
            return ValueTask.FromResult(new DecisionResult(DecisionStatus.Ready, chosen, request.Candidates.ToDictionary(c => c.Id, c => c.Id == chosen ? 1d : 0d), null, Id, Version));
        }
    }
    private class TenantBackgroundModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, true);
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public Guid DelegationTargetInstanceId { get; set; }
        public ConcurrentQueue<string> Failures { get; } = new();
        public static ExecutionOrigin Origin(ModelRequest request) => request.Context?.Sections.Any(s=>s.Id==ContextSectionId.CoreBackgroundReport)==true
            ? ExecutionOrigin.TaskCompletion : request.Context?.Sections.Any(s=>s.Id==ContextSectionId.CoreBackground)==true
                ? ExecutionOrigin.DelegatedTask : ExecutionOrigin.UserMessage;
        public virtual async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request); await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            var origin = Origin(request);
            if (origin == ExecutionOrigin.UserMessage && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == "tasks.delegate"))
            {
                if (request.Tools?.Any(t => t.Name == "tasks.delegate") != true)
                {
                    yield return new ModelSemanticResponseReady(new("Task unavailable.", new(ModelSpeechMode.None, null), []));
                    yield return new ModelCompleted(ModelStopReason.Completed); yield break;
                }
                yield return new ModelToolCallEvent(new("tenant-task-source", "tasks.delegate", JsonSerializer.Serialize(new {
                    objective = "Perform the owned background check", title = "Owned background check", reportOnCompletion = true,
                    target = new { instanceId = DelegationTargetInstanceId }, requestKey = "source-assignment-once" })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (origin == ExecutionOrigin.DelegatedTask)
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.WorkComplete);
                Assert.DoesNotContain(request.Tools!, t => t.Name is ToolCatalog.SandboxExec or ToolCatalog.BrowserNavigate or ToolCatalog.ArtifactsCreate);
                var authorization = Assert.Single(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson;
                Assert.DoesNotContain("update_persona", authorization);
                yield return new ModelToolCallEvent(new("tenant-background-finish", ToolCatalog.WorkComplete,
                    """{"outcome":"Response","summary":"Owned background result.","attentionRequired":false}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            var result=request.Messages.LastOrDefault(message=>message.Role==ModelRole.Tool && message.Name=="tasks.delegate");
            var started=false;
            if(result is not null){using var value=JsonDocument.Parse(result.Text);started=value.RootElement.TryGetProperty("task",out var assigned) && assigned.TryGetProperty("taskId",out _);}
            var text = started ? "Background started." : "Task unavailable.";
            yield return new ModelSemanticResponseReady(new(text, new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    private sealed class BackgroundFailureLogs(TenantBackgroundModel model) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(model);
        public void Dispose() { }
        private sealed class Capture(TenantBackgroundModel model) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState:notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState,Exception?,string> formatter)
            {
                if (level >= LogLevel.Error && exception is not null) model.Failures.Enqueue(exception.ToString());
                else if (level == LogLevel.Warning && model.Failures.Count < 64)
                {
                    var line = formatter(state, null);
                    if (line.StartsWith("Tenant Run projection unavailable.", StringComparison.Ordinal))
                        model.Failures.Enqueue(line[..Math.Min(line.Length, 512)]);
                }
            }
        }
    }
    private sealed class TenantBackgroundMemoryFactory(string root, TenantBackgroundModel model, bool holdCompletion = false) : AgentCoreApiFactory
    {
        protected override bool EnableTenantRunExecution => true;
        protected override IReadOnlyDictionary<string,string?> ExtraConfiguration => new Dictionary<string,string?> {
            ["Persistence:WorkspaceRoot"]=Path.Combine(root,"workspaces"), ["Browser:Enabled"]="false",["OpenSandbox:Enabled"]="false" };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        { base.ConfigureWebHost(builder); builder.ConfigureTestServices(services => {services.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model));services.AddSingleton<ILoggerProvider>(new BackgroundFailureLogs(model));
            if (holdCompletion) services.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p => new TenantRunExecution(p.GetRequiredService<ITenantRuntimeStore>(), p.GetRequiredService<ITenantRunStore>(), p.GetRequiredService<ITenantRunDispatcher>(), p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>())));
        }); }
    }
    private sealed class TenantBackgroundSqliteFactory(string root, TenantBackgroundModel model, bool holdCompletion = false) : DurableSqliteHostFactory(Path.Combine(root,"store.db"),languageModel:model)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder); builder.ConfigureTestServices(services => {
                services.AddSingleton<ILoggerProvider>(new BackgroundFailureLogs(model));
                var options=(PersistenceOptions)services.Last(d=>d.ServiceType==typeof(PersistenceOptions)).ImplementationInstance!;
                options.WorkspaceRoot=Path.Combine(root,"workspaces");options.ArtifactRoot=Path.Combine(root,"artifacts");options.AttachmentRoot=Path.Combine(root,"attachments");options.DefinitionResourceRoot=Path.Combine(root,"resources");
                services.Replace(ServiceDescriptor.Singleton<IDefinitionResourceContentStore>(p => new FileDefinitionResourceContentStore(p.GetRequiredService<PersistenceOptions>().DefinitionResourceRoot)));
                services.Replace(ServiceDescriptor.Singleton<IAgentDefinitionResourceAdminStore>(p => new SqliteAgentDefinitionResourceAdminStore(p.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<AgentCoreDbContext>>(), p.GetRequiredService<IDefinitionResourceContentStore>(), p.GetRequiredService<IIdGenerator>())));
                if(holdCompletion) services.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p=>new TenantRunExecution(p.GetRequiredService<ITenantRuntimeStore>(),p.GetRequiredService<ITenantRunStore>(),p.GetRequiredService<ITenantRunDispatcher>(),p.GetRequiredService<IIdGenerator>(),p.GetRequiredService<TimeProvider>())));
            });
        }
    }
    private static async Task<(Guid Tenant,Guid Session,Guid Source)> StartTenantBackground(WebApplicationFactory<Program> factory,HttpClient owner,bool createAuthority=true,bool submit=true,bool attachmentCapability=false,IReadOnlyList<string>? fileCapabilities=null,bool reportReads=false,bool independentTask=false,bool requestIsolated=false,bool auto=false)
    {
        await Login(owner,"owner@example.test","explicit-synthetic-test-password");
        var tenant=(await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();var api=$"/api/v2/tenants/{tenant}";
        var human=(await Json(await owner.GetAsync(api+"/access/me"))).GetProperty("principalId").GetGuid();
        JsonElement agent;
        if (attachmentCapability || fileCapabilities is not null || requestIsolated)
        {
            var draft = await Json(await owner.PostAsJsonAsync(api + "/agent-definitions", new { name = "Background exact file reader", expectedRevision = await Revision(owner, tenant) }));
            var candidate = System.Text.Json.Nodes.JsonNode.Parse(draft.GetProperty("candidate").GetRawText())!;
            if (requestIsolated)
            {
                candidate["voice"]!["enabled"] = false;
            }
            if (independentTask) candidate["triggerPolicy"] = JsonSerializer.SerializeToNode((await factory.Services.GetRequiredService<IBuiltInAgentDefinitionStore>().GetAsync("general-assistant", 35))!.TriggerPolicy, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var capabilities = fileCapabilities ?? new[] { "tasks.delegate", ToolCatalog.AttachmentsRead, ToolCatalog.WorkspaceRead, ToolCatalog.WorkspaceList };
            candidate["executionPolicy"] = JsonSerializer.SerializeToNode(new { harness = Array.Empty<string>(), knowledgeSources = Array.Empty<string>(), toolAllowlist = Array.Empty<string>(),
                capabilities = new { mode = "Selected", resolvedCapabilities = capabilities }, projection = new { alwaysCapabilities = capabilities.Where(c => c != ToolCatalog.AttachmentsRead).ToArray() } });
            var draftId = draft.GetProperty("draftId").GetGuid();
            var saved = await Json(await owner.PutAsJsonAsync(api + $"/definition-drafts/{draftId}", new { candidate, expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 }));
            Assert.True(saved.TryGetProperty("revision", out _), saved.GetRawText());
            await Json(await owner.PostAsJsonAsync(api + $"/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = saved.GetProperty("revision").GetInt64() }));
            agent = await Json(await owner.PostAsJsonAsync(api + "/agents/from-publication", new { definitionId = draft.GetProperty("definitionId").GetString(), version = 1, expectedRevision = await Revision(owner, tenant) }));
        }
        else agent=await Json(await owner.PostAsJsonAsync(api+"/agents",new{templateId="general-assistant",version=36,expectedRevision=await Revision(owner,tenant)}));
        var instance=agent.GetProperty("instance").GetProperty("instanceId").GetGuid();var executor=agent.GetProperty("principalId").GetGuid();
        var instanceRevision = 1;
        if (factory.Services.GetRequiredService<ILanguageModel>() is TenantBackgroundModel background)
        {
            background.DelegationTargetInstanceId = instance;
            var cooperation = await owner.PutAsJsonAsync(api + $"/agents/{instance}/cooperation", new {
                cooperation = new { acceptTasks = true, acceptDirectMessages = false, allowDirectMessageWake = false, maxConcurrentTasks = 2 },
                expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision });
            Assert.True(cooperation.IsSuccessStatusCode, await cooperation.Content.ReadAsStringAsync()); instanceRevision++;
        }

        if (requestIsolated)
        {
            var isolation = api + $"/agents/{instance}/state-isolation";
            var preview = await Json(await owner.PostAsync(isolation + "/presets/Stateless", null));
            Assert.Equal(System.Net.HttpStatusCode.OK, (await owner.PostAsJsonAsync(isolation + "/apply", new {
                expectedRevision = preview.GetProperty("securityRevision").GetInt64(), expected = preview.GetProperty("plan").GetProperty("expected"),
                draft = preview.GetProperty("plan").GetProperty("draft"), fingerprint = preview.GetProperty("plan").GetProperty("fingerprint").GetString() })).StatusCode);
            instanceRevision++;
        }
        if(createAuthority){var access=factory.Services.GetRequiredService<TenantAccessService>();var role=await access.SaveRoleAsync(human,tenant,null,"Explicit Task fixture execution",["sessions.create","agent-runs.create","tasks.delegate","tasks.read","tasks.cancel","agents.read","agents.chat","sessions.read","agent-messages.read","agent-messages.send"],await Revision(owner,tenant));
        await access.AssignRoleAsync(human,tenant,executor,role.RoleId,AgentCore.Domain.Security.AccessScope.ForTenant(tenant),await Revision(owner,tenant));}
        if(reportReads) { var access=factory.Services.GetRequiredService<TenantAccessService>(); var readRole=await access.SaveRoleAsync(human,tenant,null,"Independent completion source reader",["sessions.read","agent-runs.read"],await Revision(owner,tenant)); await access.AssignRoleAsync(human,tenant,executor,readRole.RoleId,AgentCore.Domain.Security.AccessScope.ForTenant(tenant),await Revision(owner,tenant)); }
        var privateSpace = await Json(await owner.GetAsync(api + $"/agents/{instance}/private-space"));
        var spaceAccess = factory.Services.GetRequiredService<TenantAccessService>();
        var spaceRole = await spaceAccess.SaveRoleAsync(human, tenant, null, "Read exact background fixture context", ["spaces.read"], await Revision(owner, tenant));
        await spaceAccess.AssignRoleAsync(human, tenant, executor, spaceRole.RoleId, AgentCore.Domain.Security.AccessScope.ForResource(tenant, "space", privateSpace.GetProperty("spaceId").GetGuid().ToString("D")), await Revision(owner, tenant));
        if (auto) Assert.Equal(System.Net.HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/models/routing", new { routing = new { enabled = true, allowConfiguredDefault = false, minimumProbability = .7 }, expectedRevision = await Revision(owner, tenant), expectedSettingsRevision = 0 })).StatusCode);
        var session=(await Json(await owner.PostAsJsonAsync(api+"/sessions",new{agentInstanceId=instance,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=instanceRevision,retentionKind="saved",modelPreference=new { mode=auto?"auto":"inherit",reasoning=new { mode="modelDefault" } }}))).GetProperty("sessionId").GetGuid();
        if(!submit)return(tenant,session,Guid.Empty);
        var result=await Json(await owner.PostAsJsonAsync(api+$"/sessions/{session}/messages",new{messageId=Guid.NewGuid(),text="Start the exact owned background check",expectedRevision=await Revision(owner,tenant),expectedSessionRevision=1}));
        return(tenant,session,result.GetProperty("runId").GetGuid());
    }
    private static async Task<(AgentRun Child,AgentRun? Report)> WaitTenantBackground(WebApplicationFactory<Program> factory,Guid tenant,Guid session,Guid source,bool report)
    {
        var runs=factory.Services.GetRequiredService<ITenantRunStore>();var sessions=factory.Services.GetRequiredService<ITenantSessionStore>();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(35));AgentRun? child=null;AgentRun? notification=null;AgentRun? parent=null;
        while(!timeout.IsCancellationRequested)
        {
            parent=await runs.FindTenantRunAsync(tenant,source);
            var page=await sessions.ListTenantSessionsAsync(tenant,null,0,100);
            var childRoot=page.Items.SingleOrDefault(s=>s.Snapshot.Origin.OriginatingAgentRunId==source);
            child=childRoot?.Snapshot.Origin.InitialBackgroundAgentRunId is {} childId?await runs.FindTenantRunAsync(tenant,childId):null;
            foreach(var id in await runs.ListTenantRunIdsAsync(tenant,session))
            {var item=await runs.FindTenantRunAsync(tenant,id);if(item?.Admission.ExecutionContext?.Origin==ExecutionOrigin.TaskCompletion)notification=item;}
            if(parent?.IsTerminal==true && parent.Status!=AgentRunStatus.Completed || child?.IsTerminal==true && child.Status!=AgentRunStatus.Completed || notification?.IsTerminal==true && notification.Status!=AgentRunStatus.Completed)break;
            if(parent?.IsTerminal==true && child?.IsTerminal==true && (!report||notification?.IsTerminal==true))break;
            await Task.Delay(25,timeout.Token);
        }
        Assert.True(parent?.Status==AgentRunStatus.Completed,$"Parent {parent?.Status}: {parent?.Failure?.Code} {parent?.Failure?.Summary}");
        Assert.NotNull(child);Assert.True(child.Status==AgentRunStatus.Completed,$"Child {child.Status}: {child.Failure?.Code} {child.Failure?.Summary}");
        if(report){Assert.NotNull(notification);Assert.True(notification.Status==AgentRunStatus.Completed,$"Report {notification.Status}: {notification.Failure?.Code} {notification.Failure?.Summary}\n{string.Join('\n',((TenantBackgroundModel)factory.Services.GetRequiredService<ILanguageModel>()).Failures)}");}
        return(child,notification);
    }
    [Theory(Timeout=90000)] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_background_model_starts_detached_owned_work_and_reports_once_to_original_conversation(bool sqlite, bool auto)
    {
        var root=Path.Combine(Path.GetTempPath(),"tenant-background-host-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{
            var model=new TenantBackgroundModel(); var routingBackend = new BackgroundRoutingBackend();
            await using WebApplicationFactory<Program> original=sqlite?new TenantBackgroundSqliteFactory(root,model):new TenantBackgroundMemoryFactory(root,model);
            await using var factory = original.WithWebHostBuilder(builder => {
                if (auto) { builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string,string?> { ["DecisionIntelligence:Enabled"]="true", ["DecisionIntelligence:Backend"]="Synthetic", ["DecisionIntelligence:ShadowMode"]="false" }));
                    builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<IDecisionBackend>(routingBackend))); }
            });
            using var owner=factory.CreateClient(new WebApplicationFactoryClientOptions{BaseAddress=new("https://localhost")});
            var started=await StartTenantBackground(factory,owner,reportReads:true,auto:auto);var(child,report)=await WaitTenantBackground(factory,started.Tenant,started.Session,started.Source,true);
            if (auto) { Assert.Equal(3, routingBackend.Calls); Assert.Equal("ranked", child.PinnedModel.Decision!.Basis); Assert.Equal("ranked", report!.PinnedModel.Decision!.Basis); }
            Assert.Equal("Owned background result.",child.Result!.Text);Assert.Equal(started.Session,report!.SessionId);
            Assert.Equal(child.AgentRunId,report.Admission.ExecutionContext!.ParentRunId);Assert.Equal(child.Admission.ExecutionContext!.RootActivationId,report.Admission.ExecutionContext.RootActivationId);
            Assert.Null(child.Owner.ProfileIdOrNull);Assert.Null(report.Owner.ProfileIdOrNull);
            var catalog = await Json(await owner.GetAsync($"/api/v2/tenants/{started.Tenant}/sessions?pageSize=1"));
            Assert.Equal(1, catalog.GetProperty("total").GetInt32());
            Assert.Equal(started.Session, Assert.Single(catalog.GetProperty("items").EnumerateArray()).GetProperty("sessionId").GetGuid());
            Assert.Equal(2, (await factory.Services.GetRequiredService<ITenantSessionStore>().ListTenantSessionsAsync(started.Tenant, null, 0, 100)).Total);
            var history=await Json(await owner.GetAsync($"/api/v2/tenants/{started.Tenant}/sessions/{started.Session}/messages?limit=100"));
            Assert.Contains("Task Succeeded: Owned background result.",history.ToString());
            Assert.Single(model.Requests,r=>TenantBackgroundModel.Origin(r)==ExecutionOrigin.DelegatedTask);
            var audit=await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(started.Tenant,200);
            Assert.Single(audit,a=>a.Action=="tasks.delegate");Assert.Single(audit,a=>a.Action=="tasks.report");
            Assert.Empty(await factory.Services.GetRequiredService<IAgentRunStore>().ListDeliveryCandidatesAsync(100,DateTimeOffset.UtcNow));
        }finally{SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Theory(Timeout=60000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_background_model_cannot_borrow_the_requesting_Owner_creation_authority(bool sqlite)
    {
        var root=Path.Combine(Path.GetTempPath(),"tenant-background-denied-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{
            var model=new TenantBackgroundModel();await using WebApplicationFactory<Program> factory=sqlite?new TenantBackgroundSqliteFactory(root,model):new TenantBackgroundMemoryFactory(root,model);
            using var owner=factory.CreateClient(new WebApplicationFactoryClientOptions{BaseAddress=new("https://localhost")});var started=await StartTenantBackground(factory,owner,false);
            var runs=factory.Services.GetRequiredService<ITenantRunStore>();AgentRun? run;
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));
            do{run=await runs.FindTenantRunAsync(started.Tenant,started.Source);if(run?.IsTerminal==true)break;await Task.Delay(25,timeout.Token);}while(!timeout.IsCancellationRequested);
            Assert.NotNull(run);Assert.Equal(AgentRunStatus.Completed,run.Status);
            Assert.Single((await factory.Services.GetRequiredService<ITenantSessionStore>().ListTenantSessionsAsync(started.Tenant,null,0,100)).Items);
            Assert.DoesNotContain(model.Requests,r=>TenantBackgroundModel.Origin(r)==ExecutionOrigin.DelegatedTask);
            Assert.DoesNotContain(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(started.Tenant,200),a=>a.Action=="tasks.delegate");
            Assert.Contains("Task unavailable.",(await Json(await owner.GetAsync($"/api/v2/tenants/{started.Tenant}/sessions/{started.Session}/messages?limit=100"))).ToString());
        }finally{SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact(Timeout=90000)]
    public async Task Tenant_background_completed_child_survives_SQLite_application_host_recreation_without_replaying_work()
    {
        var root=Path.Combine(Path.GetTempPath(),"tenant-background-restart-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);Guid tenant,session,source,childId;
        try{
            var firstModel=new TenantBackgroundModel();
            await using(var first=new TenantBackgroundSqliteFactory(root,firstModel,holdCompletion:true))
            {
                using var owner=first.CreateClient(new WebApplicationFactoryClientOptions{BaseAddress=new("https://localhost")});var started=await StartTenantBackground(first,owner,reportReads:true);(tenant,session,source)=started;
                var result=await WaitTenantBackground(first,tenant,session,source,false);childId=result.Child.AgentRunId;
                Assert.Single(await first.Services.GetRequiredService<ITenantTaskStore>().ListTaskReconciliationAsync(100));
                Assert.DoesNotContain(firstModel.Requests,r=>TenantBackgroundModel.Origin(r)==ExecutionOrigin.TaskCompletion);
            }
            var secondModel=new TenantBackgroundModel();
            await using(var second=new TenantBackgroundSqliteFactory(root,secondModel))
            {
                using var owner=second.CreateClient(new WebApplicationFactoryClientOptions{BaseAddress=new("https://localhost")});await Login(owner,"owner@example.test","explicit-synthetic-test-password");
                var result=await WaitTenantBackground(second,tenant,session,source,true);Assert.Equal(childId,result.Child.AgentRunId);
                Assert.DoesNotContain(secondModel.Requests,r=>TenantBackgroundModel.Origin(r) is ExecutionOrigin.DelegatedTask or ExecutionOrigin.UserMessage);
                Assert.Empty(secondModel.Requests); // Core renders the admitted report without another provider generation.
                Assert.Empty(await second.Services.GetRequiredService<ITenantRuntimeStore>().ListTenantBackgroundCompletionsAsync(100));
            }
        }finally{SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Theory(Timeout=120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_background_completion_crosses_actual_MessagePack_wire_and_credits_only_its_delivered_prefix(bool sqlite)
    {
        var root=Path.Combine(Path.GetTempPath(),"tenant-background-wire-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{
            var model=new TenantBackgroundModel();await using WebApplicationFactory<Program> factory=sqlite?new NativeTenantSqliteFactory(root,model):new NativeTenantMemoryFactory(root,model);
            factory.UseKestrel(0);using var ready=factory.CreateClient();var setup=await StartTenantBackground(factory,ready,submit:false,reportReads:true);
            var repo=new DirectoryInfo(AppContext.BaseDirectory);while(repo is not null&&!File.Exists(Path.Combine(repo.FullName,"AgentCore.sln")))repo=repo.Parent;Assert.NotNull(repo);
            var start=new ProcessStartInfo("node","tenant-background.mjs"){WorkingDirectory=Path.Combine(repo.FullName,"tests","realtime-js"),UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
            start.Environment["BASE_URL"]=ready.BaseAddress!.ToString().TrimEnd('/');start.Environment["TENANT_ID"]=setup.Tenant.ToString("D");start.Environment["SESSION_ID"]=setup.Session.ToString("D");
            using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try{await process.WaitForExitAsync(timeout.Token);}finally{if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}}
            Assert.True(process.ExitCode==0,await stdout+await stderr);Assert.Contains("Tenant background wire passed",await stdout);
        }finally{SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
    }

}
