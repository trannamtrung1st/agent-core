using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Browser;

namespace AgentCore.Application.Tests;

public sealed class BrowserDynamicRecoveryRuntimeTests
{
    [Fact]
    public async Task Owned_run_suppresses_unusable_mouse_and_closes_despite_unverified_logout()
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(default);
        try
        {
            var model = new DynamicModel(browser.HostPolicy.NavigationOrigins.Single() + "/settle-churn");
            var result = await BrowserLogoutRecoveryTests.RunAsync(browser, model, "Log out, then close the browser even if logout fails.");
            Assert.False(result.ContextOpen);
            var assistant = result.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant);
            Assert.Equal(EntryStatus.Completed, assistant.Status);
            Assert.Equal("Logout unverified. Browser closure confirmed.", assistant.Text);
            Assert.Contains(model.Requests.SelectMany(r => r.Messages), m => m.Role == ModelRole.Tool && m.Text.Contains("coordinate_evidence_unavailable") && m.Text.Contains("effectAttempted\":false"));
            Assert.Contains(model.Requests.SelectMany(r => r.Messages), m => m.Role == ModelRole.System && m.Text.Contains("coordinateEvidence=false") && m.Text.Contains("browser.close remains actionable"));
            Assert.Single(assistant.Envelope!.EffectReceipts!, r => r.Tool == ToolCatalog.BrowserClose);
            Assert.DoesNotContain(assistant.Envelope!.EffectReceipts!, r => r.Tool == ToolCatalog.BrowserVisionMouse);
        }
        finally { await browser.StopAsync(default); }
    }

    private sealed class DynamicModel(string url) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Vision: true, Tools: true, StructuredOutput: true);
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested(); Requests.Add(request);
            var step = Requests.Count - 1;
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            string SnapshotId()
            {
                using var capture = JsonDocument.Parse(results.Last().Text);
                Assert.False(capture.RootElement.GetProperty("coordinateEvidence").GetBoolean());
                Assert.True(capture.RootElement.GetProperty("imageDelivered").GetBoolean());
                Assert.Contains("dom_mutation_during_settle", capture.RootElement.GetProperty("coordinateEvidenceUnavailableReasons").EnumerateArray().Select(v => v.GetString()));
                Assert.Contains(request.Messages, m => m.Parts?.OfType<ModelImageContent>().Any() == true);
                return capture.RootElement.GetProperty("snapshotId").GetString()!;
            }
            if (step == 3) Assert.Contains("coordinate_evidence_unavailable", results.Last().Text);
            if (step == 4)
            {
                Assert.Contains("closed", results.Last().Text);
                yield return new ModelSemanticResponseReady(new("Logout unverified. Browser closure confirmed.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            (string tool, object args) action = step switch
            {
                0 => (ToolCatalog.BrowserNavigate, new { url }),
                1 => (ToolCatalog.BrowserScreenshot, new { }),
                2 => (ToolCatalog.BrowserVisionMouse, new { operation = "click", x = 20, y = 20, snapshotId = SnapshotId() }),
                _ => (ToolCatalog.BrowserClose, new { })
            };
            Assert.Contains(request.Tools ?? [], t => t.Name == action.tool);
            yield return new ModelToolCallEvent(new("dynamic-" + step, action.tool, JsonSerializer.Serialize(action.args)));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
