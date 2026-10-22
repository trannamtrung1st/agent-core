using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    private sealed class ManagementMutationLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Errors { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Sink(Errors);
        public void Dispose() { }
        private sealed class Sink(ConcurrentQueue<string> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState,Exception?,string> format)
            { if (IsEnabled(level) && errors.Count < 12) errors.Enqueue(format(state, exception) + " " + exception); }
        }
    }
    private const string ManagementInstructions = "New exact <script>literal configuration</script> for future Runs.";
    private sealed class ManagementMutationModel(string tool) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new AgentCore.Infrastructure.Providers.Synthetic.ScriptedLanguageModel().Capabilities;
        public string Arguments = "{}"; public bool ReadFirst = true; public bool ForceConsent;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            var messages = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray(); ModelToolCall? next = null;
            if (!messages.Any(m => m.Name == ToolCatalog.RequestAuthorization))
            {
                if (ReadFirst && !messages.Any(m => m.Name == "settings.inspect"))
                    next = request.Tools?.Any(t => t.Name == "settings.inspect") == true ? new("read-management", "settings.inspect", """{"section":"instructions"}""")
                        : messages.Any(m => m.Name == ToolCatalog.CapabilitiesLoad && m.ToolCallId == "discover-management-read") ? null
                        : new("discover-management-read", ToolCatalog.CapabilitiesLoad, """{"query":"settings.inspect","limit":1}""");
                else if (!messages.Any(m => m.Name == tool))
                    next = request.Tools?.Any(t => t.Name == tool) == true ? new("execute-management", tool, Arguments)
                        : messages.Any(m => m.Name == ToolCatalog.CapabilitiesLoad && m.ToolCallId == "discover-management-command") ? null
                        : new("discover-management-command", ToolCatalog.CapabilitiesLoad, JsonSerializer.Serialize(new { query = tool, limit = 1 }));
                else
                {
                    using var result = JsonDocument.Parse(messages.Last(m => m.Name == tool).Text);
                    if (ForceConsent || result.RootElement.TryGetProperty("decision", out var decision) && decision.GetString() == "requiresApproval")
                        next = new("consent-management", ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new { operation = "mutate_management", change = new { tool, arguments = JsonSerializer.Deserialize<JsonElement>(Arguments) } }));
                }
            }
            if (next is not null) { yield return new ModelToolCallEvent(next); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break; }
            var authorized = messages.LastOrDefault(m => m.Name == ToolCatalog.RequestAuthorization) ?? messages.LastOrDefault(m => m.Name == tool);
            using var body = JsonDocument.Parse(authorized?.Text ?? "{}");
            var applied = body.RootElement.TryGetProperty("applied", out var done) && done.ValueKind == JsonValueKind.True;
            yield return new ModelTextDelta(applied ? "The exact management change was recorded; the current Run stayed pinned." : "The management change was withheld; no new configuration was applied.");
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    private static async Task<(Guid Tenant, Guid Instance)> SetupManagementMutation(WebApplicationFactory<Program> host, HttpClient owner)
    {
        var (tenant, instance) = await SetupManagementRead(host, owner, true);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync($"/api/v2/tenants/{tenant}/agents/{instance}/self-management", new {
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2,
            overrides = new { mode = new { value = "Managed" }, scopes = new { value = new[] { "Settings", "ManagementPolicy", "AutomationPreferences" } }, autoApply = new { value = new[] { "ConversationPreferences" } } }
        })).StatusCode);
        return (tenant, instance);
    }
    private static string ManagementCommand(string tool, string model) => tool switch {
        "settings.patch" => JsonSerializer.Serialize(new { expectedInstanceRevision = 3, section = "instructions", set = new { agentInstructions = ManagementInstructions }, clear = Array.Empty<string>() }),
        "management.freeze" => """{"expectedInstanceRevision":3}""",
        "management.policy.update" => """{"expectedInstanceRevision":3,"mode":"Assisted","scopes":["Settings"],"managedAutoApply":[]}""",
        "automation.preferences.patch" => JsonSerializer.Serialize(new { expectedInstanceRevision = 3, catalogKey = model, reasoningEffort = (string?)null }),
        _ => throw new ArgumentException()
    };
    [Theory]
    [InlineData(false, "settings.patch", "approve")] [InlineData(true, "settings.patch", "approve")]
    [InlineData(false, "settings.patch", "reject")] [InlineData(true, "settings.patch", "reject")]
    [InlineData(false, "management.policy.update", "approve")] [InlineData(true, "management.policy.update", "approve")]
    [InlineData(false, "management.policy.update", "reject")] [InlineData(true, "management.policy.update", "reject")]
    [InlineData(false, "management.freeze", "approve")] [InlineData(true, "management.freeze", "approve")]
    [InlineData(false, "management.freeze", "reject")] [InlineData(true, "management.freeze", "reject")]
    [InlineData(false, "automation.preferences.patch", "approve")] [InlineData(true, "automation.preferences.patch", "approve")]
    [InlineData(false, "automation.preferences.patch", "reject")] [InlineData(true, "automation.preferences.patch", "reject")]
    public async Task Canonical_management_commands_reuse_exact_consent_atomic_effects_and_preserve_read_sources_and_Run_pins(bool sqlite, string tool, string decision)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-management-mutation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ManagementMutationModel(tool);
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            await using var host = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model))));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            var logs = new ManagementMutationLogs(); host.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
            var (tenant, instance) = await SetupManagementMutation(host, owner); var api = $"/api/v2/tenants/{tenant}";
            var key = host.Services.GetRequiredService<IModelCatalog>().DefaultKey; model.Arguments = ManagementCommand(tool, key);
            var created = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 3 })); var session = created.GetProperty("sessionId").GetGuid();
            var response = await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect then apply this exact Agent configuration through qualified consent.", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); var runId = (await Json(response)).GetProperty("runId").GetGuid();
            if (factory is ApprovalExecutionFactory memory) Volatile.Write(ref memory.Enabled, true);
            var runs = host.Services.GetRequiredService<ITenantRunStore>(); var pending = await AwaitManagementRun(runs, tenant, runId, true);
            Assert.True(pending.Status == AgentRunStatus.WaitingForApproval, $"{pending.Status}: {pending.Failure?.Code}: {pending.Failure?.Summary}");
            var approvals = host.Services.GetRequiredService<ITenantApprovalStore>(); var request = (await approvals.FindApprovalAsync(tenant, pending.ApprovalRequestId!.Value))!;
            Assert.Equal(TenantOperationKind.ManagementMutation, request.Operation.Kind); Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal(tool, request.Operation.ManagementMutation!.ToolName);
            Assert.Single(TenantManagementReadReferences.Read(pending.Checkpoint));
            var source = await host.Services.GetRequiredService<ITenantManagementStore>().ReadTenantManagementSourceAsync(new(tenant, runId), null);
            _ = TenantManagementMutationRules.Change(source.Reader, request.Operation.ManagementMutation);
            Assert.Throws<AccessDeniedException>(() => TenantManagementMutationRules.Change(source.Reader, request.Operation.ManagementMutation with { ChangesJson = "[]" }));
            if (tool == "management.policy.update") Assert.Contains("None", request.Operation.ManagementMutation.ChangesJson);
            Assert.Equal(3, (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance.Revision);
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}")); Assert.Equal("mutate_management", detail.GetProperty("preparedAction").GetProperty("operation").GetString()); Assert.True(detail.GetProperty("review").GetProperty("canDecide").GetBoolean());
            if (sqlite && tool == "settings.patch" && decision == "approve")
            {
                await using var db = await host.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
                await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.ManagementMutation.ChangesJson','[]') WHERE RequestId={request.RequestId}"));
                await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261022070000_TenantManagementReadConsent"));
                // Compatible earlier Down steps may commit before a lower refusal.
                // Restore the running binary's schema before resuming its pending task.
                await db.GetService<IMigrator>().MigrateAsync();
                Assert.False(db.Database.HasPendingModelChanges());
                Assert.Equal(TenantApprovalStatus.Pending, (await approvals.FindApprovalAsync(tenant, request.RequestId))!.Status);
            }
            var decided = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision, actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
            Assert.True(decided.StatusCode == HttpStatusCode.OK, (await decided.Content.ReadAsStringAsync()) + "\n" + string.Join("\n", logs.Errors));
            AgentRun? final;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                while (true)
                {
                    final = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100, deadline.Token)).Items.FirstOrDefault(r => r.ApprovalRequestId == request.RequestId && r.AgentRunId != runId);
                    if (final?.IsTerminal == true) break;
                    if (deadline.IsCancellationRequested)
                    {
                        var retained = (await runs.ListTenantSessionRunsAsync(tenant, session, null, 0, 100)).Items;
                        Assert.Fail(JsonSerializer.Serialize(retained.Select(r => new { r.AgentRunId, r.Status, r.Revision, Failure = r.Failure?.Code, Effect = r.SideEffect.Disposition })) + "\n" + string.Join("\n", logs.Errors));
                    }
                    await Task.Delay(25);
                }
            Assert.True(final.Status == AgentRunStatus.Completed, $"{final.Status}: {final.Failure?.Code}: {final.Failure?.Summary}");
            Assert.Equal(3, final.Admission.Configuration!.InstanceRevision); Assert.Single(TenantManagementReadReferences.Read(final.Checkpoint));
            Assert.DoesNotContain(ManagementInstructions, final.Admission.Configuration.Definition.AgentInstructions);
            var saved = (await host.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance;
            Assert.Equal(decision == "approve" ? 4 : 3, saved.Revision); Assert.Null(await approvals.FindOperationGrantAsync(tenant, request.RequestId));
            var receipt = await host.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(tenant, request.Operation.OperationId);
            if (decision == "approve")
            {
                Assert.NotNull(receipt); Assert.Equal(TenantManagementMutationRules.Completed, receipt.ReasonCode); Assert.Equal(4, receipt.InstanceRevision);
                if (tool == "settings.patch") Assert.Equal(ManagementInstructions, saved.SettingsOverrides!.AgentInstructions!.Value);
                if (tool == "management.freeze") Assert.True(saved.HarnessManagement!.Policy.Frozen);
                if (tool == "management.policy.update") Assert.Equal(AgentCore.Domain.Definitions.HarnessManagementMode.Assisted, saved.HarnessManagement!.Overrides!.Mode!.Value);
                if (tool == "automation.preferences.patch") Assert.Equal(key, saved.UnattendedModelCatalogKey);
                await host.Services.GetRequiredService<ITenantManagementStore>().RequireTenantManagementReadSourcesAsync(new(tenant, final.AgentRunId), TenantManagementReadReferences.Read(final.Checkpoint), null);
            }
            else Assert.Null(receipt);
            var history = await Json(await owner.GetAsync(api + $"/sessions/{session}/messages?limit=100")); Assert.Contains(decision == "approve" ? "exact management change was recorded" : "management change was withheld", history.GetRawText());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
