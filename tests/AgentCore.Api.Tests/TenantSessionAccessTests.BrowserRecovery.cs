using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [TenantBrowserChromiumTheory]
    [InlineData(false, "dialog")] [InlineData(true, "dialog")]
    [InlineData(false, "close-invalid")] [InlineData(true, "close-invalid")]
    [InlineData(false, "close-null")] [InlineData(true, "close-null")]
    [InlineData(false, "dialog-denied")] [InlineData(true, "dialog-denied")]
    [InlineData(false, "dialog-repeat")] [InlineData(true, "dialog-repeat")]
    [InlineData(false, "discovery")] [InlineData(true, "discovery")]
    [InlineData(false, "repair")] [InlineData(true, "repair")]
    [InlineData(false, "repair-exhaust")] [InlineData(true, "repair-exhaust")]
    [InlineData(false, "timeout")] [InlineData(true, "timeout")]
    [InlineData(false, "final-tool")] [InlineData(true, "final-tool")]
    public async Task Tenant_native_Browser_recovery_preserves_current_authority_and_exact_effects(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var model = new OwnedBrowserRecoveryModel(mode);
            var dialog = mode.StartsWith("dialog", StringComparison.Ordinal);
            void Configure(IServiceCollection services)
            {
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = true; options.FixturePort = 0;
                options.ProfileRoot = Path.Combine(root, "browser"); options.PolicyMode = "Restricted"; options.ProfileMode = "EphemeralSession";
                options.NavigationOrigins = []; options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient();
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
            var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var saved = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
            var instance = saved.GetProperty("instance").GetProperty("instanceId").GetGuid();
            await GrantNativePrivateSpaceRead(owner, tenant, instance); var executor = saved.GetProperty("principalId").GetGuid();
            var instanceRevision = 1L;
            if (mode is "dialog-denied" or "discovery")
            {
                var settings = await Json(await owner.GetAsync(api + $"/agents/{instance}/settings"));
                var capabilities = settings.GetProperty("items").EnumerateArray().Single(s => s.GetProperty("section").GetString() == "capabilities").GetProperty("effective");
                var selected = capabilities.GetProperty("selected").EnumerateArray().Select(v => v.GetString()!).Where(t => mode != "dialog-denied" || t != ToolCatalog.BrowserDialog).ToArray();
                var always = capabilities.GetProperty("always").EnumerateArray().Select(v => v.GetString()!).Where(t => selected.Contains(t) && (mode != "discovery" || t != ToolCatalog.BrowserFind)).ToArray();
                Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{instance}/settings/capabilities", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision++, set = new { selected, always }, clear = Array.Empty<string>() })).StatusCode);
            }
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Native recovery authority", ["agents.read", "sessions.read", "browser.read", "browser.use"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>());
            model.Url = native.Fixture.Origin + (dialog ? "/credential-login" : "/");
            model.VerifyPage = async () =>
            {
                var page = native.ContextFor(session)!.Pages.Single();
                Assert.True(await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Sign in", Exact = true }).IsVisibleAsync());
            };
            Microsoft.Playwright.IDialog? fixtureDialog = null;
            var nativeSignOutTriggers = 0;
            model.PrepareSignOut = async () =>
            {
                var page = native.ContextFor(session)!.Pages.Single();
                page.Dialog += (_, raised) => fixtureDialog = raised;
                page.Console += (_, message) => { if (message.Text == "FIXTURE-SIGNOUT-TRIGGER") Interlocked.Increment(ref nativeSignOutTriggers); };
                await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Sign out", Exact = true })
                    .EvaluateAsync("button => { sessionStorage.setItem('triggerCount', '0'); button.addEventListener('click', () => sessionStorage.setItem('triggerCount', String(Number(sessionStorage.getItem('triggerCount')) + 1)), { capture: true }); button.addEventListener('click', () => console.debug('FIXTURE-SIGNOUT-TRIGGER'), { capture: true }); }");
            };
            model.VerifyTriggerCount = async () => Assert.Equal(1, await native.ContextFor(session)!.Pages.Single().EvaluateAsync<int>("() => Number(sessionStorage.getItem('triggerCount'))"));
            var source = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = dialog ? "Open the fixture, sign in, then sign out with its confirmation. Verify sign-out and close browser." : "Open the fixture and close browser; report the native result.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
            var seen = new HashSet<Guid>(); AgentRun final;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            while (true)
            {
                await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync(deadline.Token);
                await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
                final = (await CurrentApprovalExecution(runs, tenant, source))!;
                if (final.Status == AgentRunStatus.WaitingForApproval && seen.Add(final.ApprovalRequestId!.Value))
                {
                    var approval = (await approvals.FindApprovalAsync(tenant, final.ApprovalRequestId.Value, deadline.Token))!;
                    Assert.Equal(ApprovalKind.Review, approval.Kind); Assert.NotNull(approval.Operation.BrowserCommand); Assert.Equal(human, approval.OriginalInitiatorPrincipalId);
                    var view = await Json(await owner.GetAsync(api + $"/approvals/{approval.RequestId}", deadline.Token));
                    var decided = await owner.PostAsJsonAsync(api + $"/approvals/{approval.RequestId}/decision", new { decision = "approve", actionHash = approval.Operation.ActionHash, expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = approval.Revision }, deadline.Token);
                    Assert.True(decided.IsSuccessStatusCode, await decided.Content.ReadAsStringAsync(deadline.Token));
                }
                if (final.IsTerminal && final.Result?.OutcomeKind != AgentRunOutcomeKind.Continued) break;
                await Task.Delay(25, deadline.Token);
            }
            var trace = string.Join(" | ", model.Requests.Last().Messages.Where(m => m.Role == ModelRole.Tool).TakeLast(4).Select(m => m.Name + ":" + m.Text[..Math.Min(600, m.Text.Length)]));
            Assert.True(final.Status == (mode is "repair-exhaust" or "final-tool" ? AgentRunStatus.Failed : AgentRunStatus.Completed), $"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}; phase={model.Phase}; {trace}");
            Assert.True((mode != "dialog-repeat") == RunBudgetDiagnosticProjection.From(final)!.ClosureConfirmed, $"Closure diagnostic={JsonSerializer.Serialize(RunBudgetDiagnosticProjection.From(final))}; {trace}");
            Assert.Equal(mode == "dialog-repeat" ? 1 : 0, (await native.ObserveTenantSessionAsync(new(tenant, instance, executor, session))).LiveContexts);
            Assert.True(AgentRunToolCallCheckpoint.TryRead(final.Checkpoint, out var messages));
            var browserReceipts = messages!.Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization).ToArray();
            var effects = mode switch { "dialog" => 5, "dialog-denied" => 4, "dialog-repeat" => 3, "discovery" => 3, _ => 2 };
            Assert.Equal(effects, seen.Count); Assert.Equal(effects, browserReceipts.Length);
            if (mode != "dialog-repeat")
            {
                var close = Assert.Single(browserReceipts, m => m.Text.Contains("\"status\":\"closed\"", StringComparison.Ordinal));
                using var doc = JsonDocument.Parse(close.Text);
                Assert.False(doc.RootElement.GetProperty("browser").TryGetProperty("error", out _));
            }
            if (dialog)
            {
                Assert.Equal(1, nativeSignOutTriggers);
                if (mode == "dialog-repeat")
                {
                    // The Run is terminal. Dismiss the fixture's still-pending modal only to inspect its DOM counter.
                    Assert.NotNull(fixtureDialog); await fixtureDialog.DismissAsync(); await model.VerifyTriggerCount();
                }
                var modal = Assert.Single(browserReceipts, m => m.Text.Contains("\"error\":\"dialog_pending\"", StringComparison.Ordinal));
                using var doc = JsonDocument.Parse(modal.Text); var result = doc.RootElement;
                Assert.False(result.GetProperty("replayAllowed").GetBoolean()); Assert.True(result.GetProperty("commandCompleted").GetBoolean());
                var observed = result.GetProperty("browser"); Assert.True(observed.GetProperty("effectAttempted").GetBoolean());
                Assert.False(observed.GetProperty("effectConfirmedBySdk").GetBoolean()); Assert.False(observed.GetProperty("applicationOutcomeVerified").GetBoolean());
                Assert.Equal(mode != "dialog-denied", final.LoadedCapabilityIds.Contains(ToolCatalog.BrowserDialog));
                Assert.Equal(0, final.CapabilityLoadCount);
                if (mode == "dialog-repeat") Assert.Equal("browserBlocked", AgentRunToolCallCheckpoint.ReadExecutionState(final.Checkpoint).FinalizationReason);
            }
            if (mode == "discovery") { Assert.Empty(final.LoadedCapabilityIds); Assert.Equal(1, final.CapabilityLoadCount); }
            if (mode is "repair" or "repair-exhaust" or "timeout" or "final-tool")
            {
                Assert.Equal(4, model.Requests.Count);
                Assert.All(model.Requests.Last().Tools ?? [], tool => Assert.Equal(AssistantResponseSchema.ResponseFunctionName, tool.Name));
                if (mode.StartsWith("repair", StringComparison.Ordinal))
                {
                    Assert.Empty(model.Requests.Last().Tools ?? []);
                    Assert.Null(model.Requests.Last().ResponseContract);
                    Assert.Equal(ProviderFailureReason.MissingDisplayText, AgentRunToolCallCheckpoint.ReadProtocolRepairReason(final.Checkpoint));
                }
                else
                {
                    Assert.Null(model.Requests.Last().ResponseContract);
                    Assert.Equal(AssistantResponseSchema.ResponseFunctionName, Assert.Single(model.Requests.Last().Tools!).Name);
                }
                Assert.Equal(mode is "repair" or "repair-exhaust" ? "protocolRepair" : "providerTimeout", AgentRunToolCallCheckpoint.ReadExecutionState(final.Checkpoint).FinalizationReason ?? "protocolRepair");
            }
            if (mode == "close-invalid")
            {
                Assert.Contains("invalid_tool_strategy_blocked", final.Checkpoint!.PayloadJson);
                Assert.Contains(messages!, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.BrowserSnapshot && !m.Text.Contains("\"error\"", StringComparison.Ordinal));
            }
            if (mode == "close-null") Assert.Contains(messages!, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.BrowserClose && m.Text.Contains("browser.close accepts only", StringComparison.Ordinal));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class OwnedBrowserRecoveryModel(string mode) : ILanguageModel
    {
        public string Url = "";
        public Func<Task>? VerifyPage;
        public Func<Task>? PrepareSignOut;
        public Func<Task>? VerifyTriggerCount;
        public int Phase;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public ModelCapabilities Capabilities => new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request); var step = Phase++;
            var last = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool);
            var dialog = mode.StartsWith("dialog", StringComparison.Ordinal);
            object Button(string name) => new { by = "role", value = "button", name, exact = true };
            var terminal = mode is "repair" or "repair-exhaust" or "timeout" or "final-tool";
            if (terminal && step >= 2)
            {
                if (step == 2 || mode == "repair-exhaust")
                {
                    yield return new ModelFailed(mode.StartsWith("repair", StringComparison.Ordinal)
                        ? new(ProviderErrorCode.InvalidResponse, "Controlled presentation failure after exact native close.", FailureReason: ProviderFailureReason.MissingDisplayText, ResponseChannel: ProviderResponseChannel.StructuredOutput)
                        : new(ProviderErrorCode.Timeout, "Controlled total provider timeout after exact native close.", FailureReason: ProviderFailureReason.TotalTimeout));
                    yield break;
                }
                if (mode == "final-tool")
                {
                    yield return new ModelToolCallEvent(new("prohibited-final-close", ToolCatalog.BrowserClose, "{}"));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
                }
                if (mode == "repair") yield return new ModelTextDelta("Native browser closure remains recorded; application sign-out was not verified.");
                else yield return new ModelSemanticResponseReady(new("Native browser closure remains recorded; application sign-out was not verified.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            if (dialog && step >= 3 && step < 5 && (step == 3 || mode == "dialog-repeat"))
            {
                Assert.Contains("dialog_pending", last!.Text);
                Assert.DoesNotContain(request.Tools ?? [], t => t.Name == ToolCatalog.BrowserFind);
                var authorization = Assert.Single(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization);
                Assert.Equal(mode != "dialog-denied", authorization.ParametersJson.Contains(ToolCatalog.BrowserDialog, StringComparison.Ordinal));
                Assert.DoesNotContain(ToolCatalog.BrowserClick, authorization.ParametersJson);
            }
            if (dialog && step == 2 && PrepareSignOut is not null) await PrepareSignOut();
            if (mode == "dialog" && step == 6)
            {
                Assert.Contains("Sign in", last!.Text); if (VerifyPage is not null) await VerifyPage();
            }
            if (mode == "discovery" && step == 1) Assert.Contains(request.Tools ?? [], t => t.Name == ToolCatalog.BrowserFind);
            if (mode == "discovery" && step == 2)
            {
                Assert.Contains("load_already_projected", last!.Text);
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.BrowserFind);
                Assert.Contains(ToolCatalog.BrowserFillForm, Assert.Single(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson);
            }
            if (mode == "discovery" && step == 5) Assert.Contains("Discovery verified", last!.Text);
            (string Tool, object Args) action = terminal ? step switch
            {
                0 => (ToolCatalog.BrowserNavigate, new { url = Url }),
                _ => (ToolCatalog.BrowserClose, new { })
            } : dialog ? step switch
            {
                0 => (ToolCatalog.BrowserNavigate, new { url = Url }),
                1 => (ToolCatalog.BrowserClick, new { target = Button("Sign in") }),
                2 => (ToolCatalog.BrowserClick, new { target = Button("Sign out") }),
                3 when mode == "dialog-denied" => (ToolCatalog.BrowserClose, new { }),
                >= 3 and <= 4 when mode == "dialog-repeat" => (ToolCatalog.BrowserFind, new { target = Button("Irrelevant query " + step) }),
                >= 4 when mode == "dialog-denied" => ("", new { }),
                >= 5 when mode == "dialog-repeat" => ("", new { }),
                3 => (ToolCatalog.BrowserDialog, new { operation = "inspect" }),
                4 => (ToolCatalog.BrowserDialog, new { operation = "accept" }),
                5 => (ToolCatalog.BrowserFind, new { target = Button("Sign in") }),
                6 => (ToolCatalog.BrowserClose, new { }),
                _ => ("", new { })
            } : mode == "discovery" ? step switch
            {
                0 => (ToolCatalog.BrowserNavigate, new { url = Url }),
                1 => (ToolCatalog.CapabilitiesLoad, new { query = ToolCatalog.BrowserFind, limit = 1 }),
                2 => (ToolCatalog.BrowserFind, new { target = new { by = "label", value = "Record" } }),
                3 => (ToolCatalog.BrowserFillForm, new { fields = new[] { new { target = new { by = "label", value = "Record" }, value = "Discovery verified" } } }),
                4 => (ToolCatalog.BrowserSnapshot, new { }),
                5 => (ToolCatalog.BrowserClose, new { }),
                _ => ("", new { })
            } : mode == "close-null" ? step switch
            {
                0 => (ToolCatalog.BrowserNavigate, new { url = Url }),
                1 => (ToolCatalog.BrowserClose, new { }),
                2 => (ToolCatalog.BrowserClose, new { }),
                _ => ("", new { })
            } : step switch
            {
                0 => (ToolCatalog.BrowserNavigate, new { url = Url }),
                >= 1 and <= 4 => (ToolCatalog.BrowserClose, new { request = new { } }),
                5 => (ToolCatalog.BrowserSnapshot, new { }),
                6 => (ToolCatalog.BrowserClose, new { operation = "close" }),
                _ => ("", new { })
            };
            if (mode == "dialog" && action.Tool == ToolCatalog.BrowserClose && VerifyTriggerCount is not null) await VerifyTriggerCount();
            if (action.Tool.Length == 0)
            {
                yield return new ModelSemanticResponseReady(new("Native browser closure recorded; only observed outcomes are verified.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            var malformed = mode == "close-invalid" && step is >= 1 and <= 4 || mode == "close-null" && step == 1;
            var args = mode == "close-null" && step == 1 ? "null" : JsonSerializer.Serialize(action.Args);
            var approved = !malformed && ToolCatalog.IsBrowserTool(action.Tool) && !TenantApprovalRules.BrowserIntent(Guid.Parse("00000000-0000-0000-0000-000000000001"), action.Tool, args).ReadOnly;
            var tool = approved ? ToolCatalog.RequestAuthorization : action.Tool;
            if (!malformed && !(mode == "dialog-repeat" && step >= 3)) Assert.Contains(request.Tools ?? [], t => t.Name == tool);
            if (approved) args = JsonSerializer.Serialize(new { operation = "run_browser_command", command = new { tool = action.Tool, arguments = action.Args } });
            yield return new ModelToolCallEvent(new("native-recovery-" + step, tool, args));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
