using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.SignalR.Client;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [TenantBrowserChromiumTheory]
    [InlineData(false, "unavailable")] [InlineData(true, "unavailable")]
    [InlineData(false, "user-stop")] [InlineData(true, "user-stop")]
    [InlineData(false, "work-cutoff")] [InlineData(true, "work-cutoff")]
    [InlineData(false, "reply-reserve")] [InlineData(true, "reply-reserve")]
    [InlineData(false, "deadline")] [InlineData(true, "deadline")]
    [InlineData(false, "dialog-cleanup")] [InlineData(true, "dialog-cleanup")]
    [InlineData(false, "dialog-denied-cleanup")] [InlineData(true, "dialog-denied-cleanup")]
    [InlineData(false, "dialog-wait")] [InlineData(true, "dialog-wait")]
    public async Task Tenant_native_Browser_boundaries_preserve_recorded_effects_without_replay_or_untyped_receipt_leakage(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-boundary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            var model = new OwnedBrowserBoundaryModel(mode);
            void Configure(IServiceCollection services)
            {
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = true; options.FixturePort = 0;
                options.ProfileRoot = Path.Combine(root, "browser"); options.PolicyMode = "Restricted"; options.ProfileMode = "EphemeralSession";
                options.NavigationOrigins = []; options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            if (mode == "unavailable") factory.UseKestrel(0);
            using var ready = mode == "unavailable" ? factory.CreateClient() : null;
            var cookies = new CookieContainer();
            using var owner = mode == "unavailable" ? new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready!.BaseAddress } : factory.CreateClient();
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid(); var instanceRevision = 1L;
            if (mode == "dialog-denied-cleanup")
            {
                var settings = await Json(await owner.GetAsync(api + $"/agents/{instance}/settings"));
                var capabilities = settings.GetProperty("items").EnumerateArray().Single(s => s.GetProperty("section").GetString() == "capabilities").GetProperty("effective");
                var selected = capabilities.GetProperty("selected").EnumerateArray().Select(v => v.GetString()!).Where(t => t != ToolCatalog.BrowserDialog).ToArray();
                Assert.Equal(HttpStatusCode.OK, (await owner.PatchAsJsonAsync(api + $"/agents/{instance}/settings/capabilities", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision++, set = new { selected }, clear = Array.Empty<string>() })).StatusCode);
            }
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/execution-budgets", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision++, budgets = new ExecutionBudgetPolicy(InteractiveBrowser: new(48, 180)) })).StatusCode);
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Native boundary execution", ["agents.read", "sessions.read", "browser.read", "browser.use"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>()); var dialog = mode.StartsWith("dialog-", StringComparison.Ordinal);
            model.Url = native.Fixture.Origin + (dialog ? "/credential-login" : "/");
            var clicks = 0; Microsoft.Playwright.IDialog? fixtureDialog = null;
            model.PrepareSignOut = async () =>
            {
                var page = native.ContextFor(session)!.Pages.Single();
                page.Dialog += (_, raised) => fixtureDialog = raised;
                page.Console += (_, message) => { if (message.Text == "BOUNDARY-SIGNOUT-TRIGGER") Interlocked.Increment(ref clicks); };
                await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Sign out", Exact = true })
                    .EvaluateAsync("button => button.addEventListener('click', () => console.debug('BOUNDARY-SIGNOUT-TRIGGER'), { capture: true })");
            };
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
            async Task<Guid> Submit(string text, long revision) => (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = revision }))).GetProperty("runId").GetGuid();
            var events = new ConcurrentQueue<ServerEvent>();
            await using var hub = mode == "unavailable" ? TenantSocket(owner.BaseAddress!, cookies, events) : null;
            if (hub is not null) { await hub.StartAsync(); Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted); }
            var source = await Submit(dialog ? "Sign in, then sign out if authorized, verify sign-out and close browser. Report any blocked progress truthfully." : "Open the fixture and close browser, then report verified actions.", 1);
            var seen = new HashSet<Guid>(); var retryIdentities = new HashSet<(Guid Run, Guid Response)>(); AgentRun? suspended = null; AgentRun final;
            var boundaryReleased = false; var waitReleased = false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            async Task<AgentRun> Pump(Guid id)
            {
                while (true)
                {
                    await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync(timeout.Token);
                    await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, timeout.Token);
                    var current = (await CurrentApprovalExecution(runs, tenant, id))!;
                    if (current.Status == AgentRunStatus.WaitingForApproval && seen.Add(current.ApprovalRequestId!.Value))
                    {
                        var request = (await approvals.FindApprovalAsync(tenant, current.ApprovalRequestId.Value, timeout.Token))!;
                        Assert.NotNull(request.Operation.BrowserCommand); Assert.Equal(human, request.OriginalInitiatorPrincipalId);
                        var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}", timeout.Token));
                        // Preserve actual admission chronology rather than depending on GUID order at one frozen timestamp.
                        clock.Advance(TimeSpan.FromMilliseconds(1));
                        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision }, timeout.Token)).StatusCode);
                    }
                    if (!boundaryReleased && model.Entered.Task.IsCompleted)
                    {
                        boundaryReleased = true;
                        if (mode == "user-stop") Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agent-runs/{current.AgentRunId}/cancel", new { expectedRevision = await Revision(owner, tenant), expectedRunRevision = current.Revision }, timeout.Token)).StatusCode);
                        else clock.Advance(TimeSpan.FromSeconds(mode == "deadline" ? 181 : mode == "reply-reserve" ? 155 : 95));
                        model.Release.TrySetResult();
                    }
                    if (mode == "dialog-wait" && !waitReleased && current.Status == AgentRunStatus.WaitingForSignal)
                    {
                        suspended = current; waitReleased = true;
                        await ((AgentCore.Api.Realtime.TenantSessionRuntimeHost)factory.Services.GetRequiredService<ITenantRunDispatcher>())
                            .WaitForGenerationReleaseAsync(session, current.AgentRunId, current.Wait!.SuspendedGeneration!.Value, timeout.Token);
                        clock.Advance(TimeSpan.FromSeconds(2));
                    }
                    if (current.Status == AgentRunStatus.WaitingToRetry && current.NextRetryAtUtc is { } due)
                    {
                        Assert.NotNull(current.ResponseId); retryIdentities.Add((current.AgentRunId, current.ResponseId.Value));
                        if (due > clock.GetUtcNow()) clock.Advance(due - clock.GetUtcNow() + TimeSpan.FromMilliseconds(1));
                    }
                    if (current.IsTerminal && current.Result?.OutcomeKind != AgentRunOutcomeKind.Continued) return current;
                    await Task.Delay(25, timeout.Token);
                }
            }
            final = await Pump(source);
            string Trace() => string.Join(" | ", model.Requests.Last().Messages.Where(m => m.Role == ModelRole.Tool).TakeLast(4).Select(m => m.Name + ":" + m.Text[..Math.Min(600, m.Text.Length)]));
            var expected = mode is "unavailable" or "deadline" ? AgentRunStatus.Failed : mode == "user-stop" ? AgentRunStatus.Cancelled : AgentRunStatus.Completed;
            Assert.True(final.Status == expected, $"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}; phase={model.Phase}; {Trace()}");
            Assert.True(AgentRunToolCallCheckpoint.TryRead(final.Checkpoint, out var messages));
            var browserReceipts = messages!.Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization).ToArray();
            var expectedEffects = mode == "dialog-cleanup" ? 5 : mode == "dialog-denied-cleanup" ? 4 : mode == "dialog-wait" ? 3 : 2;
            Assert.Equal(expectedEffects, seen.Count); Assert.Equal(expectedEffects, browserReceipts.Length);
            Assert.Equal(mode != "dialog-wait", RunBudgetDiagnosticProjection.From(final)!.ClosureConfirmed);
            Assert.Equal(mode == "dialog-wait" ? 1 : 0, (await native.ObserveTenantSessionAsync(new(tenant, instance, executor, session))).LiveContexts);
            if (dialog)
            {
                Assert.Equal(1, clicks); Assert.Equal(mode != "dialog-denied-cleanup", final.LoadedCapabilityIds.Contains(ToolCatalog.BrowserDialog)); Assert.Equal(0, final.CapabilityLoadCount);
                if (mode == "dialog-wait")
                {
                    Assert.NotNull(suspended); Assert.Equal(suspended.AgentRunId, final.AgentRunId); Assert.Equal(suspended.ResponseId, final.ResponseId); Assert.Equal(suspended.AttemptCount, final.AttemptCount);
                    Assert.Equal(1, final.WaitCount); Assert.Equal("browserBlocked", AgentRunToolCallCheckpoint.ReadExecutionState(final.Checkpoint).FinalizationReason);
                    Assert.NotNull(fixtureDialog); await fixtureDialog.DismissAsync();
                }
                else Assert.NotNull(AgentRunToolCallCheckpoint.ReadExecutionState(final.Checkpoint).CleanupStartedAtMessage);
            }
            if (mode == "deadline")
            {
                Assert.Equal("run-deadline", final.Failure!.Code); Assert.Null(AgentRunToolCallCheckpoint.ReadProtocolRepairReason(final.Checkpoint)); Assert.Equal(3, model.Requests.Count);
                var previous = (await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(tenant, session, null, null, 100))!;
                var next = await Submit("Why did that reply fail? Report only verified prior execution facts.", previous.SessionRevision);
                Assert.Equal(AgentRunStatus.Completed, (await Pump(next)).Status);
                var facts = Assert.Single(model.Requests.Last().Messages, m => m.Role == ModelRole.Developer && m.Text.StartsWith("Trusted Core execution facts (previous", StringComparison.Ordinal));
                Assert.Contains("run-deadline", facts.Text); Assert.Contains(ToolCatalog.BrowserClose, facts.Text); Assert.DoesNotContain("sign-out confirmed", facts.Text);
                Assert.Equal(2, seen.Count);
            }
            if (mode is "work-cutoff" or "reply-reserve")
            {
                Assert.Equal(4, model.Requests.Count);
                var state = AgentRunToolCallCheckpoint.ReadExecutionState(final.Checkpoint);
                if (mode == "work-cutoff") Assert.NotNull(state.CleanupStartedAtMessage);
                else Assert.Equal("runDeadline", state.FinalizationReason);
            }
            if (mode == "user-stop") { Assert.Equal(3, model.Requests.Count); Assert.Equal(1, final.AttemptCount); }
            if (mode == "unavailable")
            {
                Assert.Equal(3, final.AttemptCount); Assert.Equal(5, model.Requests.Count);
                var firstWire = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == final.ResponseId!.Value.ToString("D") && e.Payload["status"]?.ToString() == "failed"));
                Assert.True(!firstWire.Payload.TryGetValue("effectReceipts", out var firstEffects) || firstEffects is null);
                Assert.Equal(3, final.AttemptCount); Assert.Equal(5, model.Requests.Count); Assert.NotNull(final.ResponseId); Assert.Equal((final.AgentRunId, final.ResponseId.Value), Assert.Single(retryIdentities));
                var before = (await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(tenant, session, null, null, 100))!;
                var secondId = await Submit("Explain why the reply failed using only current permitted context.", before.SessionRevision);
                var second = await Pump(secondId); Assert.Equal(AgentRunStatus.Failed, second.Status); Assert.NotEqual(final.ResponseId, second.ResponseId); Assert.Equal(3, second.AttemptCount);
                var nextWire = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == second.ResponseId!.Value.ToString("D") && e.Payload["status"]?.ToString() == "failed"));
                Assert.True(!nextWire.Payload.TryGetValue("effectReceipts", out var nextEffects) || nextEffects is null);
                Assert.Equal(2, seen.Count); Assert.All(model.Requests.ToArray().TakeLast(3), r => Assert.DoesNotContain(r.Messages, m => m.Role == ModelRole.Tool));
                Assert.DoesNotContain(model.Requests.SelectMany(r => r.Messages), m => m.Text.Contains("Browser closed", StringComparison.Ordinal));
            }
            var history = (await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items").EnumerateArray().ToArray();
            Assert.All(history, h => Assert.True(!h.TryGetProperty("effectReceipts", out var receipts) || receipts.ValueKind == JsonValueKind.Null || receipts.GetArrayLength() == 0));
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class OwnedBrowserBoundaryModel(string mode) : ILanguageModel
    {
        public string Url = ""; public int Phase; private bool injected;
        public Func<Task>? PrepareSignOut;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModelCapabilities Capabilities => new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request); var step = Phase++; var dialog = mode.StartsWith("dialog-", StringComparison.Ordinal);
            if (mode == "unavailable" && step >= 2) { yield return new ModelFailed(new(ProviderErrorCode.Unavailable, "Controlled post-close transport failure.", FailureReason: ProviderFailureReason.Http5xx)); yield break; }
            var boundary = dialog ? mode != "dialog-wait" && step == 3 : step == 2;
            if (boundary && !injected)
            {
                injected = true; if (dialog) Phase--;
                Entered.TrySetResult(); await Release.Task;
                yield return new ModelFailed(mode == "deadline" ? new(ProviderErrorCode.InvalidResponse, "Controlled invalid format after hard deadline.", FailureReason: ProviderFailureReason.MissingDisplayText)
                    : new(ProviderErrorCode.Cancelled, "Controlled provider cancellation at the execution boundary."));
                yield break;
            }
            if (!dialog && step >= 3)
            {
                if (mode == "work-cutoff")
                {
                    Assert.Contains(request.Context!.Sections, s => s.Id == ContextSectionId.CoreCleanup);
                    var approval = Assert.Single(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization);
                    Assert.Contains(ToolCatalog.BrowserClose, approval.ParametersJson); Assert.DoesNotContain(ToolCatalog.BrowserType, approval.ParametersJson);
                }
                else if (mode != "deadline") Assert.All(request.Tools ?? [], t => Assert.Equal(AssistantResponseSchema.ResponseFunctionName, t.Name));
                yield return new ModelSemanticResponseReady(new("The recorded browser closure remains separate from unverified sign-out.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            object Button(string name) => new { by = "role", value = "button", name, exact = true };
            if (dialog && step == 2 && PrepareSignOut is not null) await PrepareSignOut();
            if (dialog && step >= 3 && (mode == "dialog-wait" || mode == "dialog-cleanup" && step < 5 || mode == "dialog-denied-cleanup" && step == 3))
            {
                Assert.DoesNotContain(request.Tools ?? [], t => t.Name == ToolCatalog.BrowserFind);
                if (mode != "dialog-wait") Assert.Contains(request.Context!.Sections, s => s.Id == ContextSectionId.CoreCleanup);
            }
            (string Tool, object Args) action = !dialog ? step switch
            { 0 => (ToolCatalog.BrowserNavigate, new { url = Url }), _ => (ToolCatalog.BrowserClose, new { }) }
                : step switch
                {
                    0 => (ToolCatalog.BrowserNavigate, new { url = Url }),
                    1 => (ToolCatalog.BrowserClick, new { target = Button("Sign in") }),
                    2 => (ToolCatalog.BrowserClick, new { target = Button("Sign out") }),
                    3 when mode == "dialog-wait" => (ToolCatalog.ExecutionWait, new { mode = "duration", seconds = 2 }),
                    4 or 5 when mode == "dialog-wait" => (ToolCatalog.BrowserFind, new { target = Button("Ineffective repeated search " + step) }),
                    >= 6 when mode == "dialog-wait" => ("", new { }),
                    3 when mode == "dialog-denied-cleanup" => (ToolCatalog.BrowserClose, new { }),
                    >= 4 when mode == "dialog-denied-cleanup" => ("", new { }),
                    3 => (ToolCatalog.BrowserDialog, new { operation = "inspect" }),
                    4 => (ToolCatalog.BrowserDialog, new { operation = "accept" }),
                    5 => (ToolCatalog.BrowserFind, new { target = Button("Sign in") }),
                    6 => (ToolCatalog.BrowserClose, new { }),
                    _ => ("", new { })
                };
            if (action.Tool.Length == 0)
            {
                yield return new ModelSemanticResponseReady(new("Verified progress is retained; unresolved sign-out remains uncertain.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            var approved = ToolCatalog.IsBrowserTool(action.Tool) && !TenantApprovalRules.BrowserIntent(Guid.Parse("00000000-0000-0000-0000-000000000001"), action.Tool, JsonSerializer.Serialize(action.Args)).ReadOnly;
            var tool = approved ? ToolCatalog.RequestAuthorization : action.Tool;
            if (!(mode == "dialog-wait" && step is 4 or 5)) Assert.Contains(request.Tools ?? [], t => t.Name == tool);
            yield return new ModelToolCallEvent(new("boundary-" + step, tool, approved ? JsonSerializer.Serialize(new { operation = "run_browser_command", command = new { tool = action.Tool, arguments = action.Args } }) : JsonSerializer.Serialize(action.Args)));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
