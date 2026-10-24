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
        var gate = new AgentCore.Infrastructure.Tools.ToolConfigurationGate(null,null,browser,true);
        await using var runtime=SessionRuntimeFixture.Create(snapshot,model,new DefaultAgentBrain(new PromptContextBuilder(gate,browser)),
            memory,output,new SystemIdGenerator(TimeProvider.System),TimeProvider.System,NullLogger<SessionRuntime>.Instance,
            tools:new SessionToolExecutor( browser:browser, configurationGate:gate),
            browserLease:browser,agentRuns:runs);
        await runtime.AttachAsync();Assert.True(await runtime.SubmitPersistedUserTextAsync(input,Guid.NewGuid()));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromMinutes(5));
        Assert.DoesNotContain(output.Items,item=>item.Payload is ErrorOutput);
        return(runtime.Snapshot,id,browser.ContextFor(id) is not null);
    }


}
