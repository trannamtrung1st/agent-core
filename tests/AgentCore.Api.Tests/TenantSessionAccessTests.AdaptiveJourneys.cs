using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
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
    [InlineData(false, "accessible", false)] [InlineData(true, "accessible", false)]
    [InlineData(false, "accessible", true)] [InlineData(true, "accessible", true)]
    [InlineData(false, "dashboard", false)] [InlineData(true, "dashboard", false)]
    [InlineData(false, "dashboard", true)] [InlineData(true, "dashboard", true)]
    [InlineData(false, "weak", true)] [InlineData(true, "weak", true)]
    public Task Tenant_native_adaptive_journey_uses_current_semantic_or_delivered_visual_evidence_and_verifies_the_application(bool sqlite, string mode, bool vision)
        => OwnedAdaptiveJourney(sqlite, mode, vision, false, false, "Inspect the application and verify the requested change.", false, false, adaptive: true);

    [TenantBrowserChromiumTheory]
    [InlineData(false, "accessible", true, false, false, "Log out, then close the browser.", true, true)]
    [InlineData(true, "accessible", true, false, false, "Log out, then close the browser.", true, true)]
    // Tenant coordinate commands are deliberately unavailable; the visible icon
    // can be described from a delivered image but cannot acquire pixel authority.
    [InlineData(false, "icon", true, false, false, "Log out, then close the browser.", false, true)]
    [InlineData(true, "icon", true, false, false, "Log out, then close the browser.", false, true)]
    [InlineData(false, "icon", false, false, false, "Log out, then close the browser.", false, true)]
    [InlineData(true, "icon", false, false, false, "Log out, then close the browser.", false, true)]
    [InlineData(false, "icon", true, true, false, "Log out, then close the browser.", false, true)]
    [InlineData(true, "icon", true, true, false, "Log out, then close the browser.", false, true)]
    [InlineData(false, "icon", true, false, true, "Log out, then close the browser.", false, true)]
    [InlineData(true, "icon", true, false, true, "Log out, then close the browser.", false, true)]
    [InlineData(false, "failure", true, false, false, "Log out, then close the browser even if logout fails.", false, true)]
    [InlineData(true, "failure", true, false, false, "Log out, then close the browser even if logout fails.", false, true)]
    [InlineData(false, "failure", true, false, false, "Log out; only close the browser after verified logout.", false, false)]
    [InlineData(true, "failure", true, false, false, "Log out; only close the browser after verified logout.", false, false)]
    [InlineData(false, "accessible", true, false, false, "Log out but don't close the browser.", true, false)]
    [InlineData(true, "accessible", true, false, false, "Log out but don't close the browser.", true, false)]
    [InlineData(false, "accessible", true, false, false, "Keep me signed in and close the browser.", false, true)]
    [InlineData(true, "accessible", true, false, false, "Keep me signed in and close the browser.", false, true)]
    public Task Tenant_native_logout_journey_reports_verified_signout_and_requested_closure_independently(bool sqlite, string mode, bool vision, bool disabled, bool exhausted, string input, bool logout, bool close)
        => OwnedAdaptiveJourney(sqlite, mode, vision, disabled, exhausted, input, logout, close, adaptive: false);

    private async Task OwnedAdaptiveJourney(bool sqlite, string mode, bool vision, bool disabled, bool exhausted, string input, bool logout, bool close, bool adaptive)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-native-adaptive-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new OwnedAdaptiveModel(mode, vision, disabled, exhausted, input, adaptive);
            void Configure(IServiceCollection services)
            {
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = true; options.FixturePort = 0;
                options.ProfileRoot = Path.Combine(root, "browser"); options.ProfileMode = "EphemeralSession"; options.PolicyMode = "Restricted";
                options.NavigationOrigins = []; options.InteractionOrigins = null; options.TargetOrigins = null; options.ResourceOrigins = [];
                options.ScreenshotPrivacy = disabled ? "Disabled" : "Protected"; options.Limits = new(CapturesPerScope: exhausted ? 1 : 4);
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                offeringId = vision ? "dca30000-0000-4000-8000-000000000003" : null }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Current native journey authority", ["agents.read", "sessions.read", "browser.read", "browser.use", "artifacts.create", "artifacts.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>());
            model.Url = native.Fixture.Origin + (adaptive ? "/adaptive-browser.html" : "/browser-logout.html") + "?mode=" + mode;
            var initial = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = input, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>(); var decided = new HashSet<Guid>(); AgentRun final;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            while (true)
            {
                await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync(deadline.Token);
                await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
                final = (await CurrentApprovalExecution(runs, tenant, initial))!;
                if (final.Status == AgentRunStatus.WaitingForApproval && decided.Add(final.ApprovalRequestId!.Value))
                {
                    var request = (await approvals.FindApprovalAsync(tenant, final.ApprovalRequestId.Value, deadline.Token))!;
                    Assert.NotNull(request.Operation.BrowserCommand); Assert.Equal(human, request.OriginalInitiatorPrincipalId);
                    var view = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}", deadline.Token));
                    var decision = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                        expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision }, deadline.Token);
                    Assert.True(decision.IsSuccessStatusCode, await decision.Content.ReadAsStringAsync(deadline.Token));
                }
                if (final.IsTerminal && final.Result?.OutcomeKind != AgentRunOutcomeKind.Continued) break;
                await Task.Delay(25, deadline.Token);
            }
            Assert.True(final.Status == AgentRunStatus.Completed, $"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}; phase={model.Phase}; {string.Join(" | ", model.Requests.Last().Messages.Where(m => m.Role == ModelRole.Tool).TakeLast(3).Select(m => m.Text))}");
            Assert.DoesNotContain("private-fixture-password", JsonSerializer.Serialize(model.Requests)); Assert.DoesNotContain("private-fixture-token", JsonSerializer.Serialize(model.Requests));
            Assert.DoesNotContain(model.Calls, c => c.Name == ToolCatalog.BrowserDialog);
            if (adaptive)
            {
                var page = native.ContextFor(session)!.Pages.Single();
                if (mode == "weak")
                {
                    Assert.False(model.Verified); Assert.True(model.MouseRefused);
                    Assert.NotEqual("Selected PUMP-1042", await page.GetByRole(Microsoft.Playwright.AriaRole.Status).InnerTextAsync());
                }
                else
                {
                    Assert.Equal(mode == "accessible" ? "Notes saved: Inspection complete" : "Selected PUMP-1042", await page.GetByRole(Microsoft.Playwright.AriaRole.Status).InnerTextAsync());
                    Assert.True(model.Verified);
                }
                Assert.Equal(mode == "weak" ? 1 : 0, model.Calls.Count(c => c.Name == ToolCatalog.BrowserVisionMouse));
                Assert.Equal(vision && mode != "accessible" ? 1 : 0, model.Calls.Count(c => c.Name == ToolCatalog.BrowserScreenshot));
            }
            else
            {
                Assert.Equal(logout, model.Verified); Assert.Equal(close, native.ContextFor(session) is null);
                Assert.Equal(close ? 1 : 0, model.Calls.Count(c => c.Name == ToolCatalog.BrowserClose));
                if (mode != "accessible") Assert.Contains(model.Requests.SelectMany(r => r.Messages), m => m.Role == ModelRole.System && m.Text.Contains("Semantic targeting has not located", StringComparison.Ordinal));
            }
            var images = model.Requests.SelectMany(r => r.Messages).SelectMany(m => m.Parts?.OfType<ModelImageContent>() ?? []).ToArray();
            if (vision && model.Calls.Any(c => c.Name == ToolCatalog.BrowserScreenshot)) Assert.NotEmpty(images); else Assert.Empty(images);
            Assert.DoesNotContain("data:image/", final.Checkpoint!.PayloadJson);
            Assert.All(images, image => Assert.DoesNotContain(Convert.ToBase64String(image.Bytes.AsSpan()), final.Checkpoint.PayloadJson));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class OwnedAdaptiveModel(string mode, bool vision, bool disabled, bool exhausted, string input, bool adaptive) : ILanguageModel
    {
        public string Url = ""; public int Phase; private int load; private string? snapshotId;
        public bool Verified, MouseRefused, Closed;
        public List<ModelToolCall> Calls { get; } = [];
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public ModelCapabilities Capabilities => new(true, true, Vision: vision, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            object Button(string name) => new { by = "role", value = "button", name, exact = true };
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            var capture = results.LastOrDefault(m => m.ToolCallId?.StartsWith("journey-capture", StringComparison.Ordinal) == true);
            string Snapshot()
            {
                if (snapshotId is not null) return snapshotId;
                Assert.NotNull(capture); using var json = JsonDocument.Parse(capture.Text);
                Assert.True(json.RootElement.GetProperty("captureImageProvided").GetBoolean()); Assert.NotEmpty(capture.Parts?.OfType<ModelImageContent>() ?? []);
                return snapshotId = json.RootElement.GetProperty("browser").GetProperty("observation").GetProperty("snapshotId").GetString()!;
            }
            var plan = new List<(string Tool, object Args)> { (ToolCatalog.BrowserNavigate, new { url = Url }) };
            var visual = vision && !disabled && !exhausted && mode != "accessible";
            if (adaptive)
            {
                plan.Add((ToolCatalog.BrowserSnapshot, new { }));
                if (mode == "accessible")
                {
                    plan.Add((ToolCatalog.BrowserType, new { target = new { by = "label", value = "Notes" }, text = "Inspection complete" }));
                    plan.Add((ToolCatalog.BrowserClick, new { target = Button("Save notes") }));
                }
                else
                {
                    plan.Add((ToolCatalog.BrowserClick, new { target = Button("Cooling project") }));
                    if (vision) plan.Add((ToolCatalog.BrowserScreenshot, new { }));
                    if (mode == "weak") plan.Add((ToolCatalog.BrowserVisionMouse, new { operation = "click", x = 680, y = 165, snapshotId = capture is null ? "" : Snapshot() }));
                    else plan.Add((ToolCatalog.BrowserClick, new { target = new { by = "role", value = "button", name = "Inspect", within = new { by = "role", value = "row", hasText = "PUMP-1042" } } }));
                }
                if (mode == "weak") plan.Add((ToolCatalog.BrowserSnapshot, new { }));
                else plan.Add(("browser.verify", new { condition = "text", target = new { by = "role", value = "status" }, text = mode == "accessible" ? "Notes saved: Inspection complete" : "Selected PUMP-1042" }));
            }
            else
            {
                if (exhausted) plan.Add((ToolCatalog.BrowserScreenshot, new { }));
                if (!input.Contains("Keep me signed in", StringComparison.Ordinal))
                {
                    if (mode == "accessible") plan.Add((ToolCatalog.BrowserClick, new { target = Button("Account menu") }));
                    else
                    {
                        plan.Add((ToolCatalog.BrowserFind, new { target = Button("Account") })); plan.Add((ToolCatalog.BrowserFind, new { target = Button("Profile") }));
                        if (visual) { plan.Add((ToolCatalog.BrowserScreenshot, new { })); plan.Add((ToolCatalog.BrowserVisionMouse, new { operation = "click", x = 1224, y = 40, snapshotId = capture is null ? "" : Snapshot() })); }
                    }
                    if (mode == "accessible")
                    {
                        plan.Add((ToolCatalog.BrowserClick, new { target = Button("Log out") }));
                        plan.Add(("browser.verify", new { condition = "text", target = new { by = "role", value = "status" }, text = "Signed out" }));
                    }
                }
                if (!input.Contains("don't close", StringComparison.Ordinal) && !(input.Contains("only close", StringComparison.Ordinal) && mode == "failure")) plan.Add((ToolCatalog.BrowserClose, new { }));
            }
            var verified = results.LastOrDefault(m => m.ToolCallId?.StartsWith("journey-verify", StringComparison.Ordinal) == true);
            if (verified is not null) { using var json = JsonDocument.Parse(verified.Text); Verified = json.RootElement.TryGetProperty("passed", out var passed) && passed.ValueKind == JsonValueKind.True; }
            foreach (var receipt in results)
            {
                using var data = JsonDocument.Parse(receipt.Text);
                if (receipt.ToolCallId?.StartsWith("journey-mouse", StringComparison.Ordinal) == true) MouseRefused = data.RootElement.TryGetProperty("error", out _);
                if (receipt.ToolCallId?.StartsWith("journey-close", StringComparison.Ordinal) == true && data.RootElement.TryGetProperty("browser", out var native)
                    && native.ValueKind == JsonValueKind.Object && native.TryGetProperty("status", out var status)) Closed = status.GetString() is "closed" or "already_closed";
            }
            if (Phase >= plan.Count)
            {
                yield return new ModelSemanticResponseReady(new(adaptive ? Verified ? "Application outcome independently observed." : "Visual context received; coordinate action unavailable and application change unverified."
                    : $"Logout: {(Verified ? "verified" : "unverified")}. Browser closure: {(Closed ? "confirmed" : "not requested or conditional")}.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            var action = plan[Phase]; var args = JsonSerializer.Serialize(action.Args);
            var effect = action.Tool == ToolCatalog.BrowserVisionMouse || !TenantApprovalRules.BrowserIntent(Guid.Parse("00000000-0000-0000-0000-000000000001"), action.Tool, args).ReadOnly;
            var available = effect ? request.Tools?.Any(t => t.Name == ToolCatalog.RequestAuthorization && t.ParametersJson.Contains(action.Tool, StringComparison.Ordinal)) == true
                : request.Tools?.Any(t => t.Name == action.Tool) == true;
            if (action.Tool == ToolCatalog.BrowserVisionMouse)
            {
                Assert.DoesNotContain(request.Tools ?? [], t => t.Name == action.Tool);
                Assert.DoesNotContain(action.Tool, Assert.Single(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson);
            }
            else if (!available)
            {
                Assert.InRange(load++, 0, 8); Assert.Contains(request.Tools ?? [], t => t.Name == ToolCatalog.CapabilitiesLoad);
                yield return new ModelToolCallEvent(new("journey-load-" + load, ToolCatalog.CapabilitiesLoad, JsonSerializer.Serialize(new { query = action.Tool, limit = 1 })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            var prefix = action.Tool == ToolCatalog.BrowserScreenshot ? "journey-capture" : action.Tool == "browser.verify" ? "journey-verify"
                : action.Tool == ToolCatalog.BrowserVisionMouse ? "journey-mouse" : action.Tool == ToolCatalog.BrowserClose ? "journey-close" : "journey-action";
            var actual = new ModelToolCall(prefix + Phase, action.Tool, args); Calls.Add(actual); Phase++;
            yield return new ModelToolCallEvent(effect ? actual with { Name = ToolCatalog.RequestAuthorization,
                ArgumentsJson = JsonSerializer.Serialize(new { operation = "run_browser_command", command = new { tool = action.Tool, arguments = action.Args } }) } : actual);
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
