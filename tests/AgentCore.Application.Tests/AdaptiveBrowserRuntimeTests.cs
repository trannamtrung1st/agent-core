using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
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
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace AgentCore.Application.Tests;

public sealed class AdaptiveBrowserRuntimeTests
{
    [Theory]
    [InlineData("accessible", false)]
    [InlineData("accessible", true)]
    [InlineData("dashboard", false)]
    [InlineData("dashboard", true)]
    [InlineData("weak", true)]
    public async Task Owned_run_observes_then_uses_permitted_evidence_and_independently_verifies(string mode, bool vision)
    {
        var browser = NewBrowser(); await browser.StartAsync(default);
        try
        {
            var names = new[] { ToolCatalog.BrowserNavigate, ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserFind,
                ToolCatalog.BrowserClick, ToolCatalog.BrowserType, ToolCatalog.BrowserScreenshot,
                ToolCatalog.BrowserVisionMouse, "browser.verify" };
            var definition = await CapabilityProjectionTests.Definition(names);
            definition = definition with { Environment = definition.Environment! with { Projection = new(names) } };
            var owner = Guid.NewGuid(); var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            var snapshot = RuntimeAgentRunStore.WithPins(new(1, id, 1, definition, SessionMode.Text, null, SessionStatus.Created,
                [], "", 0, null, null, now, now, AgentInstanceId: owner,
                ModelSelection: new("synthetic-offline/scripted", "primary-llm", "scripted", ModelSelectionSource.SystemDefault, null)));
            var memory = new InMemoryMemoryStore(); var runs = new RuntimeAgentRunStore(); runs.Bind(memory);
            await memory.SaveAsync(snapshot, 0);
            var instances = new InMemoryAgentInstanceStore();
            await instances.InsertAsync(new(owner, definition.Id, definition.Version, definition.Identity, AgentInstanceLifecycle.Active, now, now));
            var artifacts = new InMemoryArtifactStore(TimeProvider.System);
            var output = new CapturingSessionOutput();
            var model = new JourneyModel(browser.HostPolicy.NavigationOrigins.Single() + "/adaptive-browser.html?mode=" + mode, mode, vision);
            await using var runtime = SessionRuntimeFixture.Create(snapshot, model,
                new DefaultAgentBrain(new PromptContextBuilder(ToolConfigurationGates.AllowAll, browser)), memory, output,
                new SystemIdGenerator(TimeProvider.System), TimeProvider.System, NullLogger<SessionRuntime>.Instance,
                tools: new SessionToolExecutor(browser: browser, artifacts: artifacts, agentInstances: instances, configurationGate: ToolConfigurationGates.AllowAll),
                browserLease: browser, agentRuns: runs);
            await runtime.AttachAsync(); var source = Guid.NewGuid();
            Assert.True(await runtime.SubmitPersistedUserTextAsync(mode == "accessible" ? "Save inspection notes." : "Inspect the cooling project pump PUMP-1042.", source));
            await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromMinutes(2));
            Assert.True(!output.Items.Any(item => item.Payload is ErrorOutput), string.Join("\n", model.Failures));
            Assert.Equal("Application outcome verified.", runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant).Text);
            var page = browser.ContextFor(id)!.Pages.Single();
            Assert.Equal(mode == "accessible" ? "Notes saved: Inspection complete" : "Selected PUMP-1042", await page.GetByRole(AriaRole.Status).InnerTextAsync());
            Assert.Equal(ToolCatalog.BrowserNavigate, model.Calls[0]);
            Assert.Equal(ToolCatalog.BrowserSnapshot, model.Calls[1]);
            var visual = vision && mode != "accessible";
            Assert.Equal(visual ? 1 : 0, model.Calls.Count(n => n == ToolCatalog.BrowserScreenshot));
            Assert.Equal(mode == "weak" ? 1 : 0, model.Calls.Count(n => n == ToolCatalog.BrowserVisionMouse));
            Assert.Contains(model.Requests[0].Messages, m => m.Role == ModelRole.System && m.Text.Contains("semantic-first"));
            var run = await runs.ForSourceAsync(id, source); Assert.NotNull(run);
            if (visual)
            {
                var shot = model.Requests.SelectMany(r => r.Messages).First(m => m.Name == ToolCatalog.BrowserScreenshot);
                var image = Assert.Single(shot.Parts!.OfType<ModelImageContent>());
                using var metadata = JsonDocument.Parse(shot.Text);
                Assert.True(metadata.RootElement.GetProperty("imageDelivered").GetBoolean());
                Assert.Equal(metadata.RootElement.GetProperty("snapshotId").GetString(), metadata.RootElement.GetProperty("observation").GetProperty("snapshotId").GetString());
                Assert.Equal(metadata.RootElement.GetProperty("tabRef").GetString(), metadata.RootElement.GetProperty("observation").GetProperty("tabRef").GetString());
                var artifactId = metadata.RootElement.GetProperty("artifactId").GetGuid();
                Assert.NotNull(await artifacts.GetAsync(id, artifactId)); Assert.Null(await artifacts.GetAsync(Guid.NewGuid(), artifactId));
                Assert.DoesNotContain("private-fixture-password", shot.Text); Assert.DoesNotContain("private-fixture-token", shot.Text);
                // The exact captured bytes traverse the real provider serializer, without paid inference.
                var handler = new WireHandler(); using var http = new HttpClient(handler);
                var adapter = new OpenAICompatibleLanguageModel(http, new LanguageModelProviderOptions
                { Adapter = "OpenAICompatible", BaseUrl = "https://fixture.invalid/v1/", ApiKey = "fixture", DefaultModel = "fixture-vision", Vision = true, Tools = true });
                await foreach (var _ in adapter.GenerateAsync(new(Guid.NewGuid(), [shot]))) { }
                using var body = JsonDocument.Parse(handler.Body!);
                var wireImage = body.RootElement.GetProperty("messages")[1].GetProperty("content").EnumerateArray()
                    .Single(part => part.GetProperty("type").GetString() == "image_url").GetProperty("image_url").GetProperty("url").GetString();
                Assert.Equal("data:image/png;base64," + Convert.ToBase64String(image.Bytes), wireImage);
                Assert.Equal("tool", body.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
                Assert.Equal("user", body.RootElement.GetProperty("messages")[1].GetProperty("role").GetString());
                Assert.DoesNotContain("base64", body.RootElement.GetProperty("messages")[0].ToString());
            }
            else Assert.All(model.Requests.SelectMany(r => r.Messages), m => Assert.Empty(m.Parts?.OfType<ModelImageContent>() ?? []));
        }
        finally { await browser.StopAsync(default); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Browser_strategy_is_projected_only_for_eligible_authority(bool authorized, bool vision)
    {
        var names = authorized ? new[] { ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserScreenshot, ToolCatalog.BrowserVisionMouse } : [];
        var definition = await CapabilityProjectionTests.Definition(names);
        var context = CapabilityProjectionTests.Context(definition) with { ModelSupportsTools = true, ModelSupportsVision = vision };
        var request = new PromptContextBuilder(ToolConfigurationGates.AllowAll).Build(context, Guid.NewGuid());
        var guidance = request.Messages.Where(m => m.Text.StartsWith("Browser workflow:")).ToArray();
        if (!authorized) Assert.Empty(guidance);
        else
        {
            var text = Assert.Single(guidance).Text;
            if (vision) { Assert.Contains("proactively", text); Assert.Contains("snapshotId", text); }
            else { Assert.Contains("never claim visual understanding", text); Assert.DoesNotContain("proactively", text); }
        }
    }

    [Fact]
    public async Task Host_disabled_capture_is_not_recommended_to_a_vision_model()
    {
        var browser = new NativePlaywrightBrowser(new() { ScreenshotPrivacy = "Disabled" }, null);
        var definition = await CapabilityProjectionTests.Definition([ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserScreenshot, ToolCatalog.BrowserVisionMouse]);
        var context = CapabilityProjectionTests.Context(definition) with { ModelSupportsTools = true, ModelSupportsVision = true };
        var request = new PromptContextBuilder(ToolConfigurationGates.AllowAll, browser).Build(context, Guid.NewGuid());
        var text = Assert.Single(request.Messages, m => m.Text.StartsWith("Browser workflow:")).Text;
        Assert.Contains("Host policy disables screenshots", text);
        Assert.DoesNotContain("proactively", text);
        Assert.Contains("visual coordinates are unavailable", text);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Masked_capture_failure_budget_and_text_only_recovery(bool vision, bool unavailable)
    {
        var options = new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = true,
            FixturePort = 0, InteractionMode = "InteractiveDemo", Limits = new(CapturesPerScope: 1),
            NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] };
        var browser = new NativePlaywrightBrowser(options, loggerFactory: null); await browser.StartAsync(default);
        var id = Guid.NewGuid();
        try
        {
            await browser.ExecuteAsync(new(id, new BrowserNavigate(browser.HostPolicy.NavigationOrigins.Single() + "/adaptive-browser.html?mode=accessible")));
            var page = browser.ContextFor(id)!.Pages.Single();
            await page.EvaluateAsync("() => { const field=document.createElement('input'); field.setAttribute('data-sensitive',''); field.value='protected-visible-value'; document.body.appendChild(field); }");
            var fieldBox = (await page.Locator("[data-sensitive]").BoundingBoxAsync())!;
            var definition = await CapabilityProjectionTests.Definition(ToolCatalog.BrowserScreenshot, ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserVisionMouse);
            var artifacts = new InMemoryArtifactStore(TimeProvider.System);
            var executor = new SessionToolExecutor(browser: browser, artifacts: artifacts, configurationGate: ToolConfigurationGates.AllowAll);
            var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, SupportsVision: vision, CaptureScope: "one-capture");
            async Task<ToolExecutionResult> Run(string name, object args, int budget = ToolLimits.MaxOutputBytes) =>
                await executor.ExecuteAsync(definition, id, new(Guid.NewGuid().ToString(), name, JsonSerializer.Serialize(args)), budget, admission: admission);
            var failure = await Run(ToolCatalog.BrowserScreenshot, new { target = new { by = "role", value = "button", name = "Missing" } });
            Assert.Contains("target_missing", failure.Text); Assert.Contains("semantic observation", failure.Text); Assert.Empty(failure.Parts ?? []);
            if (unavailable) browser.CaptureProbe = () => new TimeoutException("semantic unavailable");
            var capture = await Run(ToolCatalog.BrowserScreenshot, new { });
            browser.CaptureProbe = null;
            using var metadata = JsonDocument.Parse(capture.Text);
            Assert.Equal(unavailable, metadata.RootElement.GetProperty("observationUnavailable").GetBoolean());
            Assert.Equal(vision, metadata.RootElement.GetProperty("imageDelivered").GetBoolean());
            Assert.Equal(vision, metadata.RootElement.GetProperty("coordinateEvidence").GetBoolean());
            Assert.DoesNotContain("protected-visible-value", capture.Text);
            Assert.True(metadata.RootElement.GetProperty("redactions").GetInt32() > 0);
            var artifact = metadata.RootElement.GetProperty("artifactId").GetGuid();
            await using var content = await artifacts.OpenContentAsync(id, artifact);
            using var bytes = new MemoryStream(); await content.CopyToAsync(bytes);
            using var bitmap = SkiaSharp.SKBitmap.Decode(bytes.ToArray());
            var pixel = bitmap.GetPixel((int)(fieldBox.X + fieldBox.Width / 2), (int)(fieldBox.Y + fieldBox.Height / 2));
            Assert.True(pixel.Red < 32 && pixel.Green < 32 && pixel.Blue < 32);
            if (vision) Assert.Equal(bytes.ToArray(), Assert.Single(capture.Parts!.OfType<ModelImageContent>()).Bytes);
            else { Assert.Empty(capture.Parts ?? []); Assert.Contains("did not receive image content", capture.Text); }
            var limit = await Run(ToolCatalog.BrowserScreenshot, new { });
            Assert.Contains("capture_limit", limit.Text); Assert.Contains("do not retry", limit.Text); Assert.Empty(limit.Parts ?? []);
            Assert.DoesNotContain("error", (await Run(ToolCatalog.BrowserSnapshot, new { })).Text);
            var pixelArgs = new { operation = "click", x = 680, y = 165, snapshotId = metadata.RootElement.GetProperty("snapshotId").GetString() };
            if (!vision) Assert.Contains("forbidden", (await Run(ToolCatalog.BrowserVisionMouse, pixelArgs)).Text);
            admission = admission with { CaptureScope = "new-scope" };
            var bounded = await Run(ToolCatalog.BrowserScreenshot, new { }, 512);
            Assert.InRange(Encoding.UTF8.GetByteCount(bounded.Text), 1, 512);
            using var small = JsonDocument.Parse(bounded.Text);
            Assert.True(small.RootElement.GetProperty("observationTruncated").GetBoolean());
            Assert.Equal(vision, small.RootElement.GetProperty("imageDelivered").GetBoolean());
            Assert.NotNull(await artifacts.GetAsync(id, small.RootElement.GetProperty("artifactId").GetGuid()));
        }
        finally { await browser.StopAsync(default); }
    }

    internal static NativePlaywrightBrowser NewBrowser() => new(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = true,
        FixturePort = 0, InteractionMode = "InteractiveDemo", NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, loggerFactory: null);

    private sealed class JourneyModel(string url, string mode, bool vision) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Vision: vision, Tools: true, StructuredOutput: true);
        public List<ModelRequest> Requests { get; } = [];
        public List<string> Calls { get; } = [];
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
        private async IAsyncEnumerable<ModelGenerationEvent> GenerateCore(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested(); Requests.Add(request);
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            Assert.All(results, r => Assert.DoesNotContain("\"error\":", r.Text));
            var plan = new List<(string Name, object Args)>
            { (ToolCatalog.BrowserNavigate, new { url }), (ToolCatalog.BrowserSnapshot, new { }) };
            if (mode == "accessible")
            {
                plan.Add((ToolCatalog.BrowserType, new { target = new { by = "label", value = "Notes" }, text = "Inspection complete" }));
                plan.Add((ToolCatalog.BrowserClick, new { target = new { by = "role", value = "button", name = "Save notes" } }));
            }
            else
            {
                plan.Add((ToolCatalog.BrowserClick, new { target = new { by = "role", value = "button", name = "Cooling project" } }));
                if (vision) plan.Add((ToolCatalog.BrowserScreenshot, new { }));
                if (mode == "weak")
                {
                    // Deterministic transport test: these fixture pixels are scripted, not a claim of image understanding.
                    var shot = results.LastOrDefault(m => m.Name == ToolCatalog.BrowserScreenshot);
                    var snapshotId = shot is null ? "" : JsonDocument.Parse(shot.Text).RootElement.GetProperty("snapshotId").GetString();
                    plan.Add((ToolCatalog.BrowserVisionMouse, new { operation = "click", x = 680, y = 165, snapshotId }));
                }
                else plan.Add((ToolCatalog.BrowserClick, new { target = new { by = "role", value = "button", name = "Inspect", within = new { by = "role", value = "row", hasText = "PUMP-1042" } } }));
            }
            plan.Add(("browser.verify", new { condition = "text", target = new { by = "role", value = "status" }, text = mode == "accessible" ? "Notes saved: Inspection complete" : "Selected PUMP-1042" }));
            var step = Requests.Count - 1;
            if (step < plan.Count)
            {
                var action = plan[step]; Assert.Contains(request.Tools!, t => t.Name == action.Name); Calls.Add(action.Name);
                yield return new ModelToolCallEvent(new("adaptive-" + step, action.Name, JsonSerializer.Serialize(action.Args)));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
            else
            {
                Assert.Contains("\"applicationOutcomeVerified\":true", results.Last().Text);
                yield return new ModelSemanticResponseReady(new("Application outcome verified.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
        }
    }

    private sealed class WireHandler : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }
}
