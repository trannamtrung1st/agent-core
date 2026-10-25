using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.TestHost;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, false)] [InlineData(true, false, false)]
    [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task Model_managed_preferences_use_current_authority_and_direct_receipts_through_worker_and_Runtime(bool sqlite, bool allowed, bool race)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-direct-preferences-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ApprovalRecordingModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantApprovalSqliteFactory(root, model) : new ApprovalExecutionFactory();
            if (factory is ApprovalExecutionFactory memory) model = memory.Model;
            var policyRead = new PreferencePolicyReadRace();
            await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
                var descriptor = services.Single(d => d.ServiceType == typeof(IBuiltInAgentDefinitionStore));
                services.Replace(ServiceDescriptor.Singleton<IBuiltInAgentDefinitionStore>(provider => new PreferencePolicyDefinitions(
                    (IBuiltInAgentDefinitionStore)(descriptor.ImplementationInstance ?? descriptor.ImplementationFactory?.Invoke(provider)
                        ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!)), policyRead)));
            }));
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
            var api = $"/api/v2/tenants/{tenant}";
            var created = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
            var instance = created.GetProperty("instance").GetProperty("instanceId").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new
            {
                expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                overrides = new { mode = new { value = "Managed" }, scopes = new { value = new[] { "Settings" } }, autoApply = new { value = new[] { "ConversationPreferences" } } }
            })).StatusCode);
            var agents = host.Services.GetRequiredService<ITenantAgentStore>(); var before = (await agents.FindTenantAgentAsync(tenant, instance))!;
            var iam = host.Services.GetRequiredService<IIamStore>(); var state = (await iam.ReadTenantAsync(tenant))!;
            var actor = Assert.Single(state.Users).PrincipalId; var access = host.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(actor, tenant, null, "Own preferences", allowed ? ["agents.read", "agents.update"] : ["agents.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(actor, tenant, before.PrincipalId, role.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
            if (race)
            {
                var generations = 0;
                model.BeforeGenerate = (_, _) => {
                    if (Interlocked.Increment(ref generations) == 1) policyRead.AfterRead = async () => {
                        var fresh = (await iam.ReadTenantAsync(tenant))!;
                        await access.SaveRoleAsync(actor, tenant, null, "Unrelated role during live preference policy read", ["agents.read"], fresh.Tenant.Revision);
                    };
                    return Task.CompletedTask;
                };
            }
            model.ProposedPreferences = """{"set":{"language":"vi","responseLength":"concise"},"clear":[]}""";
            var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 }));
            var sessionId = session.GetProperty("sessionId").GetGuid();
            var submitted = await owner.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Use concise Vietnamese responses", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode); var runId = (await Json(submitted)).GetProperty("runId").GetGuid();
            if (factory is ApprovalExecutionFactory memoryExecution) Volatile.Write(ref memoryExecution.Enabled, true);
            var runs = host.Services.GetRequiredService<ITenantRunStore>(); AgentRun? completed = null;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                while (!deadline.IsCancellationRequested)
                {
                    completed = await runs.FindTenantRunAsync(tenant, runId);
                    if (completed?.IsTerminal == true) break;
                    await Task.Delay(25, deadline.Token);
                }
            Assert.NotNull(completed); Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}");
            var outcome = model.Requests.SelectMany(r => r.Messages).Last(m => m.Role == ModelRole.Tool && m.Name == TenantPreferenceToolContract.Name);
            using var result = System.Text.Json.JsonDocument.Parse(outcome.Text);
            Assert.Equal(allowed, result.RootElement.GetProperty("applied").GetBoolean());
            Assert.Equal(allowed ? "allowed" : "requiresApproval", result.RootElement.GetProperty("decision").GetString());
            var current = (await agents.FindTenantAgentAsync(tenant, instance))!.Instance;
            Assert.Equal(allowed ? 3 : 2, current.Revision); Assert.Equal(before.Instance.Persona, current.Persona);
            if (allowed) { Assert.Equal("vi", current.SettingsOverrides!.Language!.Value); Assert.Equal("concise", current.SettingsOverrides.ResponseLength!.Value); }
            else Assert.Equal(before.Instance.SettingsOverrides, current.SettingsOverrides);
            Assert.Empty((await host.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(tenant, null, null, new(0, 100))).Items);
            var receipt = await host.Services.GetRequiredService<ITenantPreferenceOperationStore>().FindDirectOperationReceiptAsync(tenant, runId, "direct-preferences");
            if (allowed) { Assert.NotNull(receipt); Assert.Null(receipt.RequestId); } else Assert.Null(receipt);
            Assert.Equal(race ? 1 : 0, policyRead.Applied);
            Assert.Equal(2, model.Requests.Count);
            Assert.Null(completed.Owner.ProfileIdOrNull);
            Assert.Equal(allowed ? 1 : 0, (await iam.ReadAuditAsync(tenant, 200)).Count(a => a.Action == "operations.preferences.apply"));
            Assert.Contains(model.Requests, r => r.Tools?.Any(t => t.Name == TenantPreferenceToolContract.Name) == true);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private sealed class PreferencePolicyReadRace
    {
        public Func<Task>? AfterRead;
        public int Applied;
    }
    private sealed class PreferencePolicyDefinitions(IBuiltInAgentDefinitionStore inner, PreferencePolicyReadRace race) : IBuiltInAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public async ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken ct = default)
        {
            var result = await inner.GetAsync(id, version, ct);
            if (Interlocked.Exchange(ref race.AfterRead, null) is { } change) { await change(); Interlocked.Increment(ref race.Applied); }
            return result;
        }
    }
}
