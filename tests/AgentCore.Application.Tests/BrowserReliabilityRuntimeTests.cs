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
using AriaRole = Microsoft.Playwright.AriaRole;

namespace AgentCore.Application.Tests;

public sealed class BrowserReliabilityRuntimeTests
{
    [Theory]
    [InlineData("browser-dense.html", "treeitem")]
    [InlineData("browser-custom-tree.html", "generic")]
    public async Task Synthetic_dense_tool_loop_discovers_loads_updates_verifies_and_closes_across_turns(string fixture, string targetRole)
    {
        var browser = new NativePlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = true,
            FixturePort = 0, InteractionMode = "InteractiveDemo", NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, loggerFactory: null);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var definition = await CapabilityProjectionTests.Definition(ToolCatalog.CapabilitiesLoad, ToolCatalog.BrowserNavigate,
                ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserFind, ToolCatalog.BrowserClick, ToolCatalog.BrowserFillForm, ToolCatalog.BrowserClose);
            var owner = Guid.NewGuid(); var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            var snapshot = RuntimeAgentRunStore.WithPins(new(1, id, 1, definition, SessionMode.Text, null, SessionStatus.Created,
                [], "", 0, null, null, now, now, AgentInstanceId: owner,
                ModelSelection: new("synthetic-offline/scripted", "primary-llm", "scripted", ModelSelectionSource.SystemDefault, null)));
            var memory = new InMemoryMemoryStore(); var runs = new RuntimeAgentRunStore(); runs.Bind(memory);
            await memory.SaveAsync(snapshot, 0);
            var instances = new InMemoryAgentInstanceStore();
            await instances.InsertAsync(new(owner, definition.Id, definition.Version, definition.Identity, AgentInstanceLifecycle.Active, now, now), initialSkills: definition.SkillList);
            var output = new CapturingSessionOutput();
            var model = new DenseModel(browser.HostPolicy.NavigationOrigins.Single() + "/" + fixture, targetRole);
            await using var runtime = SessionRuntimeFixture.Create(snapshot, model,
                new DefaultAgentBrain(new PromptContextBuilder(ToolConfigurationGates.AllowAll, browser)), memory, output,
                new SystemIdGenerator(TimeProvider.System), TimeProvider.System, NullLogger<SessionRuntime>.Instance,
                tools: new SessionToolExecutor(browser: browser, agentInstances: instances, configurationGate: ToolConfigurationGates.AllowAll),
                browserLease: browser, agentRuns: runs);
            await runtime.AttachAsync();
            var source = Guid.NewGuid();
            Assert.True(await runtime.SubmitPersistedUserTextAsync("Inspect the dense catalog, open its final entry and update Summary.", source));
            await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromMinutes(3));
            Assert.True(!output.Items.Any(item => item.Payload is ErrorOutput), string.Join("\n", model.Failures));
            var page = browser.ContextFor(id)!.Pages.Single();
            Assert.Equal("Opened Entry 1999", await page.GetByRole(AriaRole.Status).InnerTextAsync());
            Assert.Equal("Verified change", await page.GetByRole(AriaRole.Textbox, new() { Name = "Summary" }).InputValueAsync());
            var run = await runs.ForSourceAsync(id, source);
            Assert.Contains(ToolCatalog.BrowserFillForm, run!.LoadedCapabilityIds);
            Assert.Equal(1, run.CapabilityLoadCount);
            Assert.Contains(model.Results, r => r.Name == ToolCatalog.BrowserFind && r.Text.Contains("Entry 1999"));
            Assert.Contains(model.Results, r => r.Name == ToolCatalog.BrowserFillForm && !r.Text.Contains("error"));
            Assert.DoesNotContain(model.Requests[0].Tools!, t => t.Name == ToolCatalog.BrowserFillForm);
            Assert.Contains(model.Requests[0].Tools!, t => t.Name == ToolCatalog.BrowserClose);
            Assert.DoesNotContain("dense-private-token", string.Join('\n', model.Results.Select(r => r.Text)));
            foreach (var expected in new[] { "closed", "already_closed" })
            {
                source = Guid.NewGuid();
                Assert.True(await runtime.SubmitPersistedUserTextAsync("Close the browser.", source));
                await runtime.WaitUntilIdleAsync();
                Assert.Null(browser.ContextFor(id));
                Assert.Equal(expected, runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant).Text);
                Assert.Empty((await runs.ForSourceAsync(id, source))!.LoadedCapabilityIds);
            }
            Assert.True(!output.Items.Any(item => item.Payload is ErrorOutput), string.Join("\n", model.Failures));
            Assert.InRange(model.Requests.Count, 8, 16);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Failed_or_unauthorized_close_never_reports_success(bool authorized)
    {
        var browser = new UnavailableBrowser();
        var definition = await CapabilityProjectionTests.Definition(authorized
            ? [ToolCatalog.CapabilitiesLoad, ToolCatalog.BrowserClose] : [ToolCatalog.CapabilitiesLoad]);
        var now = DateTimeOffset.UtcNow;
        var snapshot = new SessionSnapshot(1, Guid.NewGuid(), 1, definition, SessionMode.Text, null, SessionStatus.Created,
            [], "", 0, null, null, now, now, AgentInstanceId: Guid.NewGuid());
        var memory = new InMemoryMemoryStore(); await memory.SaveAsync(snapshot, 0);
        var output = new CapturingSessionOutput(); var model = new DenseModel("unused");
        await using var runtime = SessionRuntimeFixture.Create(snapshot, model,
            new DefaultAgentBrain(new PromptContextBuilder(ToolConfigurationGates.AllowAll, browser)), memory, output,
            new SystemIdGenerator(TimeProvider.System), TimeProvider.System, NullLogger<SessionRuntime>.Instance,
            tools: new SessionToolExecutor(browser: browser, configurationGate: ToolConfigurationGates.AllowAll));
        await runtime.AttachAsync(); Assert.True(await runtime.SubmitUserTextAsync("Close the browser."));
        await runtime.WaitUntilIdleAsync();
        var answer = runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant).Text;
        Assert.Equal(authorized ? "Unable to confirm closure: provider_unavailable" : "Browser closure is not authorized.", answer);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(0, browser.CloseCalls);
        Assert.DoesNotContain(output.Items, item => item.Payload is ErrorOutput);
    }

    private sealed class UnavailableBrowser : IBrowser
    {
        public int CloseCalls { get; private set; }
        public BrowserProviderDescriptor Provider { get; } = new("unavailable", "Unavailable", new HashSet<BrowserFeature> { BrowserFeature.Close });
        public bool IsAvailable => false;
        public BrowserHostPolicy HostPolicy { get; } = new(true, true, BrowserInteractionMode.InteractiveDemo, ["http://127.0.0.1"]);
        public ValueTask<Uri?> GetCurrentUrlAsync(Guid id, CancellationToken ct = default) => new((Uri?)null);
        public ValueTask<BrowserResult> NavigateAsync(BrowserRequest request, CancellationToken ct = default) => new(new BrowserResult("provider_unavailable", null));
        public ValueTask<BrowserResult> SnapshotAsync(Guid id, CancellationToken ct = default) => new(new BrowserResult("provider_unavailable", null));
        public ValueTask<BrowserResult> InteractAsync(BrowserRequest request, CancellationToken ct = default) => new(new BrowserResult("provider_unavailable", null));
        public ValueTask<BrowserResult> CloseAsync(Guid id, CancellationToken ct = default) { CloseCalls++; return new(new BrowserResult(null, Status: "provider_unavailable", DataJson: System.Text.Json.JsonSerializer.Serialize(new { status = "provider_unavailable" }))); }

        public ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default) => request.Operation switch
        {
            BrowserOperation.Navigate => NavigateAsync(request, ct),
            BrowserOperation.Snapshot or BrowserOperation.WaitFor => SnapshotAsync(request.SessionId, ct),
            BrowserOperation.Close => CloseAsync(request.SessionId, ct),
            BrowserOperation.Click or BrowserOperation.Type or BrowserOperation.Hover or BrowserOperation.Drag or BrowserOperation.Upload or BrowserOperation.FillForm => InteractAsync(request, ct),
            _ => new(new BrowserResult("unsupported_operation")),
        };
}

    private sealed class DenseModel(string url, string targetRole = "treeitem") : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public List<ModelRequest> Requests { get; } = [];
        public List<ModelMessage> Results { get; } = [];
        private readonly Dictionary<Guid, int> _steps = [];
        public List<string> Failures { get; } = [];
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await using var iterator = GenerateCore(request, ct).GetAsyncEnumerator(ct);
            while (true)
            {
                bool moved;
                try { moved = await iterator.MoveNextAsync(); }
                catch (Exception ex) { Failures.Add(ex.ToString()); throw; }
                if (!moved) yield break;
                yield return iterator.Current;
            }
        }
        private async IAsyncEnumerable<ModelGenerationEvent> GenerateCore(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested(); Requests.Add(request);
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            Results.AddRange(results.Where(r => !Results.Any(old => old.ToolCallId == r.ToolCallId)));
            var step = _steps.GetValueOrDefault(request.ResponseId); _steps[request.ResponseId] = step + 1;
            var close = request.Messages.Last(m => m.Role == ModelRole.User).Text.Contains("Close", StringComparison.Ordinal);
            string Reference() { using var json = JsonDocument.Parse(results.Last().Text); return json.RootElement.GetProperty("matches")[0].GetProperty("ref").GetString()!; }
            object args; string tool;
            if (close)
            {
                if (step > 0)
                {
                    using var json = JsonDocument.Parse(results.Last().Text);
                    var text = json.RootElement.TryGetProperty("status", out var status) ? status.GetString()!
                        : json.RootElement.TryGetProperty("error", out var error) ? "Unable to confirm closure: " + error.GetString()
                        : "Browser closure is not authorized.";
                    foreach (var item in Answer(text)) yield return item; yield break;
                }
                if (!request.Tools!.Any(t => t.Name == ToolCatalog.BrowserClose))
                {
                    tool = ToolCatalog.CapabilitiesLoad; args = new { query = "browser.close", limit = 1 };
                }
                else { tool = ToolCatalog.BrowserClose; args = new { }; }
            }
            else
            {
                (string Tool, object Args) planned = step switch
                {
                    0 => (ToolCatalog.BrowserNavigate, new { url }),
                    1 => (ToolCatalog.BrowserFind, targetRole == "generic" ? (object)new { text = "Entry 1999" } : new { role = targetRole, name = "Entry 1999" }),
                    2 => (ToolCatalog.BrowserClick, new { @ref = Reference() }),
                    3 => (ToolCatalog.CapabilitiesLoad, new { query = "browser.fill_form", limit = 1 }),
                    4 => (ToolCatalog.BrowserFind, new { role = "textbox", name = "Summary" }),
                    5 => (ToolCatalog.BrowserFillForm, new { fields = new[] { new { @ref = Reference(), value = "Verified change" } } }),
                    6 => (ToolCatalog.BrowserFind, new { role = "textbox", name = "Summary" }),
                    _ => ("", new { })
                };
                (tool, args) = planned;
                if (tool.Length == 0)
                {
                    using var json = JsonDocument.Parse(results.Last().Text);
                    Assert.Equal("Verified change", json.RootElement.GetProperty("matches")[0].GetProperty("state").GetProperty("value").GetString());
                    foreach (var item in Answer("Opened Entry 1999 and verified Summary.")) yield return item;
                    yield break;
                }
            }
            Assert.Contains(request.Tools!, t => t.Name == tool);
            yield return new ModelToolCallEvent(new(request.ResponseId + ":" + step, tool, JsonSerializer.Serialize(args)));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
        private static IEnumerable<ModelGenerationEvent> Answer(string text) =>
            [new ModelSemanticResponseReady(new(text, new(ModelSpeechMode.Same, null), [])), new ModelCompleted(ModelStopReason.Completed)];
    }
}
