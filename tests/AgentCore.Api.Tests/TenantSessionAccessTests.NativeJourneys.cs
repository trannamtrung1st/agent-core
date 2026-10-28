using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Context;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [TenantBrowserChromiumTheory]
    [InlineData(false,"sso")] [InlineData(true,"sso")]
    [InlineData(false,"large")] [InlineData(true,"large")]
    [InlineData(false,"budget")] [InlineData(true,"budget")]
    [InlineData(false,"no-hover")] [InlineData(true,"no-hover")]
    public async Task Tenant_native_Browser_complete_SSO_discovery_and_reserved_cleanup_journeys(bool sqlite,string mode)
    {
        var root=Path.Combine(Path.GetTempPath(),"tenant-native-journey-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        await using var sso=new GenericSsoFixture();var budget=mode is "budget" or "no-hover";var model=new NativeJourneyModel(mode);
        try
        {
            void Configure(IServiceCollection services)
            {
                var options=(BrowserOptions)services.Single(d=>d.ServiceType==typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled=true;options.Headless=true;options.Channel=null;options.FixtureEnabled=budget;options.FixturePort=0;
                options.ProfileRoot=Path.Combine(root,"browser");options.PolicyMode="Restricted";options.ProfileMode="EphemeralSession";
                options.NavigationOrigins=[sso.ApplicationOrigin,sso.IdentityOrigin];options.TargetOrigins=null;options.InteractionOrigins=null;options.ResourceOrigins=[];
            }
            await using WebApplicationFactory<Program> factory=sqlite?new NativeTenantSqliteFactory(root,model,Configure):new NativeTenantMemoryFactory(root,model,Configure);
            using var owner=factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress=new("https://localhost"),AllowAutoRedirect=false });
            await Login(owner,"owner@example.test","explicit-synthetic-test-password");
            var tenant=(await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();var api=$"/api/v2/tenants/{tenant}";
            var human=(await Json(await owner.GetAsync(api+"/access/me"))).GetProperty("principalId").GetGuid();
            var saved=await Json(await owner.PostAsJsonAsync(api+"/agents",new { templateId="general-assistant",version=36,expectedRevision=await Revision(owner,tenant) }));
            var instance=saved.GetProperty("instance").GetProperty("instanceId").GetGuid();var executor=saved.GetProperty("principalId").GetGuid();var instanceRevision=1L;
            var native=Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>());
            model.Url=budget?native.Fixture.Origin+"/budget-workflow":sso.ApplicationOrigin+"/";
            var secret=budget?"synthetic-budget-password":GenericSsoFixture.Password;var origin=budget?native.Fixture.Origin!:sso.IdentityOrigin;
            var credentials=factory.Services.GetRequiredService<TenantCredentialService>();var bindings=factory.Services.GetRequiredService<TenantCredentialBindingService>();
            for(var index=0;index<(mode=="large"?131:1);index++)
            {
                var target=index==(mode=="large"?130:0);var name=target?"Exact journey account":$"Unused account {index:D3}";var alias=target?"zz-target":$"unused-{index:D3}";
                var value=await credentials.CreateAsync(human,tenant,new(name,CredentialKind.Password,CredentialPurpose.General,null,[origin],target?secret:"unused-synthetic-secret"),await Revision(owner,tenant));
                await bindings.BindAsync(human,tenant,instance,value.Credential.CredentialId,alias,await Revision(owner,tenant),instanceRevision++,1);
            }
            if(mode=="no-hover")
            {
                var settings=await Json(await owner.GetAsync(api+$"/agents/{instance}/settings"));
                var selected=settings.GetProperty("items").EnumerateArray().Single(s=>s.GetProperty("section").GetString()=="capabilities").GetProperty("effective").GetProperty("selected").EnumerateArray().Select(v=>v.GetString()!).Where(t=>t!=ToolCatalog.BrowserHover).ToArray();
                Assert.Equal(HttpStatusCode.OK,(await owner.PatchAsJsonAsync(api+$"/agents/{instance}/settings/capabilities",new { expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=instanceRevision++,set=new { selected },clear=Array.Empty<string>() })).StatusCode);
            }
            var session=(await Json(await owner.PostAsJsonAsync(api+"/sessions",new { retentionKind="saved", agentInstanceId=instance,expectedRevision=await Revision(owner,tenant),expectedInstanceRevision=instanceRevision }))).GetProperty("sessionId").GetGuid();
            var access=factory.Services.GetRequiredService<TenantAccessService>();var role=await access.SaveRoleAsync(human,tenant,null,"Native journey exact authority",["agents.read","sessions.read","browser.read","browser.use","credentials.read","credentials.use"],await Revision(owner,tenant));
            await access.AssignRoleAsync(human,tenant,executor,role.RoleId,AccessScope.ForTenant(tenant),await Revision(owner,tenant));
            var scope=new TenantBrowserOwner(tenant,instance,executor,session);
            model.VerifyBeforeClose=async()=> {
                var page=native.ContextFor(session)!.Pages.Single();
                if(budget)Assert.Equal(mode!="no-hover",await page.GetByRole(Microsoft.Playwright.AriaRole.Button,new() { Name="Sign in",Exact=true }).IsVisibleAsync());
                else Assert.Contains("Ready for inspection",await page.InnerTextAsync("body"));
            };
            var task=budget?"Sign in with the saved journey account, inspect Pump 002 Attribute, Table and Media thoroughly. Log out, verify sign-out and close browser.":$"Open {model.Url}, sign in as {GenericSsoFixture.Email} using the saved Exact journey account. Report AC-1042 and close the browser. Do not load or invoke Skills.";
            var initial=(await Json(await owner.PostAsJsonAsync(api+$"/sessions/{session}/messages",new { messageId=Guid.NewGuid(),text=task,expectedRevision=await Revision(owner,tenant),expectedSessionRevision=1 }))).GetProperty("runId").GetGuid();
            var runs=factory.Services.GetRequiredService<ITenantRunStore>();var approvals=factory.Services.GetRequiredService<ITenantApprovalStore>();var seen=new HashSet<Guid>();AgentRun final;
            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(6));
            while(true)
            {
                await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync(deadline.Token);
                await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8,deadline.Token);
                final=(await CurrentApprovalExecution(runs,tenant,initial))!;
                if(final.Status==AgentRunStatus.WaitingForApproval && seen.Add(final.ApprovalRequestId!.Value))
                {
                    var request=(await approvals.FindApprovalAsync(tenant,final.ApprovalRequestId.Value,deadline.Token))!;
                    Assert.Equal(ApprovalKind.Review,request.Kind);Assert.NotNull(request.Operation.BrowserCommand);Assert.Equal(human,request.OriginalInitiatorPrincipalId);
                    var view=await Json(await owner.GetAsync(api+$"/approvals/{request.RequestId}",deadline.Token));Assert.DoesNotContain(secret,view.GetRawText());
                    var decided=await owner.PostAsJsonAsync(api+$"/approvals/{request.RequestId}/decision",new { decision="approve",actionHash=request.Operation.ActionHash,expectedRevision=view.GetProperty("securityRevision").GetInt64(),expectedRequestRevision=request.Revision },deadline.Token);
                    Assert.True(decided.IsSuccessStatusCode,await decided.Content.ReadAsStringAsync(deadline.Token));
                }
                if(final.IsTerminal && final.Result?.OutcomeKind!=AgentRunOutcomeKind.Continued)break;
                await Task.Delay(25,deadline.Token);
            }
            string Trace()=>string.Join(" | ",model.Requests.Last().Messages.Where(m=>m.Role==ModelRole.Tool).TakeLast(4).Select(m=>m.Name+":"+m.Text[..Math.Min(600,m.Text.Length)]));
            Assert.True(final.Status==AgentRunStatus.Completed,$"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}; Model phase: {model.Phase}; Tools: {string.Join(",",model.Requests.Last().Tools?.Select(t=>t.Name) ?? [])}; {Trace()}");
            Assert.Equal(0,final.SkillLoadCount);Assert.Empty(final.ActiveSkillKeys);Assert.True(seen.Count>=4);Assert.Equal(48,final.Admission.ExecutionBudget!.Profile.MaxSteps);Assert.True(final.Checkpoint!.StepCount<=48);
            Assert.DoesNotContain(secret,JsonSerializer.Serialize(model.Requests));Assert.DoesNotContain(secret,final.Checkpoint.PayloadJson);
            Assert.DoesNotContain("ProtectedPayload",JsonSerializer.Serialize(model.Requests));
            Assert.Equal(0,(await native.ObserveTenantSessionAsync(scope)).LiveContexts);
            var diagnostic=RunBudgetDiagnosticProjection.From(final)!;Assert.True(diagnostic.ClosureConfirmed);Assert.False(diagnostic.LogoutVerified);
            Assert.Equal(budget?"partial":"completed",diagnostic.CleanupStatus);
            if(budget)
            {
                Assert.NotNull(AgentRunToolCallCheckpoint.ReadExecutionState(final.Checkpoint).CleanupStartedAtMessage);Assert.True(model.CleanupObserved);
                Assert.True(final.Checkpoint.StepCount>=37);Assert.Equal(mode!="no-hover",model.SignOutObserved);
            }
            else { Assert.Equal(1,sso.AcceptedLogins);Assert.True(sso.ProtectedReads>0);Assert.True(model.RecordObserved); }
            if(mode=="large") { Assert.Equal(131,model.Aliases.Count);Assert.True(model.DiscoveryPages>=9); }
            Assert.Equal(0,factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true); }
    }
    private sealed class NativeJourneyModel(string mode):ILanguageModel
    {
        public string Url="";public Func<Task>? VerifyBeforeClose;public bool RecordObserved,SignOutObserved,CleanupObserved;public int DiscoveryPages;
        public HashSet<string> Aliases { get; }=new(StringComparer.Ordinal);public ConcurrentQueue<ModelRequest> Requests { get; }=new();public ModelCapabilities Capabilities=>new(true,true,Tools:true,StructuredOutput:true);
        private int phase,callIndex;private string? cursor,alias;public int Phase=>phase;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,[System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct=default)
        {
            Requests.Enqueue(request);await Task.CompletedTask;ct.ThrowIfCancellationRequested();var budget=mode is "budget" or "no-hover";
            var last=request.Messages.LastOrDefault(m=>m.Role==ModelRole.Tool);
            if(phase==3 && last?.Name==ToolCatalog.CredentialsList)
            {
                using var doc=JsonDocument.Parse(last.Text);var root=doc.RootElement;Assert.True(root.GetProperty("tenantCredentialRead").GetBoolean());DiscoveryPages++;
                foreach(var item in root.GetProperty("items").EnumerateArray()) { var reference=item.GetProperty("reference").GetString()!;Aliases.Add(reference);if(item.GetProperty("displayName").GetString()=="Exact journey account")alias=reference; }
                if(alias is null) { Assert.True(root.GetProperty("hasMore").GetBoolean());cursor=root.GetProperty("nextCursor").GetString(); } else phase=4;
            }
            object Target(string name,string role="button")=>new { by="role",value=role,name,exact=true };
            (string Tool,object Arguments) action;
            if(phase==3)action=(ToolCatalog.CredentialsList,new { cursor,limit=16 });
            else
            {
                action=phase switch {
                    0=>(ToolCatalog.BrowserNavigate,new { url=Url }),
                    1=>(ToolCatalog.BrowserType,new { target=new { by="label",value="Email" },text=GenericSsoFixture.Email }),
                    2=>(ToolCatalog.CapabilitiesLoad,new { query=ToolCatalog.BrowserFillCredential,limit=1 }),
                    4=>(ToolCatalog.BrowserFillCredential,new { target=new { by="label",value="Password" },credentialRef=alias }),
                    5=>(ToolCatalog.BrowserClick,new { target=Target("Sign in") }),
                    6=>(ToolCatalog.BrowserSnapshot,new { }),
                    7 when !budget=>(ToolCatalog.BrowserClose,new { }),
                    7=>(ToolCatalog.BrowserClick,new { target=Target("Data Management") }),
                    8 when budget=>(ToolCatalog.BrowserClick,new { target=Target("AI Engineering") }),
                    9 when budget=>(ToolCatalog.BrowserClick,new { target=Target("Pump 002") }),
                    11 or 13 or 15 when budget=>(ToolCatalog.BrowserClick,new { target=Target(new[] { "Attribute","Table","Media" }[(phase-11)/2],"tab") }),
                    >=10 and <16 when budget=>(ToolCatalog.BrowserSnapshot,new { }),
                    >=16 and <36 when budget=>(ToolCatalog.BrowserSnapshot,new { target=new { by="role",value="row",hasText=$"Measurement {phase-16} " } }),
                    36 when budget && mode!="no-hover"=>(ToolCatalog.BrowserHover,new { target=Target("Account") }),
                    37 when budget && mode!="no-hover"=>(ToolCatalog.BrowserClick,new { target=Target("Log out") }),
                    38 when budget && mode!="no-hover"=>(ToolCatalog.BrowserWait,new { condition="target",state="visible",target=Target("Sign in") }),
                    39 when budget && mode!="no-hover"=>(ToolCatalog.BrowserSnapshot,new { }),
                    40 when budget && mode!="no-hover"=>(ToolCatalog.BrowserClose,new { }),
                    36 when budget=>(ToolCatalog.BrowserClose,new { }),
                    _=>("",new { }) };
                if(phase==7 && !budget) { Assert.Contains("Ready for inspection",last!.Text);RecordObserved=true; }
                if(phase==10 && budget)Assert.Contains("Pump 002",last!.Text);
                if(phase==40 && budget) { Assert.Contains("Sign in",last!.Text);SignOutObserved=true; }
                if(phase>=36 && budget) { Assert.Contains(request.Context!.Sections,s=>s.Id==ContextSectionId.CoreCleanup);CleanupObserved=true; }
                phase++;
            }
            if(action.Tool.Length==0)
            {
                yield return new ModelSemanticResponseReady(new(budget?mode=="no-hover"?"Inspected Pump 002. Sign-out unverified because hover is unavailable. Browser closure recorded.":"Inspected Pump 002 and observed the sign-in screen after logout. Browser closure recorded.":"AC-1042 is Ready for inspection. Browser closure recorded.",new(ModelSpeechMode.Same,null),[]));
                yield return new ModelCompleted(ModelStopReason.Completed);yield break;
            }
            if(action.Tool==ToolCatalog.BrowserClose && VerifyBeforeClose is not null)await VerifyBeforeClose();
            var arguments=JsonSerializer.Serialize(action.Arguments);
            var authorized=ToolCatalog.IsBrowserTool(action.Tool) && !TenantApprovalRules.BrowserIntent(Guid.Parse("00000000-0000-0000-0000-000000000001"),action.Tool,arguments).ReadOnly;
            var name=authorized?ToolCatalog.RequestAuthorization:action.Tool;var definition=Assert.Single(request.Tools!,t=>t.Name==name);
            if(authorized) { Assert.Contains(action.Tool,definition.ParametersJson);if(CleanupObserved) { Assert.DoesNotContain("send_http_request",definition.ParametersJson);Assert.DoesNotContain("browser.navigate",definition.ParametersJson); } }
            yield return new ModelToolCallEvent(new("journey-"+callIndex++,name,authorized?JsonSerializer.Serialize(new { operation="run_browser_command",command=new { tool=action.Tool,arguments=action.Arguments } }):JsonSerializer.Serialize(action.Arguments)));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
