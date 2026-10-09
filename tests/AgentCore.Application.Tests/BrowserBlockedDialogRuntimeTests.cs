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
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Owned_native_dialog_suppresses_page_searches_and_preserves_recovery_or_partial_reply(bool authorized, bool repeat, bool resume)
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var model = new BlockedDialogModel(authorized, repeat, resume);
        var always = new[] { ToolCatalog.BrowserFind, ToolCatalog.BrowserClick, ToolCatalog.BrowserClose, ToolCatalog.ExecutionWait };
        var environment = new RoleEnvironment(Capabilities: new("Selected", authorized ? [..always, ToolCatalog.BrowserDialog] : always), Projection: new(always));
        await using var runtime = CreateCore(model, browser, null, [], environment: environment);
        try
        {
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(runtime.SessionId, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/credential-login")));
            var page = browser.ContextFor(runtime.SessionId)!.Pages[0];
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true }).EvaluateAsync("button => { sessionStorage.setItem('triggerCount', '0'); button.addEventListener('click', () => sessionStorage.setItem('triggerCount', String(Number(sessionStorage.getItem('triggerCount')) + 1)), { capture: true }); }");
            await runtime.AttachAsync();
            Assert.True(await runtime.SubmitUserTextAsync("Sign out if authorized and report verified progress."));
            await runtime.WaitUntilIdleAsync();
            AgentRun? suspended = null;
            if (resume)
            {
                suspended = await SessionRuntimeFixture.ResumeDurationWaitAsync(runtime);
                await runtime.WaitUntilIdleAsync();
            }
            var answer = runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant);
            Assert.True(answer.Status == EntryStatus.Completed, "Status=" + answer.Status + "; projections=" + string.Join(" / ", model.Requests.Select(r => string.Join(",", (r.Tools ?? []).Select(t => t.Name)))) + "; reply=" + answer.Text);
            Assert.Equal(authorized && !repeat ? "Sign-out verified." : "Sign-out is blocked and unverified.", answer.Text);
            var run = Assert.Single(await SessionRuntimeFixture.RunsForAsync(runtime));
            Assert.Equal(AgentRunStatus.Completed, run.Status);
            Assert.Equal(authorized, run.LoadedCapabilityIds.Contains(ToolCatalog.BrowserDialog));
            Assert.Equal(0, run.CapabilityLoadCount);
            if (suspended is not null)
            {
                Assert.Equal(suspended.AgentRunId, run.AgentRunId);
                Assert.Equal(suspended.ResponseId, run.ResponseId);
                Assert.Equal(suspended.AttemptCount, run.AttemptCount);
                Assert.Single(model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.BrowserClick).Select(m => m.ToolCallId).Distinct());
            }
            Assert.InRange(model.Requests.Count, 3, 8);
            if (repeat) Assert.Contains("browserBlocked", run.Checkpoint!.PayloadJson);
            if (!authorized || repeat)
                Assert.Null((await browser.ExecuteAsync(BrowserToolArguments.Request(runtime.SessionId, ToolCatalog.BrowserDialog,
                    JsonSerializer.SerializeToElement(new { operation = "dismiss" })))).ErrorCode);
            Assert.Equal(1, await page.EvaluateAsync<int>("() => Number(sessionStorage.getItem('triggerCount'))"));
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    private sealed class BlockedDialogModel(bool authorized, bool repeat, bool resume) : ILanguageModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            var step = Requests.Count - 1;
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            BrowserTarget Ref() { using var json = JsonDocument.Parse(results.Last().Text); return json.RootElement.GetProperty("matches")[0].GetProperty("target").Deserialize<BrowserTarget>(JsonSerializerOptions.Web)!; }
            if (step >= 2 && (repeat || !authorized || step < 4))
            {
                Assert.DoesNotContain(request.Tools ?? [], t => t.Name == ToolCatalog.BrowserFind);
                Assert.Equal(authorized && request.Tools is not null, (request.Tools ?? []).Any(t => t.Name == ToolCatalog.BrowserDialog));
                var environment = Assert.Single(request.Messages, m => m.Role == ModelRole.System
                    && m.Text.StartsWith(AgentCore.Application.Agents.PromptContextBuilder.ToolEnvironmentPrefix)).Text;
                Assert.DoesNotContain("browser.find", environment);
                Assert.DoesNotContain("browser.click", environment);
            }
            if (step >= 2 && request.Tools is null || step == 2 && !authorized || step == 5 && !repeat)
            {
                if (step == 5 && !repeat) Assert.Contains("Sign in", results.Last().Text);
                yield return new ModelSemanticResponseReady(new(authorized && !repeat ? "Sign-out verified." : "Sign-out is blocked and unverified.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            (string tool, object args) action = step switch
            {
                0 => (ToolCatalog.BrowserFind, new { target = new { by = "role", value = "button", name = "Sign out" } }),
                1 => (ToolCatalog.BrowserClick, new { target = Ref() }),
                2 when resume => (ToolCatalog.ExecutionWait, new { mode = "duration", seconds = 2 }),
                _ when repeat => (ToolCatalog.BrowserFind, new { target = new { by = "text", value = "irrelevant query " + step } }),
                2 => (ToolCatalog.BrowserDialog, new { operation = "inspect" }),
                3 => (ToolCatalog.BrowserDialog, new { operation = "accept" }),
                _ => (ToolCatalog.BrowserFind, new { target = new { by = "role", value = "button", name = "Sign in" } })
            };
            // The repeated case deliberately violates projection to exercise dispatch refusal too.
            yield return new ModelToolCallEvent(new("blocked-" + step, action.tool, JsonSerializer.Serialize(action.args)));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
            await Task.CompletedTask;
        }
    }
}
