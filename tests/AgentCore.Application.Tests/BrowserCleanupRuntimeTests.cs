using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Browser;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Playwright;

namespace AgentCore.Application.Tests;

public sealed partial class TerminalDisplayRepairTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Owned_run_resolves_only_authorized_signout_dialog_and_closes_near_deadline(bool authorized)
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-09T12:00:00Z"));
        var model = new CleanupModel(clock, authorized);
        var always = new[] { ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserFind, ToolCatalog.BrowserClick, ToolCatalog.BrowserClose, ToolCatalog.BrowserType };
        var environment = new RoleEnvironment(Capabilities: new("Selected", authorized ? [..always, ToolCatalog.BrowserDialog] : always), Projection: new(always));
        await using var runtime = CreateCore(model, browser, clock, [], environment: environment);
        try
        {
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(runtime.SessionId, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/credential-login")));
            var page = browser.ContextFor(runtime.SessionId)!.Pages[0];
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
            await runtime.AttachAsync();
            Assert.True(await runtime.SubmitUserTextAsync("Inspect the account, sign out, verify sign-out, then close."));
            await runtime.WaitUntilIdleAsync();
            Assert.Equal(EntryStatus.Completed, runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant).Status);
            Assert.True(page.IsClosed);
            var run = Assert.Single(await SessionRuntimeFixture.RunsForAsync(runtime));
            Assert.Equal(authorized, run.LoadedCapabilityIds.Contains(ToolCatalog.BrowserDialog));
            Assert.Equal(0, run.CapabilityLoadCount); // Core recovery does not consume discovery calls.
            Assert.Contains("Core browser cleanup phase", run.Checkpoint!.PayloadJson);
            Assert.Contains(model.Requests.Last().Messages, m => m.Text.Contains("remaining browser work budget"));
            Assert.DoesNotContain(model.Requests.Last().Tools ?? [], t => t.Name == ToolCatalog.BrowserType);
            Assert.Equal(authorized, model.SignedOutObserved);
            Assert.Single(runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant).Envelope!.EffectReceipts!, r => r.Tool == ToolCatalog.BrowserClose);
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(runtime.SessionId, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/credential-login")));
            // Ephemeral context is fresh; authentication verification was made before closure, not inferred from reopening.
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    private sealed class CleanupModel(FakeTimeProvider clock, bool authorized) : ILanguageModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public bool SignedOutObserved { get; private set; }
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request); var step = Requests.Count - 1;
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            if (step == 1) clock.Advance(TimeSpan.FromSeconds(220));
            BrowserTarget Ref() { using var json = JsonDocument.Parse(results.Last().Text); return json.RootElement.GetProperty("matches")[0].GetProperty("target").Deserialize<BrowserTarget>(JsonSerializerOptions.Web)!; }
            (string, object) action = step switch
            {
                0 => (ToolCatalog.BrowserFind, new { target = new { by = "role", value = "button", name = "Sign out" } }),
                1 => (ToolCatalog.BrowserClick, new { target = Ref() }),
                2 when authorized => (ToolCatalog.BrowserDialog, new { operation = "inspect" }),
                3 when authorized => (ToolCatalog.BrowserDialog, new { operation = "accept" }),
                4 when authorized => (ToolCatalog.BrowserFind, new { target = new { by = "role", value = "button", name = "Sign in" } }),
                5 when authorized => (ToolCatalog.BrowserClose, new { }),
                2 => (ToolCatalog.BrowserClose, new { }),
                _ => ("", new { })
            };
            if (step == 2) { Assert.Contains("dialog_pending", results.Last().Text); Assert.Contains("browser.dialog", results.Last().Text); }
            if (step == 5 && authorized) { Assert.Contains("Sign in", results.Last().Text); SignedOutObserved = true; }
            if (action.Item1 == "")
            {
                yield return new ModelSemanticResponseReady(new(authorized ? "Sign-out verified at the login screen; browser closure recorded." : "Dialog resolution is not authorized. Browser closed; sign-out unverified.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            Assert.Contains(request.Tools ?? [], t => t.Name == action.Item1);
            yield return new ModelToolCallEvent(new("cleanup-" + step, action.Item1, JsonSerializer.Serialize(action.Item2)));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            await Task.CompletedTask;
        }
    }
}
