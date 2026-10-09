using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Credentials;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Application.Tests;

public sealed partial class TerminalDisplayRepairTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Protected_asset_journey_preserves_cleanup_capacity_and_truthful_partial_when_hover_not_granted(bool grantHover)
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var root = Path.Combine(Path.GetTempPath(), "budget-credential-" + Guid.NewGuid().ToString("N"));
        try
        {
            var origin = browser.HostPolicy.NavigationOrigins.Single();
            var instances = new InMemoryAgentInstanceStore(); var credentialStore = new InMemoryCredentialStore(instances);
            var credentials = new CredentialService(credentialStore, credentialStore, new LocalCredentialProtector(root), instances,
                new SystemIdGenerator(TimeProvider.System), TimeProvider.System);
            var model = new BudgetAssetModel(origin, grantHover);
            var grants = new[] { ToolCatalog.BrowserNavigate, ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserType,
                ToolCatalog.BrowserFillCredential, ToolCatalog.BrowserClick, ToolCatalog.BrowserClose, "browser.verify", ToolCatalog.BrowserWait,
                "browser.scroll", ToolCatalog.BrowserPressKey }.Concat(grantHover ? [ToolCatalog.BrowserHover] : Array.Empty<string>()).ToArray();
            var executor = new SessionToolExecutor(browser: browser, agentInstances: instances, credentials: credentials, configurationGate: ToolConfigurationGates.AllowAll);
            await using var runtime = CreateCore(model, browser, null, grants, executor: executor);
            var snapshot = runtime.Snapshot; var now = DateTimeOffset.UtcNow;
            await instances.InsertAsync(new(snapshot.AgentInstanceId, snapshot.Definition.Id, snapshot.Definition.Version,
                snapshot.Definition.Identity, AgentInstanceLifecycle.Active, now, now));
            var secret = await credentials.CreateAsync("Synthetic asset account", "Password", null, [origin], "synthetic-budget-password");
            await credentials.BindAsync(snapshot.AgentInstanceId, secret.CredentialId, "asset-account", 1);
            await runtime.AttachAsync();
            Assert.True(await runtime.SubmitUserTextAsync("Sign in using the protected asset account, open Data Management, AI Engineering, Pump 002 and inspect Attribute, Table and Media. Log out, verify sign-out and close browser."));
            await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromMinutes(2));
            var run = Assert.Single(await SessionRuntimeFixture.RunsForAsync(runtime));
            Assert.Equal(AgentRunStatus.Completed, run.Status);
            Assert.True(model.AssetObserved); Assert.Equal(grantHover, model.SignOutVerified);
            Assert.Contains(RunFinalization.CleanupMarker, run.Checkpoint!.PayloadJson);
            Assert.Contains("closed", run.Checkpoint.PayloadJson);
            Assert.True(run.Checkpoint.StepCount <= 48);
            Assert.True(Encoding.UTF8.GetByteCount(run.Checkpoint.PayloadJson) + 8192 < AgentRunLimits.MaxCheckpointBytes);
            Assert.DoesNotContain("synthetic-budget-password", run.Checkpoint.PayloadJson);
            Assert.DoesNotContain("synthetic-budget-password", string.Join('\n', model.Requests.SelectMany(r => r.Messages).Select(m => m.Text)));
            Assert.DoesNotContain(model.Requests.SelectMany(r => r.Messages).SelectMany(m => m.ToolCalls ?? []), c => c.ArgumentsJson.Contains("synthetic-budget-password"));
            Assert.DoesNotContain(model.Requests.Last().Tools ?? [], t => t.Name == ToolCatalog.BrowserType);
            if (!grantHover) Assert.DoesNotContain(model.Requests.SelectMany(r => r.Tools ?? []), t => t.Name == ToolCatalog.BrowserHover);
            Assert.Contains(grantHover ? "Sign-out verified" : "Sign-out unverified", runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant).Text);
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class BudgetAssetModel(string origin, bool grantHover) : ILanguageModel
    {
        public List<ModelRequest> Requests { get; } = [];
        public bool AssetObserved { get; private set; }
        public bool SignOutVerified { get; private set; }
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var step = Requests.Count; Requests.Add(request);
            if (step == 9) { Assert.Contains("Pump 002", request.Messages.Last(m => m.Role == ModelRole.Tool).Text); AssetObserved = true; }
            if (step == 39 && grantHover) { Assert.Contains("\"applicationOutcomeVerified\":true", request.Messages.Last(m => m.Role == ModelRole.Tool).Text); SignOutVerified = true; }
            object Target(string name, string value = "button") => new { by = "role", value, name, exact = true };
            (string Tool, object Args) action = step switch
            {
                0 => (ToolCatalog.BrowserNavigate, new { url = origin + "/budget-workflow" }),
                1 => (ToolCatalog.BrowserType, new { target = new { by = "label", value = "Email" }, text = "synthetic@example.test" }),
                2 => (ToolCatalog.BrowserFillCredential, new { target = new { by = "label", value = "Password" }, credentialRef = "asset-account" }),
                3 => (ToolCatalog.BrowserClick, new { target = Target("Sign in") }),
                4 or 8 or 10 or 12 or 14 => (ToolCatalog.BrowserSnapshot, new { }),
                5 => (ToolCatalog.BrowserClick, new { target = Target("Data Management") }),
                6 => (ToolCatalog.BrowserClick, new { target = Target("AI Engineering") }),
                7 => (ToolCatalog.BrowserClick, new { target = Target("Pump 002") }),
                9 => (ToolCatalog.BrowserClick, new { target = Target("Attribute", "tab") }),
                11 => (ToolCatalog.BrowserClick, new { target = Target("Table", "tab") }),
                13 => (ToolCatalog.BrowserClick, new { target = Target("Media", "tab") }),
                >= 15 and < 36 when step % 2 == 1 => (ToolCatalog.BrowserClick, new { target = Target(new[] { "Attribute", "Table", "Media" }[(step / 2) % 3], "tab") }),
                >= 15 and < 36 => (ToolCatalog.BrowserSnapshot, new { }),
                36 when grantHover => (ToolCatalog.BrowserHover, new { target = Target("Account") }),
                37 when grantHover => (ToolCatalog.BrowserClick, new { target = Target("Log out") }),
                38 when grantHover => ("browser.verify", new { condition = "visible", target = Target("Sign in") }),
                39 when grantHover => (ToolCatalog.BrowserSnapshot, new { }),
                40 when grantHover => (ToolCatalog.BrowserClose, new { }),
                36 => (ToolCatalog.BrowserClose, new { }),
                _ => ("", new { })
            };
            if (step >= 36) Assert.Contains(request.Messages, m => m.Text == RunFinalization.CleanupMarker);
            if (action.Tool.Length == 0)
            {
                yield return new ModelSemanticResponseReady(new(grantHover ? "Inspected Attribute, Table and Media. Sign-out verified independently. Browser closure recorded." : "Inspected Attribute, Table and Media. Sign-out unverified: account hover is not authorized. Browser closure recorded.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            Assert.Contains(request.Tools!, t => t.Name == action.Tool);
            yield return new ModelToolCallEvent(new("asset-" + step, action.Tool, JsonSerializer.Serialize(action.Args)));
            yield return new ModelCompleted(ModelStopReason.ToolCalls); await Task.CompletedTask;
        }
    }
}
