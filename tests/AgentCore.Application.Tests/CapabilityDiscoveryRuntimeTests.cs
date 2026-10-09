using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Browser;
using Microsoft.Playwright;

namespace AgentCore.Application.Tests;

public sealed partial class TerminalDisplayRepairTests
{
    [Fact]
    public async Task Discovery_loads_missing_form_tool_on_next_request_and_native_effect_grounds_final()
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var model = new DiscoveryFormModel();
        var names = new[] { ToolCatalog.CapabilitiesLoad, ToolCatalog.BrowserFind, ToolCatalog.BrowserFillForm, ToolCatalog.BrowserSnapshot };
        await using var runtime = CreateCore(model, browser, null, [], environment: new RoleEnvironment(Capabilities: new("Selected", names), Projection: new([])));
        try
        {
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(runtime.SessionId, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/")));
            await runtime.AttachAsync();
            Assert.True(await runtime.SubmitUserTextAsync("Fill the record field and verify the value."));
            await runtime.WaitUntilIdleAsync();
            var page = browser.ContextFor(runtime.SessionId)!.Pages[0];
            Assert.Equal("Discovery verified", await page.GetByLabel("Record", new() { Exact = true }).InputValueAsync());
            var final = runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant);
            Assert.True(final.Status == EntryStatus.Completed, final.Text + "\n" + model.LastResult);
            Assert.Contains("Discovery verified", final.Text);
            var run = Assert.Single(await SessionRuntimeFixture.RunsForAsync(runtime));
            Assert.Equal([ToolCatalog.BrowserFillForm], run.LoadedCapabilityIds);
            Assert.Equal(1, run.CapabilityLoadCount);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Repeated_equivalent_no_match_stops_loading_and_finishes_with_grounded_limitation()
    {
        var model = new NoMatchDiscoveryModel();
        await using var runtime = CreateCore(model, null, null, [], environment: new RoleEnvironment(Capabilities: new("Selected", [ToolCatalog.CapabilitiesLoad]), Projection: new([])));
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Teleport the browser."));
        await runtime.WaitUntilIdleAsync();
        var final = runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, final.Status);
        Assert.Contains("No eligible capability", final.Text);
        var run = Assert.Single(await SessionRuntimeFixture.RunsForAsync(runtime));
        Assert.Empty(run.LoadedCapabilityIds);
        Assert.Equal(2, run.CapabilityLoadCount); // Third/fourth calls are refused before admission.
        Assert.Equal(5, model.Requests);
        Assert.Contains("invalidCallRecovery", run.Checkpoint!.PayloadJson);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{broken")]
    [InlineData("{\"query\":\"browser\",\"limit\":0}")]
    public async Task Invalid_discovery_returns_distinct_actionable_receipt_without_admitting_load(string arguments)
    {
        await using var runtime = CreateCore(new InvalidDiscoveryModel(arguments), null, null, [],
            environment: new RoleEnvironment(Capabilities: new("Selected", [ToolCatalog.CapabilitiesLoad]), Projection: new([])));
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Discover a tool."));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(EntryStatus.Completed, runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant).Status);
        var run = Assert.Single(await SessionRuntimeFixture.RunsForAsync(runtime));
        Assert.Empty(run.LoadedCapabilityIds);
        Assert.Equal(0, run.CapabilityLoadCount);
    }

    private sealed class InvalidDiscoveryModel(string arguments) : ILanguageModel
    {
        private bool _called;
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!_called)
            {
                _called = true;
                yield return new ModelToolCallEvent(new("invalid-discovery", ToolCatalog.CapabilitiesLoad, arguments));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
            else
            {
                using var receipt = JsonDocument.Parse(request.Messages.Last(m => m.Role == ModelRole.Tool).Text);
                Assert.Equal("load_invalid", receipt.RootElement.GetProperty("outcome").GetString());
                Assert.Equal("invalid", receipt.RootElement.GetProperty("error").GetString());
                Assert.Contains("query", receipt.RootElement.GetProperty("nextStep").GetString());
                yield return new ModelSemanticResponseReady(new("Discovery arguments were invalid; no capability was loaded.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
            await Task.CompletedTask;
        }
    }

    private sealed class DiscoveryFormModel : ILanguageModel
    {
        private int _step;
        private BrowserTarget? _ref;
        public string LastResult { get; private set; } = "";
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var step = _step++;
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            LastResult = results.LastOrDefault()?.Text ?? "";
            BrowserTarget Ref() { using var json = JsonDocument.Parse(results.Last().Text); return json.RootElement.GetProperty("matches")[0].GetProperty("target").Deserialize<BrowserTarget>(JsonSerializerOptions.Web)!; }
            if (step == 0) Assert.DoesNotContain(request.Tools ?? [], t => t.Name == ToolCatalog.BrowserFillForm);
            if (step == 1)
            {
                using var load = JsonDocument.Parse(results.Last().Text);
                Assert.Equal("load_matched", load.RootElement.GetProperty("outcome").GetString());
                Assert.Contains("next model request", load.RootElement.GetProperty("nextStep").GetString());
                Assert.Contains(request.Tools ?? [], t => t.Name == ToolCatalog.BrowserFillForm);
            }
            (string, object) action = step switch
            {
                0 => (ToolCatalog.CapabilitiesLoad, new { query = "fill a form", limit = 1 }),
                1 => (ToolCatalog.BrowserFind, new { target = new { by = "label", value = "Record" } }),
                2 => (ToolCatalog.BrowserFillForm, new { fields = new[] { new { target = _ref = Ref(), value = "Discovery verified" } } }),
                3 => (ToolCatalog.BrowserSnapshot, new { target = _ref }),
                _ => ("", new { })
            };
            if (step == 4)
            {
                Assert.Contains("Discovery verified", results.Last().Text);
                yield return new ModelSemanticResponseReady(new("The record field now contains Discovery verified, confirmed by inspection.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            Assert.Contains(request.Tools ?? [], t => t.Name == action.Item1);
            yield return new ModelToolCallEvent(new("form-discovery-" + step, action.Item1, JsonSerializer.Serialize(action.Item2)));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            await Task.CompletedTask;
        }
    }

    private sealed class NoMatchDiscoveryModel : ILanguageModel
    {
        public int Requests { get; private set; }
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests++;
            if (Requests == 5)
            {
                Assert.Empty(request.Tools ?? []);
                Assert.Contains(request.Messages, m => m.Text.Contains("ineffective tool strategy"));
                yield return new ModelSemanticResponseReady(new("No eligible capability can teleport the browser. Discovery made no progress; this task is blocked.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            yield return new ModelToolCallEvent(new("no-match-" + Requests, ToolCatalog.CapabilitiesLoad,
                JsonSerializer.Serialize(new { query = Requests % 2 == 0 ? " BROWSER   TELEPORT " : "browser teleport", limit = Requests })));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            await Task.CompletedTask;
        }
    }
}
