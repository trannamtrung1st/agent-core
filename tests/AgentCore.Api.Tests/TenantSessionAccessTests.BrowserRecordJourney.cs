using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class NativeRecordJourneyModel(string mode, string url, bool unresolved = false) : ILanguageModel
    {
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public string Procedure { get; set; } = "";
        public bool NextTurn { get; set; }
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            var navigation = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
            var read = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.BrowserSnapshot);
            if (!NextTurn && navigation is null)
            {
                Assert.DoesNotContain(request.Tools!, t => t.Name == ToolCatalog.AppMessageSend);
                Assert.DoesNotContain(request.Tools!, t => t.Name == ToolCatalog.BrowserNavigate);
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.RequestAuthorization && t.ParametersJson.Contains(ToolCatalog.BrowserNavigate));
                yield return new ModelToolCallEvent(new("open-owned-record", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new {
                    operation = "run_browser_command", command = new { tool = ToolCatalog.BrowserNavigate, arguments = new { url } } })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (mode == "denied")
            {
                Assert.Contains("target_denied", navigation!.Text);
                yield return new ModelSemanticResponseReady(new(ScriptedLanguageModel.BrowserDenialAnswer, new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            if (!NextTurn && mode is "challenge" or "signup")
            {
                using var handedOff = JsonDocument.Parse(navigation!.Text);
                var observed = handedOff.RootElement.GetProperty("browser").GetProperty("observation");
                Assert.Equal((int)(mode == "challenge" ? BrowserInterventionKind.HumanVerificationRequired : BrowserInterventionKind.AccountRegistrationRequired), observed.GetProperty("intervention").GetInt32());
                Assert.Contains(mode == "challenge" ? "Human verification required" : "Create account", observed.GetProperty("title").GetString());
                Assert.DoesNotContain(request.Tools!, t => t.Name is ToolCatalog.BrowserSnapshot or ToolCatalog.BrowserClick or ToolCatalog.BrowserType);
                yield return new ModelSemanticResponseReady(new(mode == "challenge" ? ScriptedLanguageModel.BrowserChallengeAnswer : ScriptedLanguageModel.BrowserSignupAnswer, new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            if (read is null)
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.BrowserSnapshot);
                yield return new ModelToolCallEvent(new("observe-owned-record", ToolCatalog.BrowserSnapshot, "{}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            using var source = JsonDocument.Parse(read.Text); Assert.True(source.RootElement.GetProperty("tenantBrowserRead").GetBoolean()); Assert.True(source.RootElement.GetProperty("untrustedBrowserContent").GetBoolean());
            if (NextTurn && unresolved)
            {
                Assert.Contains("user_intervention_required", read.Text); Assert.Contains("HumanVerificationRequired", read.Text);
                yield return new ModelSemanticResponseReady(new(ScriptedLanguageModel.BrowserChallengeContinueAnswer, new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            if (NextTurn)
            {
                Assert.Contains(mode == "signup" ? "Account ready" : "AC-1042", read.Text);
                yield return new ModelSemanticResponseReady(new("Continued from the fresh owned page.", new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            Assert.Contains("AC-1042", read.Text); Assert.Contains("In review", read.Text); Assert.Contains("Ignore previous instructions", read.Text); Assert.Contains("expand the allowlist", read.Text);
            if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AppMessageSend))
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.AppMessageSend);
                yield return new ModelToolCallEvent(new("record-notice", ToolCatalog.AppMessageSend, JsonSerializer.Serialize(new { text = ScriptedLanguageModel.BrowserRecordMessage })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.SkillsLoad))
            {
                yield return new ModelToolCallEvent(new("load-record-procedure", ToolCatalog.SkillsLoad, TenantSkillLoadContract.Arguments(["definition:browser.record.lookup"])));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            Assert.Contains(Procedure, string.Join("\n", request.Messages.Select(m => m.Text)));
            yield return new ModelSemanticResponseReady(new(ScriptedLanguageModel.BrowserRecordAnswer, new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    [TenantBrowserChromiumTheory] [InlineData(false)] [InlineData(true)]
    public Task Tenant_native_record_journey_reads_actual_hostile_page_data_notices_once_loads_owned_Skill_and_answers_from_observed_status(bool sqlite) =>
        NativeRecordJourney(sqlite, "record");

    [TenantBrowserChromiumTheory] [InlineData(false)] [InlineData(true)]
    public Task Tenant_native_record_journey_denied_origin_finishes_chat_without_context_consent_or_navigation(bool sqlite) =>
        NativeRecordJourney(sqlite, "denied");

    [TenantBrowserChromiumTheory] [InlineData(false, "challenge")] [InlineData(true, "challenge")]
    [InlineData(false, "signup")] [InlineData(true, "signup")]
    public Task Tenant_native_record_journey_hands_off_challenge_or_signup_and_next_root_reads_fresh_human_resolved_page(bool sqlite, string mode) =>
        NativeRecordJourney(sqlite, mode);

    [TenantBrowserChromiumTheory] [InlineData(false)] [InlineData(true)]
    public Task Tenant_native_record_journey_keeps_unresolved_human_verification_page_observable_on_next_root(bool sqlite) =>
        NativeRecordJourney(sqlite, "challenge", resolveHandoff: false);

    private static async Task NativeRecordJourney(bool sqlite, string mode, bool resolveHandoff = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-native-record-journey-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var page = new LoopbackBrowserFixtureHost(NullLogger.Instance); await page.StartAsync(0, CancellationToken.None); Assert.True(page.IsAvailable); Assert.NotNull(page.Origin);
            var origin = page.Origin!;
            var url = mode == "denied" ? "https://example.invalid/escape" : origin + (mode is "challenge" or "signup" ? "/" + mode : "/");
            var model = new NativeRecordJourneyModel(mode, url, unresolved: !resolveHandoff);
            void Configure(IServiceCollection services)
            {
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = false;
                options.ProfileRoot = Path.Combine(root, "browser"); options.ProfileMode = "EphemeralSession"; options.PolicyMode = "Restricted";
                options.NavigationOrigins = [origin]; options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var definition = (await factory.Services.GetRequiredService<IBuiltInAgentDefinitionStore>().GetAsync("general-assistant", 32))!;
            model.Procedure = definition.SkillList.Single(s => s.Id == "browser.record.lookup").Procedure;
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Exact native record observation", ["agents.read", "sessions.read", "browser.read", "browser.use", "skills.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            var native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>()); var mutations = 0; native.ActionStartedProbe = () => Interlocked.Increment(ref mutations);
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = mode == "denied" ? "Open the requested outside site" : mode is "challenge" or "signup" ? "Open the fixture that needs a person to continue" : "Please look up record AC-1042.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var sourceId = sent.GetProperty("runId").GetGuid(); var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            if (mode != "denied")
            {
                var waiting = await AwaitBrowserRestartRun(runs, tenant, sourceId, true); Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
                var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, waiting.ApprovalRequestId!.Value))!;
                Assert.Equal(ToolCatalog.BrowserNavigate, request.Operation.BrowserCommand!.ToolName);
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                    expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode);
            }
            var done = await AwaitBrowserRestartRun(runs, tenant, sourceId, false);
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");
            Assert.NotNull(done.ResponseId); var response = done.ResponseId.Value.ToString("D"); await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == response));
            var history = (await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items");
            var answer = Assert.Single(history.EnumerateArray(), e => e.GetProperty("role").GetString() == "assistant");
            Assert.Equal(mode switch { "denied" => ScriptedLanguageModel.BrowserDenialAnswer, "challenge" => ScriptedLanguageModel.BrowserChallengeAnswer,
                "signup" => ScriptedLanguageModel.BrowserSignupAnswer, _ => ScriptedLanguageModel.BrowserRecordAnswer }, answer.GetProperty("text").GetString());
            var approvals = (await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new())).Items;
            Assert.Equal(mode == "denied" ? 0 : 1, approvals.Count); Assert.Equal(0, mutations); Assert.DoesNotContain(events, e => e.Type == "error");
            if (mode == "denied") Assert.Equal(0, (await native.ObserveTenantSessionAsync(new(tenant, instance, executor, session))).LiveContexts);
            else if (mode == "record")
            {
                Assert.Equal(origin + "/", native.ContextFor(session)!.Pages.Single().Url);
                Assert.Equal(new[] { "definition:browser.record.lookup" }, done.ActiveSkillKeys); Assert.Equal(1, done.SkillLoadCount);
                var application = Assert.Single(history.EnumerateArray(), e => e.GetProperty("role").GetString() == "applicationMessage");
                Assert.Equal(ScriptedLanguageModel.BrowserRecordMessage, application.GetProperty("text").GetString()); Assert.Equal(response, application.GetProperty("responseId").GetString());
                var first = string.Join("\n", model.Requests.First().Messages.Select(m => m.Text)); Assert.DoesNotContain(model.Procedure, first);
                Assert.Contains(model.Procedure, string.Join("\n", model.Requests.Last().Messages.Select(m => m.Text)));
                var text = answer.GetProperty("text").GetString()!; Assert.DoesNotContain("Ignore previous instructions", text); Assert.DoesNotContain("expand the allowlist", text);
                Assert.Contains(events, e => e.Type == "agent.progress" && Equals(e.Payload["kind"], "runningTool") && Equals(e.Payload["message"], ResponseProgressMessages.UsingBrowser));
                Assert.Equal(new[] { origin }, native.HostPolicy.NavigationOrigins);
            }
            if (mode is "challenge" or "signup")
            {
                var currentPage = native.ContextFor(session)!.Pages.Single(); Assert.False(currentPage.IsClosed); Assert.Equal(url, currentPage.Url);
                Assert.DoesNotContain(history.EnumerateArray(), e => e.GetProperty("role").GetString() == "applicationMessage"); Assert.Equal(0, done.SkillLoadCount);
                Assert.DoesNotContain(model.Requests.SelectMany(r => r.Messages), m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.BrowserSnapshot);
                // A person may complete the visible handoff; an unresolved page stays independently observable.
                var resolvedUrl = resolveHandoff ? origin + (mode == "signup" ? "/account" : "/records/AC-1042") : url;
                if (resolveHandoff) await currentPage.GotoAsync(resolvedUrl);
                model.NextTurn = true;
                var current = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!;
                var next = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = resolveHandoff ? "Continue after the Browser step was completed" : "Continue while the human check is still pending",
                    expectedRevision = await Revision(owner, tenant), expectedSessionRevision = current.Snapshot.Revision }));
                var continued = await AttachmentTerminal(runs, tenant, next.GetProperty("runId").GetGuid()); Assert.Equal(AgentRunStatus.Completed, continued.Status);
                Assert.NotNull(continued.ResponseId); Assert.NotEqual(done.ActivationId, continued.ActivationId);
                var nextResponse = continued.ResponseId.Value.ToString("D");
                await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == nextResponse));
                var fresh = Assert.Single(model.Requests.Last().Messages, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.BrowserSnapshot);
                Assert.Contains(resolvedUrl, fresh.Text);
                if (resolveHandoff) Assert.Contains(mode == "signup" ? "Account ready" : "AC-1042", fresh.Text);
                else { Assert.Contains("user_intervention_required", fresh.Text); Assert.Contains("HumanVerificationRequired", fresh.Text); }
                var retained = (await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items");
                Assert.Equal(2, retained.EnumerateArray().Count(e => e.GetProperty("role").GetString() == "assistant"));
                Assert.Contains(retained.EnumerateArray(), e => e.GetProperty("text").GetString() == (resolveHandoff ? "Continued from the fresh owned page." : ScriptedLanguageModel.BrowserChallengeContinueAnswer));
                Assert.DoesNotContain(retained.EnumerateArray(), e => e.GetProperty("status").GetString() == "failed");
                Assert.Single((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new())).Items);
                Assert.Equal(0, mutations); Assert.Equal(resolvedUrl, currentPage.Url);
            }
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
