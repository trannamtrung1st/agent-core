using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Tests;

public sealed class BrowserLogoutRecoveryTests
{
    [Theory]
    [InlineData("accessible",true,false,false,"Log out, then close the browser.",true,true)]
    [InlineData("icon",true,false,false,"Log out, then close the browser.",true,true)]
    [InlineData("icon",false,false,false,"Log out, then close the browser.",false,true)]
    [InlineData("icon",true,true,false,"Log out, then close the browser.",false,true)]
    [InlineData("icon",true,false,true,"Log out, then close the browser.",false,true)]
    [InlineData("failure",true,false,false,"Log out, then close the browser even if logout fails.",false,true)]
    [InlineData("failure",true,false,false,"Log out; only close the browser after verified logout.",false,false)]
    [InlineData("accessible",true,false,false,"Log out but don't close the browser.",true,false)]
    [InlineData("accessible",true,false,false,"Keep me signed in and close the browser.",false,true)]
    public async Task Owned_run_recovers_and_reports_logout_and_explicit_closure_independently(string mode,bool vision,bool disabled,bool exhausted,string input,bool logoutExpected,bool closeExpected)
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled=true,Headless=true,FixturePort=0,
            ScreenshotPrivacy=disabled?"Disabled":"Protected",Limits=new(CapturesPerScope:1) },null);
        await browser.StartAsync(default);
        try
        {
            var model=new RecoveryModel(browser.HostPolicy.NavigationOrigins.Single()+"/browser-logout.html?mode="+mode,mode,vision,disabled,exhausted,input);
            var result=await RunAsync(browser,model,input);
            var entry=result.Snapshot.Entries.Last(e=>e.Role==ConversationRole.Assistant);
            Assert.Equal(EntryStatus.Completed,entry.Status);
            Assert.Equal($"Logout: {(logoutExpected?"verified":"unverified")}. Browser closure: {(closeExpected?"confirmed":"not requested or conditional") }.",entry.Text);
            Assert.Equal(closeExpected,!result.ContextOpen);
            Assert.DoesNotContain(model.Calls,c=>c.Name==ToolCatalog.BrowserDialog);
            Assert.Equal(closeExpected?1:0,model.Calls.Count(c=>c.Name==ToolCatalog.BrowserClose));
            var visual=vision&&!disabled&&!exhausted&&mode!="accessible";
            Assert.Equal(visual||exhausted?1:0,model.Calls.Count(c=>c.Name==ToolCatalog.BrowserScreenshot));
            Assert.Equal(visual?1:0,model.Calls.Count(c=>c.Name==ToolCatalog.BrowserVisionMouse));
            if(mode!="accessible"&&!input.Contains("Keep me signed in"))
                Assert.Contains(model.Requests.SelectMany(r=>r.Messages),m=>m.Role==ModelRole.System&&m.Text.Contains("Semantic targeting has not located"));
            Assert.DoesNotContain(model.Requests.SelectMany(r=>r.Messages),m=>m.Text.Contains("private-fixture-password"));
        }
        finally { await browser.StopAsync(default); }
    }

    internal static async Task<(SessionSnapshot Snapshot,Guid SessionId,bool ContextOpen)> RunAsync(NativePlaywrightBrowser browser, ILanguageModel model,string input,
        SessionModelSelection? modelSelection = null)
    {
        var names=new[] { ToolCatalog.BrowserNavigate,ToolCatalog.BrowserSnapshot,ToolCatalog.BrowserFind,
            ToolCatalog.BrowserClick,ToolCatalog.BrowserScreenshot,ToolCatalog.BrowserVisionMouse,ToolCatalog.BrowserClose,"browser.verify",ToolCatalog.BrowserConfiguration };
        var definition=await CapabilityProjectionTests.Definition(names);
        definition=definition with { Environment=definition.Environment! with { Projection=new(names) } };
        var id=Guid.NewGuid();var owner=Guid.NewGuid();var now=DateTimeOffset.UtcNow;
        var snapshot=RuntimeAgentRunStore.WithPins(new(1,id,1,definition,SessionMode.Text,null,SessionStatus.Created,[],"",0,null,null,now,now,AgentInstanceId:owner,
            ModelSelection:modelSelection ?? new("synthetic-offline/scripted","primary-llm","scripted",ModelSelectionSource.SystemDefault,null)));
        var memory=new InMemoryMemoryStore();await memory.SaveAsync(snapshot,0);
        var runs=new RuntimeAgentRunStore();runs.Bind(memory);
        var instances=new InMemoryAgentInstanceStore();await instances.InsertAsync(new(owner,definition.Id,definition.Version,definition.Identity,AgentInstanceLifecycle.Active,now,now));
        var output=new CapturingSessionOutput();
        var gate = new AgentCore.Infrastructure.Tools.ToolConfigurationGate(null,null,null,browser,true);
        await using var runtime=SessionRuntimeFixture.Create(snapshot,model,new DefaultAgentBrain(new PromptContextBuilder(gate,browser)),
            memory,output,new SystemIdGenerator(TimeProvider.System),TimeProvider.System,NullLogger<SessionRuntime>.Instance,
            tools:new SessionToolExecutor(browser:browser,artifacts:new InMemoryArtifactStore(TimeProvider.System),agentInstances:instances,configurationGate:gate),
            browserLease:browser,agentRuns:runs);
        await runtime.AttachAsync();Assert.True(await runtime.SubmitPersistedUserTextAsync(input,Guid.NewGuid()));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromMinutes(5));
        Assert.DoesNotContain(output.Items,item=>item.Payload is ErrorOutput);
        return(runtime.Snapshot,id,browser.ContextFor(id) is not null);
    }

    private sealed class RecoveryModel(string url,string mode,bool vision,bool disabled,bool exhausted,string input):ILanguageModel
    {
        public ModelCapabilities Capabilities {get;}=new(true,true,Vision:vision,Tools:true,StructuredOutput:true);
        public List<ModelRequest> Requests {get;}=[];
        public List<ModelToolCall> Calls {get;}=[];
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,[EnumeratorCancellation]CancellationToken ct=default)
        {
            await Task.Yield();ct.ThrowIfCancellationRequested();Requests.Add(request);
            var results=request.Messages.Where(m=>m.Role==ModelRole.Tool).ToArray();
            var skipLogout=input.Contains("Keep me signed in");
            var visual=vision&&!disabled&&!exhausted&&mode!="accessible";
            var close=!input.Contains("don't close")&&!(input.Contains("only close")&&mode=="failure");
            var plan=new List<(string Name,object Args)> { (ToolCatalog.BrowserNavigate,new {url}) };
            if(exhausted)plan.Add((ToolCatalog.BrowserScreenshot,new{}));
            if(!skipLogout)
            {
                if(mode=="accessible")plan.Add((ToolCatalog.BrowserClick,new{target=new{by="role",value="button",name="Account menu"}}));
                else
                {
                    plan.Add((ToolCatalog.BrowserClick,new{target=new{by="role",value="button",name="Account"}}));
                    plan.Add((ToolCatalog.BrowserClick,new{target=new{by="role",value="button",name="Profile"}}));
                    if(visual)
                    {
                        plan.Add((ToolCatalog.BrowserScreenshot,new{}));
                        var shot=results.LastOrDefault(m=>m.Name==ToolCatalog.BrowserScreenshot);
                        var snapshotId=shot is null?"":JsonDocument.Parse(shot.Text).RootElement.GetProperty("snapshotId").GetString();
                        // Scripted fixture pixels test authority/transport, not autonomous understanding.
                        plan.Add((ToolCatalog.BrowserVisionMouse,new{operation="click",x=1224,y=40,snapshotId}));
                    }
                }
                if(mode=="accessible"||visual)
                {
                    plan.Add((ToolCatalog.BrowserClick,new{target=new{by="role",value="button",name="Log out"}}));
                    plan.Add(("browser.verify",new{condition="text",target=new{by="role",value="status"},text="Signed out"}));
                }
            }
            if(close)plan.Add((ToolCatalog.BrowserClose,new{}));
            var step=Requests.Count-1;
            if(step<plan.Count)
            {
                var action=plan[step];
                if(mode!="accessible" && results.LastOrDefault()?.Text.Contains("target_missing")==true
                    && Calls.Count(c=>c.Name==ToolCatalog.BrowserClick)>=2)
                    Assert.Contains(request.Messages,m=>m.Role==ModelRole.System&&m.Text.Contains("Semantic targeting has not located"));
                Assert.Contains(request.Tools!,t=>t.Name==action.Name);
                var call=new ModelToolCall("logout-"+step,action.Name,JsonSerializer.Serialize(action.Args));Calls.Add(call);
                yield return new ModelToolCallEvent(call);yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
            else
            {
                var verified=results.Any(r=>r.Name=="browser.verify"&&r.Text.Contains("\"applicationOutcomeVerified\":true"));
                var closed=results.Any(r=>r.Name==ToolCatalog.BrowserClose&&r.Text.Contains("\"status\":\"closed\""));
                yield return new ModelSemanticResponseReady(new($"Logout: {(verified?"verified":"unverified")}. Browser closure: {(closed?"confirmed":"not requested or conditional")}.",new(ModelSpeechMode.Same,null),[]));
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
        }
    }
}
