using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class AttachmentLoopModel(bool hold = false) : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        public Guid Session { get; set; }
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<Task>? FirstRequest { get; set; }
        public ModelCapabilities Capabilities => inner.Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.IamEffectivePermissions))
            {
                if (FirstRequest is { } before) await before();
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.IamEffectivePermissions);
                yield return new ModelToolCallEvent(new("attachment-context-scope", ToolCatalog.IamEffectivePermissions, JsonSerializer.Serialize(new { resourceType = "session", resourceId = Session.ToString("D") })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            Entered.TrySetResult();
            if (hold) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            await foreach (var item in inner.GenerateAsync(request, ct)) yield return item;
        }
    }
    private sealed record AttachmentExecutionCase(Guid Tenant, Guid Session, Guid Id, TenantSessionInitiator Human, RoleAssignment Grant);
    private static async Task<AttachmentExecutionCase> PrepareAttachmentExecution(IServiceProvider services, HttpClient owner, bool recoveryBudget = false)
    {
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var instanceRevision = 1L;
        if (recoveryBudget)
        {
            // Recovery charges offline time. Leave room beyond the five-minute claim lease.
            var changed = await Json(await owner.PutAsJsonAsync(api + $"/agents/{instance}/execution-budgets", new {
                expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision,
                budgets = new AgentCore.Domain.Definitions.ExecutionBudgetPolicy(Standard: new(24, 600), InteractiveBrowser: new(48, 600), UnattendedBoundBrowser: new(32, 600)) }));
            instanceRevision = changed.GetProperty("instance").GetProperty("revision").GetInt64();
        }
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision }))).GetProperty("sessionId").GetGuid();
        using var body = new MultipartFormDataContent(); var file = new ByteArrayContent("Private checkpoint attachment body"u8.ToArray()); file.Headers.ContentType = new MediaTypeHeaderValue("text/plain"); body.Add(file, "file", "Checkpoint source.txt");
        var id = (await Json(await owner.PostAsync(api + $"/sessions/{session}/attachments?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision=1", body))).GetProperty("attachmentId").GetGuid();
        var login = (await services.GetRequiredService<IdentityService>().SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!;
        var state = (await services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var human = state.Users.Single(); var access = services.GetRequiredService<TenantAccessService>();
        var role = await access.SaveRoleAsync(human.PrincipalId, tenant, null, "Agent checkpoint source reads", ["sessions.read", "attachments.read"], state.Tenant.Revision);
        var grant = await access.AssignRoleAsync(human.PrincipalId, tenant, agent.GetProperty("principalId").GetGuid(), role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        return new(tenant, session, id, new(human.AccountId, login.Session.SessionId, human.PrincipalId), grant);
    }
    private static async Task<AgentRun> AttachmentTerminal(ITenantRunStore runs, Guid tenant, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { while (true) { var run = (await runs.FindTenantRunAsync(tenant, id, timeout.Token))!; if (run.IsTerminal) return run; await Task.Delay(50, timeout.Token); } }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            var run = await runs.FindTenantRunAsync(tenant, id);
            throw new Xunit.Sdk.XunitException($"Run did not become terminal: status={run?.Status}; revision={run?.Revision}; attempt={run?.AttemptCount}; claim={run?.Claim}; checkpoint={run?.Checkpoint is not null}; effect={run?.SideEffect.Disposition}; failure={run?.Failure?.Code}");
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_attachment_context_revocation_after_first_model_request_prevents_tool_execution_and_followup(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-context-followup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new AttachmentLoopModel(); await using Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); var v = await PrepareAttachmentExecution(factory.Services, owner); model.Session = v.Session;
            model.FirstRequest = async () => await factory.Services.GetRequiredService<TenantAccessService>().RevokeAssignmentAsync(v.Human.PrincipalId, v.Tenant, v.Grant.AssignmentId,
                (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(v.Tenant))!.Tenant.Revision);
            var receipt = await factory.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "Use the provided file", [v.Id], await Revision(owner, v.Tenant), 1);
            var run = await AttachmentTerminal(factory.Services.GetRequiredService<ITenantRunStore>(), v.Tenant, receipt.RunId);
            Assert.Equal(AgentRunStatus.Failed, run.Status); Assert.Single(model.Requests); Assert.Null(run.Result);
            if (run.Checkpoint is not null) { Assert.True(AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)); Assert.Empty(messages!); Assert.DoesNotContain("Private checkpoint attachment body", run.Checkpoint.PayloadJson); }
            Assert.Equal(AttachmentState.Bound, (await factory.Services.GetRequiredService<ITenantAttachmentStore>().FindTenantAsync(v.Tenant, v.Session, v.Id))!.State);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class AttachmentExecutionGate(Func<bool> enabled, ITenantRunExecution inner) : ITenantRunExecution
    { public ValueTask<int> ExecuteRunnableAsync(int limit, CancellationToken ct = default) => enabled() ? inner.ExecuteRunnableAsync(limit, ct) : ValueTask.FromResult(0); }
    [Theory] [InlineData("allowed")] [InlineData("revoke")] [InlineData("source")]
    public async Task Tenant_attachment_context_checkpoint_rechecks_actual_sources_after_SQLite_application_host_recreation(string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-context-restart-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); AttachmentExecutionCase v; Guid runId; TenantAttachmentContextSource witness;
        try
        {
            var initial = new AttachmentLoopModel(hold: true);
            await using (var first = new NativeTenantSqliteFactory(root, initial, s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock))))
            {
                using var owner = first.CreateClient(); v = await PrepareAttachmentExecution(first.Services, owner, recoveryBudget: true); initial.Session = v.Session;
                var receipt = await first.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "Continue with the provided file", [v.Id], await Revision(owner, v.Tenant), 1); runId = receipt.RunId;
                await first.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
                try { await initial.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException)
                {
                    var failed = (await first.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(v.Tenant, runId))!;
                    AgentRunToolCallCheckpoint.TryRead(failed.Checkpoint, out var transcript);
                    throw new InvalidOperationException(JsonSerializer.Serialize(new { failed.Status, failed.Failure, Requests = initial.Requests.Count,
                        Tools = initial.Requests.FirstOrDefault()?.Tools?.Select(t => t.Name), Messages = transcript?.Select(m => new { m.Role, m.Name }) }));
                }
                var saved = (await first.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(v.Tenant, runId))!;
                Assert.Equal(600, saved.Admission.ExecutionBudget!.Profile.DurationSeconds);
                witness = Assert.Single(AgentRunToolCallCheckpoint.ReadExecutionState(saved.Checkpoint).TenantAttachmentSources!); Assert.Equal(v.Id, witness.AttachmentId); Assert.NotNull(witness.ProcessorVersion);
                Assert.DoesNotContain("Private checkpoint attachment body", saved.Checkpoint!.PayloadJson); Assert.Equal(2, initial.Requests.Count);
            }
            clock.Advance(AgentRunCoordinator.ClaimDuration + TimeSpan.FromSeconds(2));
            var model = new AttachmentLoopModel { Session = v.Session }; var enabled = false;
            await using var second = new NativeTenantSqliteFactory(root, model, s => {
                s.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
                s.Replace(ServiceDescriptor.Singleton<ITenantRunExecution>(p => new AttachmentExecutionGate(() => Volatile.Read(ref enabled), new TenantRunExecution(
                    p.GetRequiredService<ITenantRuntimeStore>(), p.GetRequiredService<ITenantRunStore>(), p.GetRequiredService<ITenantRunDispatcher>(), p.GetRequiredService<IIdGenerator>(), p.GetRequiredService<TimeProvider>()))));
            });
            using var fresh = second.CreateClient(); var runs = second.Services.GetRequiredService<ITenantRunStore>(); var retained = (await runs.FindTenantRunAsync(v.Tenant, runId))!;
            Assert.False(retained.IsTerminal); Assert.Equal(witness, Assert.Single(AgentRunToolCallCheckpoint.ReadExecutionState(retained.Checkpoint).TenantAttachmentSources!)); Assert.Empty(model.Requests);
            if (boundary == "revoke") await second.Services.GetRequiredService<TenantAccessService>().RevokeAssignmentAsync(v.Human.PrincipalId, v.Tenant, v.Grant.AssignmentId,
                (await second.Services.GetRequiredService<IIamStore>().ReadTenantAsync(v.Tenant))!.Tenant.Revision);
            if (boundary == "source") await File.WriteAllTextAsync(Path.Combine(root, "attachments", "tenants", v.Tenant.ToString("N"), v.Session.ToString("N"), v.Id.ToString("N")), "Changed retained source");
            Volatile.Write(ref enabled, true); await second.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
            var completed = await AttachmentTerminal(runs, v.Tenant, runId);
            if (boundary == "allowed") { Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code}: {completed.Failure?.Summary}; requests={model.Requests.Count}"); Assert.Single(model.Requests); Assert.Contains("Private checkpoint attachment body", string.Join("\n", model.Requests.Single().Messages.Select(m => m.Text))); }
            else { Assert.Equal(AgentRunStatus.Failed, completed.Status); Assert.Equal("authority-unavailable", completed.Failure!.Code); Assert.Empty(model.Requests); }
            Assert.Equal(0, second.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
