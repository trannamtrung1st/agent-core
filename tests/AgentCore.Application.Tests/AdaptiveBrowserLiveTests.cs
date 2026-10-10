using System.Runtime.CompilerServices;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace AgentCore.Application.Tests;

public sealed class AdaptiveBrowserLiveTests(Xunit.Abstractions.ITestOutputHelper evidence)
{
    [AdaptiveBrowserLiveTheory]
    [InlineData("dashboard")]
    [InlineData("weak")]
    public async Task Configured_vision_model_inspects_and_verifies_the_correct_pump(string mode)
    {
        var browser = AdaptiveBrowserRuntimeTests.NewBrowser(); await browser.StartAsync(default);
        try
        {
            var modelId = Environment.GetEnvironmentVariable("AGENTCORE_LLM_MODEL")!;
            var descriptor = ModelCatalogFactory.Real().Models.Single(m => m.ModelId == modelId);
            Assert.True(descriptor.Vision); Assert.True(descriptor.Tools);
            using var http = new HttpClient();
            var model = new RecordingModel(new SemanticResponseLanguageModel(new OpenAICompatibleLanguageModel(http, new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible", BaseUrl = "https://openrouter.ai/api/v1/", ApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"),
                DefaultModel = modelId, Transport = descriptor.Transport, Vision = descriptor.Vision, Tools = descriptor.Tools,
                StructuredOutput = descriptor.StructuredOutput, ReasoningEffort = descriptor.DefaultReasoningEffort, ReasoningObjectWire = true
            }), preferResponseFunction: descriptor.PreferResponseFunction));
            var names = new[] { ToolCatalog.BrowserNavigate, ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserFind,
                ToolCatalog.BrowserClick, ToolCatalog.BrowserScreenshot, ToolCatalog.BrowserVisionMouse, "browser.verify" };
            var definition = await CapabilityProjectionTests.Definition(names);
            definition = definition with { Environment = definition.Environment! with { Projection = new(names) } };
            var id = Guid.NewGuid(); var owner = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            var snapshot = RuntimeAgentRunStore.WithPins(new(1, id, 1, definition, SessionMode.Text, null, SessionStatus.Created,
                [], "", 0, null, null, now, now, AgentInstanceId: owner,
                ModelSelection: new("real/adaptive-browser", "primary-llm", modelId, ModelSelectionSource.SystemDefault, null)));
            var memory = new InMemoryMemoryStore(); await memory.SaveAsync(snapshot, 0);
            var runs = new RuntimeAgentRunStore(); runs.Bind(memory);
            var instances = new InMemoryAgentInstanceStore();
            await instances.InsertAsync(new(owner, definition.Id, definition.Version, definition.Identity, AgentInstanceLifecycle.Active, now, now));
            var output = new CapturingSessionOutput();
            await using var runtime = SessionRuntimeFixture.Create(snapshot, model,
                new DefaultAgentBrain(new PromptContextBuilder(ToolConfigurationGates.AllowAll, browser)), memory, output,
                new SystemIdGenerator(TimeProvider.System), TimeProvider.System, NullLogger<SessionRuntime>.Instance,
                tools: new SessionToolExecutor(browser: browser, artifacts: new InMemoryArtifactStore(TimeProvider.System), agentInstances: instances, configurationGate: ToolConfigurationGates.AllowAll),
                browserLease: browser, agentRuns: runs);
            await runtime.AttachAsync();
            var url = browser.HostPolicy.NavigationOrigins.Single() + "/adaptive-browser.html?mode=" + mode;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(await runtime.SubmitUserTextAsync($"Explore the unfamiliar application at {url}. Start with semantic evidence, then inspect its rendered layout using an authorized screenshot. Open Cooling project and inspect PUMP-1042. Prefer semantic targets; if only visual controls exist, use the blue arrow with fresh screenshot evidence. Independently verify the selected record and report its pressure. Stay within this fixture; do not use other websites."));
            await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromMinutes(5));
            Assert.DoesNotContain(output.Items, item => item.Payload is ErrorOutput);
            evidence.WriteLine($"mode={mode} model={modelId} elapsed_ms={elapsed.Elapsed.TotalMilliseconds:F1} requests={model.Requests.Count} captures={model.Calls.Count(c => c.Name == ToolCatalog.BrowserScreenshot)} calls={string.Join(",", model.Calls.Select(c => c.Name))}");
            evidence.WriteLine($"assistant_status={runtime.Snapshot.Entries.LastOrDefault(e => e.Role == ConversationRole.Assistant)?.Status} answer={runtime.Snapshot.Entries.LastOrDefault(e => e.Role == ConversationRole.Assistant)?.Text}");
            var context = browser.ContextFor(id);
            Assert.NotNull(context);
            var page = context.Pages.Single();
            Assert.Equal("Selected PUMP-1042", await page.GetByRole(AriaRole.Status).InnerTextAsync());
            Assert.Contains("4.2 bar", await page.GetByRole(AriaRole.Complementary, new() { Name = "Asset detail" }).InnerTextAsync());
            Assert.Contains(model.Requests.SelectMany(r => r.Messages), m => m.Name == ToolCatalog.BrowserScreenshot && m.Parts?.OfType<ModelImageContent>().Any() == true);
            Assert.Contains(model.Calls, c => c.Name == "browser.verify");
            if (mode == "weak") Assert.Contains(model.Calls, c => c.Name == ToolCatalog.BrowserVisionMouse);
            var answer = runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Completed).Text;
            Assert.Contains("PUMP-1042", answer); Assert.Contains("4.2", answer);
        }
        finally { await browser.StopAsync(default); }
    }

    private sealed class RecordingModel(ILanguageModel inner) : ILanguageModel
    {
        public ModelCapabilities Capabilities => inner.Capabilities;
        public List<ModelRequest> Requests { get; } = [];
        public List<ModelToolCall> Calls { get; } = [];
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            await foreach (var item in inner.GenerateAsync(request, ct))
            { if (item is ModelToolCallEvent call) Calls.Add(call.Call); yield return item; }
        }
    }
}

public sealed class AdaptiveBrowserLiveTheoryAttribute : TheoryAttribute
{
    public AdaptiveBrowserLiveTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTCORE_ADAPTIVE_BROWSER_LIVE") != "1")
            Skip = "Explicit adaptive browser vision inference opt-in is required.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")))
            Skip = "Configured provider credentials are required.";
        else if (ModelCatalogFactory.Real().Models.SingleOrDefault(m => m.ModelId == Environment.GetEnvironmentVariable("AGENTCORE_LLM_MODEL")) is not { Vision: true, Tools: true })
            Skip = "The explicitly configured catalog model must support vision and tools.";
    }
}
