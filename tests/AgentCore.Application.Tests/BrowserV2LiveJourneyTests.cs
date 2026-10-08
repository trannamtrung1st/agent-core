using System.Runtime.CompilerServices;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Tests;

public sealed class BrowserV2LiveJourneyTests
{
    [BrowserV2LiveFact]
    public async Task Configured_real_model_completes_generic_spa_through_owned_runtime()
    {
        var browser = new PlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = true,
            FixturePort = 0, InteractionMode = "InteractiveDemo", NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, loggerFactory: null);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var agentDirectory = new DirectoryInfo(AppContext.BaseDirectory);
            while (agentDirectory is not null && !Directory.Exists(Path.Combine(agentDirectory.FullName, "agents"))) agentDirectory = agentDirectory.Parent;
            var definition = (await new FileAgentDefinitionStore(Path.Combine(agentDirectory!.FullName, "agents"), SyntheticProviderAliases.Default).GetAsync("general-assistant", 17))!;
            // Keep this proof bounded to Browser and contextual capability discovery.
            definition = definition with { Environment = definition.Environment! with
            {
                Capabilities = new("Selected", BrowserToolCatalog.Tools.Values.Where(t => t.Group == "core").Select(t => t.Name).ToArray()),
                Projection = new([ToolCatalog.BrowserNavigate, ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserFind, ToolCatalog.BrowserClick, ToolCatalog.BrowserType, ToolCatalog.BrowserWait])
            } };
            using var http = new HttpClient();
            var recording = new RecordingModel(new OpenAICompatibleLanguageModel(http, new LanguageModelProviderOptions
            {
                Adapter = "OpenAICompatible", BaseUrl = "https://openrouter.ai/api/v1/", ApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY"),
                // Tool-capable does not imply native JSON-schema support. Use the tool-response channel,
                // matching the configured DeepSeek default rather than inventing a higher capability.
                DefaultModel = Environment.GetEnvironmentVariable("AGENTCORE_LLM_MODEL"), Tools = true, StructuredOutput = false,
                ReasoningEffort = Environment.GetEnvironmentVariable("AGENTCORE_LLM_REASONING_EFFORT"), ReasoningObjectWire = true,
                Timeouts = new() { SetupSeconds = 20, StreamIdleSeconds = 60, TotalSeconds = 120 }
            }));
            var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid(); var owner = Guid.NewGuid();
            var snapshot = new SessionSnapshot(1, id, 1, definition, SessionMode.Text, null, SessionStatus.Created, [], "", 0, null, null, now, now,
                AgentInstanceId: owner, ModelSelection: new("real/browser-v2-proof", "primary-llm", Environment.GetEnvironmentVariable("AGENTCORE_LLM_MODEL")!, ModelSelectionSource.SystemDefault, null));
            var memory = new InMemoryMemoryStore(); await memory.SaveAsync(snapshot, 0);
            var instances = new InMemoryAgentInstanceStore();
            await instances.InsertAsync(new(owner, definition.Id, definition.Version, definition.Identity, AgentInstanceLifecycle.Active, now, now), initialSkills: definition.SkillList);
            var output = new CapturingSessionOutput();
            await using var runtime = AgentCore.Tests.Shared.SessionRuntimeFixture.Create(snapshot, new SemanticResponseLanguageModel(recording),
                new DefaultAgentBrain(new PromptContextBuilder(ToolConfigurationGates.AllowAll, browser)), memory, output,
                new SystemIdGenerator(TimeProvider.System), TimeProvider.System, NullLogger<SessionRuntime>.Instance,
                tools: new SessionToolExecutor(browser: browser, agentInstances: instances, configurationGate: ToolConfigurationGates.AllowAll),
                browserLease: browser);
            await runtime.AttachAsync();
            var url = browser.HostPolicy.NavigationOrigins.Single() + "/browser-v2.html";
            Assert.True(await runtime.SubmitUserTextAsync($"Use the browser at {url}. Find and activate Asset 159, fill the Title field with Browser v2 proof and the Notes field with Generic SPA verified. Set Enabled to checked. Verify the resulting fields and report what happened. Use only this fixture; do not use web search or other websites."));
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await output.WaitForAsync(item => item.Payload is ResponseCompletedOutput or ErrorOutput, deadline.Token);
            await runtime.WaitUntilIdleAsync();
            var errors = output.Items.Select(item => item.Payload).OfType<ErrorOutput>().ToArray();
            Assert.True(errors.Length == 0, "Runtime errors: " + string.Join(", ", errors.Select(error => $"{error.Category}/{error.Code}/{error.FailureReason}")));
            var context = browser.ContextFor(id);
            Assert.True(context is not null, "The model did not open the fixture. Requests: " + recording.Requests.Count
                + "; offered tools: " + string.Join(", ", recording.Requests.FirstOrDefault()?.Tools?.Select(tool => tool.Name) ?? [])
                + "; calls: " + string.Join(", ", recording.Calls.Select(call => call.Name)));
            var page = context.Pages.First();
            Assert.Equal("Browser v2 proof", await page.GetByRole(Microsoft.Playwright.AriaRole.Textbox, new() { Name = "Title", Exact = true }).InputValueAsync());
            Assert.Equal("Generic SPA verified", await page.GetByRole(Microsoft.Playwright.AriaRole.Textbox, new() { Name = "Notes", Exact = true }).InputValueAsync());
            Assert.True(await page.GetByRole(Microsoft.Playwright.AriaRole.Checkbox, new() { Name = "Enabled", Exact = true }).IsCheckedAsync());
            Assert.Contains("Selected Asset 159", await page.GetByRole(Microsoft.Playwright.AriaRole.Status).InnerTextAsync());
            Assert.Contains(recording.Calls, call => call.Name == ToolCatalog.BrowserFind);
            Assert.Contains(recording.Calls, call => call.Name == ToolCatalog.BrowserFillForm);
            Assert.Contains(recording.Calls, call => call.Name == ToolCatalog.CapabilitiesLoad);
            Assert.DoesNotContain(output.Items, item => item.Payload is ErrorOutput);
            Assert.NotEmpty(runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Completed).Text);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    private sealed class RecordingModel(ILanguageModel model) : ILanguageModel
    {
        public List<ModelToolCall> Calls { get; } = [];
        public List<ModelRequest> Requests { get; } = [];
        public ModelCapabilities Capabilities => model.Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            await foreach (var item in model.GenerateAsync(request, ct))
            { if (item is ModelToolCallEvent call) Calls.Add(call.Call); yield return item; }
        }
    }
}

public sealed class BrowserV2LiveFactAttribute : FactAttribute
{
    public BrowserV2LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTCORE_BROWSER_V2_LIVE") != "1") Skip = "Explicit Browser v2 live opt-in is required.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENTCORE_LLM_MODEL"))) Skip = "Configured tool-capable model and provider key are required.";
    }
}
