using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class CanonicalClarificationVerifier(TriggerCommandAuthorizationDecision decision) : ITriggerCommandAuthorizer
    {
        public List<AutomationAuthorizationRequest> Requests { get; } = [];
        public ValueTask<TriggerCommandAuthorizationVerdict> AuthorizeAsync(AutomationAuthorizationRequest proposed, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Requests.Add(proposed); return ValueTask.FromResult(new TriggerCommandAuthorizationVerdict(decision)); }
    }

    [Theory(Timeout = 120000)]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public Task Canonical_timezone_clarification_uses_bounded_own_conversation_without_granting_historical_authority(bool sqlite, bool deny)
        => CanonicalClarification(sqlite, deny ? "deny" : "allow");
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public Task Canonical_disabled_creation_retains_the_exact_semantically_reviewed_state(bool sqlite) => CanonicalClarification(sqlite, "disabled");
    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public Task Canonical_missing_timezone_is_refused_before_semantic_verification(bool sqlite) => CanonicalClarification(sqlite, "timezone");

    private static async Task CanonicalClarification(bool sqlite, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "canonical-automation-clarification-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var verifier = new CanonicalClarificationVerifier(mode == "deny" ? TriggerCommandAuthorizationDecision.Deny : TriggerCommandAuthorizationDecision.Allow);
            var model = new CanonicalClarificationModel { AskTimeZoneFirst = mode is "allow" or "deny" };
            if (mode is "disabled" or "timezone") model.Arguments = JsonSerializer.Serialize(new { instructions = "Review learned experience", kind = "daily", localTime = "09:00",
                executionTarget = "backgroundSession", reportBack = false, enabled = false, timeZone = mode == "timezone" ? "" : "Asia/Ho_Chi_Minh" });
            void Configure(IServiceCollection services) { services.RemoveAll<ITriggerCommandAuthorizer>(); services.AddSingleton<ITriggerCommandAuthorizer>(verifier); }
            await using WebApplicationFactory<Program> host = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            var setup = await StartTenantBackground(host, owner, submit: false, fileCapabilities: [ToolCatalog.AutomationCreate], independentTask: true);
            var tenant = setup.Tenant; var api = $"/api/v2/tenants/{tenant}";
            var sessions = host.Services.GetRequiredService<ITenantSessionStore>(); var parent = (await sessions.FindTenantSessionAsync(tenant, setup.Session))!;
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var access = host.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Canonical semantic author", ["agents.read", "automations.create"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, parent.Owner.ExecutorPrincipalId, role.RoleId, AgentCore.Domain.Security.AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            async Task Send(string text)
            {
                var current = (await sessions.FindTenantSessionAsync(tenant, setup.Session))!;
                var sent = await owner.PostAsJsonAsync(api + $"/sessions/{setup.Session}/messages", new { messageId = Guid.NewGuid(), text,
                    expectedRevision = await Revision(owner, tenant), expectedSessionRevision = current.Snapshot.Revision });
                Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode); var id = (await Json(sent)).GetProperty("runId").GetGuid();
                var runs = host.Services.GetRequiredService<ITenantRunStore>(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while (true)
                {
                    await host.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
                    var run = (await runs.FindTenantRunAsync(tenant, id, deadline.Token))!;
                    if (run.IsTerminal) { Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure}"); break; }
                    await Task.Delay(20, deadline.Token);
                }
            }
            const string task = "Every day at 9am, review learned experience quietly in background.";
            if (mode is "allow" or "deny")
            {
                await Send(task); Assert.Empty(verifier.Requests);
                await Send("Dùng múi giờ Việt Nam.");
                var request = Assert.Single(verifier.Requests); Assert.Equal("Dùng múi giờ Việt Nam.", request.CurrentUserText); Assert.False(request.AdmittedTask);
                using var history = JsonDocument.Parse(request.ConversationEvidenceJson!);
                Assert.Contains(history.RootElement.EnumerateArray(), entry => entry.GetProperty("text").GetString() == task);
                Assert.DoesNotContain(history.RootElement.EnumerateArray(), entry => entry.GetProperty("text").GetString() == "Which timezone should I use?");
                Assert.DoesNotContain(history.RootElement.EnumerateArray(), entry => entry.GetProperty("text").GetString() == request.CurrentUserText);
                Assert.True(request.ConversationEvidenceJson!.Length <= 12000);
            }
            else
            {
                await Send("Review experience daily at 9 in the selected timezone; retain disabled state.");
                if (mode == "disabled") Assert.False(Assert.Single(verifier.Requests).ProposedAction.GetProperty("arguments").GetProperty("enabled").GetBoolean());
                else { Assert.Empty(verifier.Requests); Assert.Equal("invalid", Assert.Single(model.Results).GetProperty("error").GetString()); }
            }
            var retained = (await host.Services.GetRequiredService<ITenantAutomationStore>().ListTenantAutomationsAsync(tenant, parent.Owner.AgentInstanceId, null, 0, 100)).Items;
            if (mode is "deny" or "timezone") Assert.Empty(retained);
            else { var value = Assert.Single(retained); Assert.Equal(mode == "disabled" ? AutomationStatus.Disabled : AutomationStatus.Active, value.Status); }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private sealed class CanonicalClarificationModel(string toolName = ToolCatalog.AutomationCreate) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true,true,Tools:true,StructuredOutput:true);
        public List<JsonElement> Results { get; } = [];
        public string? Arguments { get; set; }
        public bool AskTimeZoneFirst { get; set; }
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested();
            var transcript = request.Interaction ?? request.Messages;
            var user = transcript.ToList().FindLastIndex(m => m.Role == ModelRole.User);
            if (AskTimeZoneFirst)
            {
                AskTimeZoneFirst = false;
                yield return new ModelSemanticResponseReady(new("Which timezone should I use?", new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }
            var result = transcript.Skip(user+1).LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == toolName);
            if (result is not null)
            {
                using var json = JsonDocument.Parse(result.Text); Results.Add(json.RootElement.Clone());
                yield return new ModelSemanticResponseReady(new("Automation result received.",new(ModelSpeechMode.None,null),[]));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            if (request.Tools?.Any(t => t.Name == toolName) != true)
                yield return new ModelToolCallEvent(new(Guid.NewGuid().ToString("N"), ToolCatalog.CapabilitiesLoad, JsonSerializer.Serialize(new { query = toolName, limit = 1 })));
            else yield return new ModelToolCallEvent(new(Guid.NewGuid().ToString("N"), toolName,
                Arguments ??
                "{\"instructions\":\"Review learned experience\",\"kind\":\"daily\",\"localTime\":\"09:00\",\"timeZone\":\"Asia/Ho_Chi_Minh\",\"executionTarget\":\"backgroundSession\",\"reportBack\":false}"));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
