using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;
public sealed partial class TenantAgentAccessTests
{
    private sealed class ExperienceMaintenanceModel : ILanguageModel
    {
        public readonly ExperienceModel Seed = new() { AllowMaintenance = true };
        public readonly ConcurrentQueue<ModelRequest> Requests = new();
        private readonly ScriptedLanguageModel synthetic = new();
        public ModelCapabilities Capabilities => synthetic.Capabilities;
        public bool Maintain; public bool StopAfterSource;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            if (StopAfterSource && request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization
                && m.Text.Contains("\"readAuthorized\":true", StringComparison.Ordinal)))
            {
                yield return new ModelToolCallEvent(new("finish-inspection-without-recording", ToolCatalog.WorkComplete,
                    """{"outcome":"NoAction","summary":"Nothing useful to record after inspection.","attentionRequired":false}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            await foreach (var item in (Maintain ? synthetic : (ILanguageModel)Seed).GenerateAsync(request, ct)) yield return item;
        }
    }
    private static async Task<AgentRun> ExperienceMaintenanceFinal(RuntimeStateFactory factory, Guid tenant, Guid run, AgentRun? knownProviderRetry = null)
    {
        var clock = factory.Services.GetRequiredService<TimeProvider>();
        AgentRun? last = null; var deferrals = 0;
        for (var pass = 0; pass < 800; pass++)
        {
            var current = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, run))!;
            last = current;
            if (current.Result?.ContinuationRunId is { } next) { run = next; continue; }
            if (current.IsTerminal) return current;
            if (current.Status == AgentRunStatus.WaitingToRetry && current.NextRetryAtUtc is { } retry && retry > clock.GetUtcNow())
            {
                // Admission deferral preserves the previous provider failure. Only the exact
                // already-observed retry may survive here; a new provider failure still fails.
                var retainedKnownFailure = knownProviderRetry?.Failure is not null && current.AgentRunId == knownProviderRetry.AgentRunId
                    && current.Checkpoint?.AwaitingRuntimeAdmission == true && knownProviderRetry.NextRetryAtUtc <= clock.GetUtcNow()
                    && JsonSerializer.Serialize(current.Failure) == JsonSerializer.Serialize(knownProviderRetry.Failure);
                Assert.True(current.Failure is null || retainedKnownFailure,
                    $"Unexpected failure before retry: {current.Failure?.Code}; admissionPending={current.Checkpoint?.AwaitingRuntimeAdmission}; attempt={current.AttemptCount}; retry={retry:O}; knownRetry={knownProviderRetry?.NextRetryAtUtc:O}");
                Assert.True(++deferrals <= 8, "Experience continuation repeatedly refused runtime admission.");
                Console.WriteLine($"Experience admission deferral {deferrals}: attempt={current.AttemptCount}; checkpoint={current.Checkpoint is not null}; effect={current.SideEffect.Disposition}; retainedKnownFailure={retainedKnownFailure}; advance={retry - clock.GetUtcNow()}");
                Assert.IsType<FakeTimeProvider>(clock).Advance(retry - clock.GetUtcNow() + TimeSpan.FromMilliseconds(1));
            }
            // This fixture freezes TimeProvider; drive the same durable dispatcher explicitly.
            await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8); await Task.Delay(25);
        }
        throw new TimeoutException($"The approved Experience maintenance did not settle its exact successor: {last?.Status}; retry={last?.NextRetryAtUtc:O}; failure={last?.Failure?.Code} {last?.Failure?.Summary}; progress={last?.Progress?.Summary}; effect={last?.SideEffect.Disposition}; {last?.KnownEffectSummary}");
    }
    public static TheoryData<bool, string> ExperienceMaintenanceCases => new() {
        { false, "approve" }, { true, "approve" }, { false, "scheduled" }, { true, "scheduled" },
        { false, "reject" }, { true, "reject" }, { false, "off" }, { true, "off" },
        { false, "suppress" }, { true, "suppress" }, { false, "optout" }, { true, "optout" },
        { false, "grant" }, { true, "grant" } };
    [Theory(Timeout = 120000)] [MemberData(nameof(ExperienceMaintenanceCases))]
    public async Task Tenant_experience_Automation_maintenance_uses_exact_consent_current_settings_and_factual_successors(bool sqlite, string mode)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var model = new ExperienceMaintenanceModel();
        await using var factory = new RuntimeStateFactory(sqlite, clock, model); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: false, automation: true); await MemoryExecutor(owner, tenant, instance, operations: true); await ExperienceAutomationRights(owner, tenant, instance);
        var revision = await ExperienceOptIn(owner, tenant, instance, true, mode != "off");
        var seeds = new[] { await MemoryRequest(factory, owner, tenant, instance, instanceRevision: revision), await MemoryRequest(factory, owner, tenant, instance, instanceRevision: revision) };
        foreach (var seed in seeds)
        {
            model.Seed.Source = seed.Run.AgentRunId;
            Assert.Equal(AgentRunStatus.Completed, (await ExperienceAutomation(factory, owner, clock, tenant, instance, revision, mode == "scheduled")).Status);
        }
        var api = $"/api/v2/tenants/{tenant}"; var experience = api + $"/agents/{instance}/experience";
        var rows = (await Json(await owner.GetAsync(experience))).GetProperty("items").EnumerateArray().ToArray(); Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.Equal("Instance", row.GetProperty("scope").GetString()));
        if (mode == "grant")
        {
            var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!;
            var role = state.Roles.Single(r => r.Name == "Memory executor " + instance.ToString("N"));
            await factory.Services.GetRequiredService<TenantAccessService>().SaveRoleAsync(actor, tenant, role.RoleId, role.Name, role.Permissions.Where(p => p != "memory.manage").ToArray(), state.Tenant.Revision);
        }
        model.Maintain = true;
        var pending = await ExperienceAutomation(factory, owner, clock, tenant, instance, revision, mode == "scheduled", "synthetic-tenant-experience: consolidate", true);
        if (mode == "off")
        {
            Assert.Equal(AgentRunStatus.Completed, pending.Status); Assert.Equal(AgentRunOutcomeKind.NoAction, pending.Result!.OutcomeKind);
            Assert.Null(pending.ApprovalRequestId); Assert.Equal(AgentRunSideEffectDisposition.None, pending.SideEffect.Disposition); return;
        }
        Assert.True(pending.Status == AgentRunStatus.WaitingForApproval, $"{pending.Status}: {pending.Failure}; {pending.Result?.Text}");
        var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, pending.ApprovalRequestId!.Value))!;
        Assert.Equal(mode == "grant" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
        Assert.Equal(TenantExperienceToolContract.Consolidate, request.Operation.ExperienceOperation!.ToolName); Assert.Null(request.Operation.ExperienceOperation.UserPrincipalId);
        Assert.Equal(2, TenantExperienceRules.Sources(request.Operation.ExperienceOperation).Length);
        var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
        Assert.Contains("PRIVATE_EXPERIENCE_EVIDENCE", detail.GetRawText()); Assert.Contains("Consolidated evidence grouping", detail.GetRawText());
        if (mode is "suppress" or "optout")
        {
            if (mode == "optout") await ExperienceOptIn(owner, tenant, instance, true, false);
            else
            {
                var page = await Json(await owner.GetAsync(experience)); var row = rows[0];
                await Json(await owner.PostAsJsonAsync(experience + "/visibility", new { expectedRevision = page.GetProperty("securityRevision").GetInt64(), experienceId = row.GetProperty("experienceId").GetGuid(), expectedExperienceRevision = row.GetProperty("revision").GetInt64(), visibility = "Suppressed" }));
            }
            var stale = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision }); Assert.False(stale.IsSuccessStatusCode);
            var retained = (await Json(await owner.GetAsync(experience + "?includeHistorical=true"))).GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, retained.Length); Assert.DoesNotContain(retained, row => row.GetProperty("visibility").GetString() == "Superseded"); return;
        }
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash,
            expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
        var completed = await ExperienceMaintenanceFinal(factory, tenant, pending.AgentRunId);
        Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code} {completed.Failure?.Summary}; {completed.Result?.Text}");
        Assert.Equal(pending.Admission.ExecutionContext!.RootActivationId, completed.Admission.ExecutionContext!.RootActivationId);
        var history = (await Json(await owner.GetAsync(experience + "?includeHistorical=true"))).GetProperty("items").EnumerateArray().ToArray();
        if (mode == "reject") { Assert.Equal(AgentRunOutcomeKind.NoAction, completed.Result!.OutcomeKind); Assert.Equal(2, history.Length); }
        else
        {
            Assert.Equal(AgentRunOutcomeKind.Response, completed.Result!.OutcomeKind); Assert.Equal(3, history.Length);
            Assert.Equal(2, history.Count(row => row.GetProperty("visibility").GetString() == "Superseded"));
            var result = Assert.Single(history, row => row.GetProperty("visibility").GetString() == "Eligible"); Assert.Equal("Instance", result.GetProperty("scope").GetString());
            Assert.Contains("retained source lineage", completed.Result.Text); Assert.Null(AgentRunToolCallCheckpoint.ReadExecutionState(completed.Checkpoint).TenantProfileSource);
            if (mode == "grant") Assert.Equal(OperationGrantStatus.Consumed, (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId))!.Status);
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
            Assert.Equal(3, (await Json(await owner.GetAsync(experience + "?includeHistorical=true"))).GetProperty("total").GetInt32());
        }
    }

    [Theory(Timeout = 120000)] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_experience_source_review_can_finish_NoAction_after_refusal_or_authorized_inspection_without_recording(bool sqlite, bool approve)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var model = new ExperienceMaintenanceModel { StopAfterSource = true };
        await using var factory = new RuntimeStateFactory(sqlite, clock, model); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: false); await MemoryExecutor(owner, tenant, instance, operations: true);
        var revision = await ExperienceOptIn(owner, tenant, instance, true); var seed = await MemoryRequest(factory, owner, tenant, instance, instanceRevision: revision);
        var api = $"/api/v2/tenants/{tenant}"; var experience = api + $"/agents/{instance}/experience";
        await Json(await owner.PutAsJsonAsync(api + "/operation-policies/sessions.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 }));
        model.Maintain = true; var preview = await Json(await owner.GetAsync(experience + $"/review-source?sourceKind=AgentRun&sourceId={seed.Run.AgentRunId:D}"));
        var admitted = await Json(await owner.PostAsJsonAsync(experience + "/checkpoints", ExperienceReviewCommand(preview))); var run = admitted.GetProperty("runId").GetGuid();
        AgentRun? pending = null;
        for (var pass = 0; pass < 500; pass++)
        {
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
            pending = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, run))!;
            if (pending.IsTerminal || pending.Status == AgentRunStatus.WaitingForApproval) break;
            await Task.Delay(25);
        }
        Assert.Equal(AgentRunStatus.WaitingForApproval, pending!.Status);
        var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, pending.ApprovalRequestId!.Value))!;
        Assert.Equal(TenantExperienceToolContract.Source, request.Operation.ExperienceOperation!.ToolName);
        var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = approve ? "approve" : "reject", actionHash = request.Operation.ActionHash,
            expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
        var completed = await ExperienceMaintenanceFinal(factory, tenant, run);
        Assert.True(completed.Status == AgentRunStatus.Completed, $"{completed.Status}: {completed.Failure?.Code} {completed.Failure?.Summary}");
        Assert.Equal(AgentRunOutcomeKind.NoAction, completed.Result!.OutcomeKind); Assert.Equal(0, (await Json(await owner.GetAsync(experience))).GetProperty("total").GetInt32());
        Assert.DoesNotContain(model.Requests.SelectMany(r => r.Messages).SelectMany(m => m.ToolCalls ?? []), call => call.Name == TenantExperienceToolContract.Record);
    }
}
