using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class TenantApprovalSqliteFactory(string root, ILanguageModel model)
        : DurableSqliteHostFactory(Path.Combine(root, "store.db"), languageModel: model)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                var options = (PersistenceOptions)services.Last(d => d.ServiceType == typeof(PersistenceOptions)).ImplementationInstance!;
                options.WorkspaceRoot = Path.Combine(root, "workspaces"); options.ArtifactRoot = Path.Combine(root, "artifacts");
                options.AttachmentRoot = Path.Combine(root, "attachments"); options.DefinitionResourceRoot = Path.Combine(root, "resources");
            });
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Model_approval_and_exact_checkpoint_survive_SQLite_host_recreation_before_human_decision(bool preferences)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-model-approval-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Guid tenant = default, instance = default, runId = default, requestId = default; AgentIdentity? desired = null;
        try
        {
            var firstModel = new ApprovalRecordingModel();
            await using (var first = new TenantApprovalSqliteFactory(root, firstModel))
            {
                using var owner = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                Assert.Equal("Sqlite", first.Services.GetRequiredService<PersistenceOptions>().Provider);
                tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
                var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
                instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/self-management", new
                {
                    expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                    overrides = new { mode = new { value = "Assisted" }, scopes = new { value = new[] { "Settings" } } }
                })).StatusCode);
                var actual = (await first.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
                desired = actual.Instance.Persona with { Name = "Durable model-requested persona" };
                if (preferences) firstModel.ProposedPreferenceAuthorization = """{"set":{"language":"vi"},"clear":[]}""";
                else firstModel.ProposedPersona = desired;
                var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 2 }));
                var sessionId = session.GetProperty("sessionId").GetGuid();
                runId = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Persist the requested persona approval", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
                var runs = first.Services.GetRequiredService<ITenantRunStore>(); AgentRun? waiting = null;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (!deadline.IsCancellationRequested)
                {
                    waiting = await runs.FindTenantRunAsync(tenant, runId);
                    if (waiting?.Status == AgentRunStatus.WaitingForApproval || waiting?.IsTerminal == true) break;
                    await Task.Delay(25, deadline.Token);
                }
                Assert.NotNull(waiting); Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}");
                requestId = waiting.ApprovalRequestId!.Value; Assert.Null(waiting.Claim); Assert.Null(waiting.Checkpoint!.BudgetRecordedAtUtc);
                Assert.True(AgentCore.Application.Execution.AgentRunToolCallCheckpoint.TryRead(waiting.Checkpoint, out var pending));
                Assert.Equal(ToolCatalog.RequestAuthorization, Assert.Single(AgentCore.Application.Execution.AgentRunToolCallCheckpoint.PendingCalls(pending!)).Name);
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
            }
            var secondModel = new ApprovalRecordingModel { ProposedPersona = preferences ? null : desired,
                ProposedPreferenceAuthorization = preferences ? """{"set":{"language":"vi"},"clear":[]}""" : null };
            await using (var second = new TenantApprovalSqliteFactory(root, secondModel))
            {
                using var owner = second.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); var api = $"/api/v2/tenants/{tenant}";
                var request = (await second.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, requestId))!;
                Assert.Equal(TenantApprovalStatus.Pending, request.Status);
                if (preferences) { Assert.Null(request.Operation.Persona); Assert.Equal("vi", request.Operation.Preferences!.Language); }
                else Assert.Equal(desired, request.Operation.Persona);
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new
                {
                    decision = "approve", actionHash = request.Operation.ActionHash, expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision
                })).StatusCode);
                var runs = second.Services.GetRequiredService<ITenantRunStore>(); AgentRun? completed = null;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (!deadline.IsCancellationRequested)
                {
                    completed = await runs.FindTenantRunAsync(tenant, runId);
                    if (completed?.IsTerminal == true) break;
                    await Task.Delay(25, deadline.Token);
                }
                Assert.NotNull(completed); Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}");
                var after = (await second.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!.Instance;
                if (preferences) Assert.Equal("vi", after.SettingsOverrides!.Language!.Value); else Assert.Equal(desired, after.Persona);
                Assert.Equal(OperationGrantStatus.Consumed, (await second.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, requestId))!.Status);
                Assert.Single(await second.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200), a => a.Action == "approvals.request");
                Assert.Single(await second.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200), a => a.Action == (preferences ? "operations.preferences.apply" : "operations.persona.apply"));
                Assert.NotEmpty(secondModel.Requests);
                Assert.All(secondModel.Requests, r => Assert.Contains(r.Context!.Sections, s => s.Id == AgentCore.Application.Context.ContextSectionId.ApprovalOutcome));
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
