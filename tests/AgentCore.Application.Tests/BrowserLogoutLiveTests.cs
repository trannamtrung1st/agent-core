using System.Runtime.CompilerServices;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.SemanticResponses;

namespace AgentCore.Application.Tests;

public sealed class BrowserLogoutLiveTests(Xunit.Abstractions.ITestOutputHelper evidence)
{
    [BrowserLogoutLiveFact]
    public async Task Genuine_vision_model_independently_chooses_visual_recovery_then_verifies_logout_and_closure()
    {
        var browser=new NativePlaywrightBrowser(new(){Enabled=true,Headless=true,FixturePort=0},null);
        await browser.StartAsync(default);
        var elapsed=System.Diagnostics.Stopwatch.StartNew();
        RecordingModel? model=null;
        try
        {
            var modelId=Environment.GetEnvironmentVariable("AGENTCORE_LLM_MODEL")!;
            var descriptor=ModelCatalogFactory.Real().Models.Single(m=>m.ModelId==modelId);
            using var http=new HttpClient();
            model=new(new SemanticResponseLanguageModel(new OpenAICompatibleLanguageModel(http,new LanguageModelProviderOptions
            {
                Adapter="OpenAICompatible",BaseUrl="https://openrouter.ai/api/v1/",ApiKey=Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"),
                DefaultModel=modelId,Transport=descriptor.Transport,Vision=true,Tools=true,StructuredOutput=descriptor.StructuredOutput,
                ReasoningEffort=descriptor.DefaultReasoningEffort,ReasoningObjectWire=true
            }),preferResponseFunction:descriptor.PreferResponseFunction));
            // Deliberately no screenshot, coordinate, selector or recovery instructions in the task prompt.
            var url=browser.HostPolicy.NavigationOrigins.Single()+"/browser-logout.html?mode=icon";
            var result=await BrowserLogoutRecoveryTests.RunAsync(browser,model,$"At {url}, open the account menu, log out, and close the browser. Stay within this application.",
                new(descriptor.Key,descriptor.ProviderAlias,descriptor.ModelId,ModelSelectionSource.Host,descriptor.DefaultReasoningEffort));
            Assert.False(result.ContextOpen);
            var receipts=model.Requests.SelectMany(r=>r.Messages).Where(m=>m.Role==ModelRole.Tool).ToArray();
            Assert.Contains(receipts,m=>m.Name==ToolCatalog.BrowserScreenshot&&m.Parts?.OfType<ModelImageContent>().Any()==true);
            Assert.Contains(model.Calls, call => call.Name == "browser.verify"
                && call.ArgumentsJson.Contains("Signed out", StringComparison.OrdinalIgnoreCase)
                && receipts.Any(m => m.ToolCallId == call.Id && m.Text.Contains("\"applicationOutcomeVerified\":true")));
            Assert.Contains(receipts,m=>m.Name==ToolCatalog.BrowserClose&&m.Text.Contains("\"status\":\"closed\""));
            Assert.DoesNotContain(model.Calls,c=>c.Name==ToolCatalog.BrowserDialog);
            Assert.DoesNotContain(receipts,m=>m.Text.Contains("stale_visual_evidence"));
            Assert.InRange(model.Calls.Count(c=>c.Name==ToolCatalog.BrowserScreenshot),1,4);
            evidence.WriteLine("answer="+result.Snapshot.Entries.Last(e=>e.Role==ConversationRole.Assistant).Text);
        }
        finally
        {
            evidence.WriteLine($"model={Environment.GetEnvironmentVariable("AGENTCORE_LLM_MODEL")} elapsed_ms={elapsed.ElapsedMilliseconds} requests={model?.Requests.Count} captures={model?.Calls.Count(c=>c.Name==ToolCatalog.BrowserScreenshot)} input_tokens={model?.InputTokens} output_tokens={model?.OutputTokens} provider_cost=unavailable calls={string.Join(",",model?.Calls.Select(c=>c.Name)??[])}");
            if(model is not null)foreach(var receipt in model.Requests.LastOrDefault()?.Messages.Where(m=>m.Role==ModelRole.Tool)??[])evidence.WriteLine($"{receipt.Name}: {receipt.Text}");
            await browser.StopAsync(default);
        }
    }
    private sealed class RecordingModel(ILanguageModel inner):ILanguageModel
    {
        public ModelCapabilities Capabilities=>inner.Capabilities;
        public List<ModelRequest> Requests {get;}=[];
        public List<ModelToolCall> Calls {get;}=[];
        public int InputTokens {get;private set;} public int OutputTokens {get;private set;}
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,[EnumeratorCancellation]CancellationToken ct=default)
        {
            Requests.Add(request);
            await foreach(var item in inner.GenerateAsync(request,ct))
            { if(item is ModelToolCallEvent call)Calls.Add(call.Call);if(item is ModelCompleted done){InputTokens+=done.InputTokens??0;OutputTokens+=done.OutputTokens??0;}yield return item; }
        }
    }
}
public sealed class BrowserLogoutLiveFactAttribute:FactAttribute
{
    public BrowserLogoutLiveFactAttribute()
    {
        if(Environment.GetEnvironmentVariable("AGENTCORE_BROWSER_LOGOUT_LIVE")!="1")Skip="Explicit paid browser logout vision acceptance opt-in is required.";
        else if(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))Skip="Provider credentials are required.";
        else if(ModelCatalogFactory.Real().Models.SingleOrDefault(m=>m.ModelId==Environment.GetEnvironmentVariable("AGENTCORE_LLM_MODEL")) is not {Vision:true,Tools:true})Skip="Select an authorized catalog model supporting tools and vision.";
    }
}
