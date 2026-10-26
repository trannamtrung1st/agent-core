using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [TenantBrowserChromiumTheory]
    [InlineData(false, "browser-dense.html")] [InlineData(true, "browser-dense.html")]
    [InlineData(false, "browser-custom-tree.html")] [InlineData(true, "browser-custom-tree.html")]
    public async Task Tenant_native_dense_Browser_discovers_last_entry_updates_from_current_targets_and_resets_load_state_before_independent_close_roots(bool sqlite, string fixture)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-dense-browser-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new OwnedDenseBrowserModel(fixture == "browser-custom-tree.html");
            void Configure(IServiceCollection services)
            {
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = true; options.FixturePort = 0;
                options.ProfileRoot = Path.Combine(root, "browser"); options.ProfileMode = "EphemeralSession"; options.PolicyMode = "Restricted";
                options.NavigationOrigins = []; options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var saved = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = saved.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = saved.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Current dense Browser authority", ["agents.read", "sessions.read", "browser.read", "browser.use"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>());
            model.Url = native.Fixture.Origin + "/" + fixture;
            model.BeforeEntryClick = async () => await native.ContextFor(session)!.Pages.Single().GetByRole(Microsoft.Playwright.AriaRole.Tree).EvaluateAsync("el => render()");
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
            var decisions = new HashSet<Guid>(); var roots = new HashSet<Guid>();
            async Task<AgentRun> Turn(string text)
            {
                var current = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!;
                var source = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text,
                    expectedRevision = await Revision(owner, tenant), expectedSessionRevision = current.Snapshot.Revision }))).GetProperty("runId").GetGuid();
                using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                while (true)
                {
                    await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync(deadline.Token);
                    await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
                    var run = (await CurrentApprovalExecution(runs, tenant, source))!;
                    if (run.Status == AgentRunStatus.WaitingForApproval && decisions.Add(run.ApprovalRequestId!.Value))
                    {
                        var request = (await approvals.FindApprovalAsync(tenant, run.ApprovalRequestId.Value, deadline.Token))!;
                        Assert.NotNull(request.Operation.BrowserCommand); Assert.Equal(human, request.OriginalInitiatorPrincipalId);
                        var view = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}", deadline.Token));
                        var decision = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                            expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision }, deadline.Token);
                        Assert.True(decision.IsSuccessStatusCode, await decision.Content.ReadAsStringAsync(deadline.Token));
                    }
                    if (run.IsTerminal && run.Result?.OutcomeKind != AgentRunOutcomeKind.Continued)
                    {
                        Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code}: {run.Failure?.Summary}; phase={model.Phase}; " +
                            string.Join(" | ", model.Requests.Last().Messages.Where(m => m.Role == ModelRole.Tool).TakeLast(3).Select(m => m.Text[..Math.Min(700, m.Text.Length)])));
                        Assert.True(roots.Add(run.Admission.Activation.RootActivationId)); return run;
                    }
                    await Task.Delay(25, deadline.Token);
                }
            }
            var completed = await Turn("Inspect the dense catalog, open Entry 1999 and update Summary to Verified change. Verify from a fresh read.");
            var page = native.ContextFor(session)!.Pages.Single();
            Assert.Equal("Opened Entry 1999", await page.GetByRole(Microsoft.Playwright.AriaRole.Status).InnerTextAsync());
            Assert.Equal("Verified change", await page.GetByRole(Microsoft.Playwright.AriaRole.Textbox, new() { Name = "Summary" }).InputValueAsync());
            Assert.Equal(1, completed.CapabilityLoadCount); Assert.Empty(completed.LoadedCapabilityIds);
            Assert.True(model.Verified); Assert.Equal(3, decisions.Count); Assert.Equal(1, model.EntryClicks); Assert.Equal(1, model.FormWrites);
            var modelText = string.Join("\n", model.Requests.SelectMany(r => r.Messages).Select(m => m.Text));
            Assert.DoesNotContain("dense-private-token", modelText); Assert.DoesNotContain("custom-private-token", modelText);
            Assert.Contains("load_already_projected", completed.Checkpoint!.PayloadJson);
            foreach (var status in new[] { "closed", "already_closed" })
            {
                model.Closing = true;
                var closed = await Turn("Close the browser and report the actual native status.");
                Assert.Equal(0, closed.CapabilityLoadCount); Assert.Empty(closed.LoadedCapabilityIds);
                Assert.Null(native.ContextFor(session)); Assert.Equal(status, model.LastCloseStatus);
                var history = (await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items");
                Assert.Equal(status, history.EnumerateArray().Last(h => h.GetProperty("role").GetString() == "assistant").GetProperty("text").GetString());
            }
            Assert.Equal(5, decisions.Count); Assert.Equal(3, roots.Count);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class OwnedDenseBrowserModel(bool custom) : ILanguageModel
    {
        public string Url = ""; public int Phase, EntryClicks, FormWrites; public bool Closing, Verified;
        public string? LastCloseStatus; public Func<Task>? BeforeEntryClick;
        private JsonElement entry, summary;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public ModelCapabilities Capabilities => new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            var close = results.LastOrDefault(m => m.ToolCallId == "dense-close");
            if (Closing && close is not null)
            {
                using var json = JsonDocument.Parse(close.Text); Assert.True(json.RootElement.GetProperty("commandCompleted").GetBoolean());
                LastCloseStatus = json.RootElement.GetProperty("browser").GetProperty("status").GetString();
                yield return new ModelSemanticResponseReady(new(LastCloseStatus!, new(ModelSpeechMode.Same, null), [])); yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            if (Closing)
            {
                Assert.Contains(ToolCatalog.BrowserClose, Assert.Single(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson);
                yield return new ModelToolCallEvent(new("dense-close", ToolCatalog.RequestAuthorization, """{"operation":"run_browser_command","command":{"tool":"browser.close","arguments":{}}}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            JsonElement Match(string id)
            {
                using var json = JsonDocument.Parse(results.Single(m => m.ToolCallId == id).Text);
                Assert.True(json.RootElement.GetProperty("tenantBrowserRead").GetBoolean());
                return Assert.Single(json.RootElement.GetProperty("matches").EnumerateArray()).Clone();
            }
            if (Phase == 2)
            {
                using var invalid = JsonDocument.Parse(results.Last().Text); Assert.Equal("invalid", invalid.RootElement.GetProperty("error").GetString());
            }
            if (Phase == 4) entry = Match("dense-entry").GetProperty("target").Clone();
            if (Phase == 7) summary = Match("dense-summary").GetProperty("target").Clone();
            if (Phase == 9)
            {
                Assert.Equal("Verified change", Match("dense-verified").GetProperty("state").GetProperty("value").GetString()); Verified = true;
                yield return new ModelSemanticResponseReady(new("Opened Entry 1999 and verified Summary.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            if (Phase == 5 && BeforeEntryClick is not null) { var before = BeforeEntryClick; BeforeEntryClick = null; await before(); }
            (string Tool, object Args, string Id) action = Phase switch
            {
                0 => (ToolCatalog.BrowserNavigate, new { url = Url }, "dense-open"),
                1 => (ToolCatalog.BrowserFind, new { role = "textbox", name = "Email", text = "Email", label = "Email", placeholder = "Enter your email", scopeRef = "", frameRef = "" }, "dense-invalid"),
                2 => (ToolCatalog.CapabilitiesLoad, new { query = ToolCatalog.BrowserFind, limit = 1 }, "dense-discover"),
                3 => (ToolCatalog.BrowserFind, custom ? (object)new { target = new { by = "text", value = "Entry 1999" } } : new { target = new { by = "role", value = "treeitem", name = "Entry 1999" } }, "dense-entry"),
                4 => (ToolCatalog.BrowserSnapshot, new { }, "dense-observe"),
                5 => (ToolCatalog.BrowserClick, new { target = entry }, "dense-click"),
                6 => (ToolCatalog.BrowserFind, new { target = new { by = "role", value = "textbox", name = "Summary" } }, "dense-summary"),
                7 => (ToolCatalog.BrowserFillForm, new { fields = new[] { new { target = summary, value = "Verified change" } } }, "dense-fill"),
                _ => (ToolCatalog.BrowserFind, new { target = new { by = "role", value = "textbox", name = "Summary" } }, "dense-verified")
            };
            if (action.Tool == ToolCatalog.BrowserClick) EntryClicks++;
            if (action.Tool == ToolCatalog.BrowserFillForm) FormWrites++;
            var args = JsonSerializer.Serialize(action.Args); var effect = Phase != 1 && action.Tool != ToolCatalog.CapabilitiesLoad
                && !TenantApprovalRules.BrowserIntent(Guid.Parse("00000000-0000-0000-0000-000000000001"), action.Tool, args).ReadOnly;
            if (effect) Assert.Contains(action.Tool, Assert.Single(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson);
            else Assert.Contains(request.Tools!, t => t.Name == action.Tool);
            Phase++;
            yield return new ModelToolCallEvent(effect ? new(action.Id, ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "run_browser_command", command = new { tool = action.Tool, arguments = action.Args } }))
                : new(action.Id, action.Tool, args));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
