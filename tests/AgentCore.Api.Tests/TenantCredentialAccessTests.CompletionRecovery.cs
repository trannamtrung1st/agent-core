using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Context;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    // Move persisted deadlines without firing wall-clock timers, as in the retired fixture.
    private sealed class CompletionRecoveryClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }
    private static ModelToolCallEvent RecoveryCall(string name, object args) =>
        new(new(Guid.NewGuid().ToString("N"), name, JsonSerializer.Serialize(args)));
    private static void ConfigureCompletionRecovery(IServiceCollection services, TimeProvider clock)
    {
        services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
        foreach (var host in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AgentCore.Api.AgentRunHostedService)).ToArray()) services.Remove(host);
    }

    private async Task<(Guid Tenant, Guid Instance, Guid Destination, string Route, Guid Automation)> CreateCompletionRecovery(
        OAuthFactory factory, HttpClient owner, TimeProvider clock, bool reporting)
    {
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var draft = await Json(await owner.PostAsJsonAsync(api + "/agent-definitions", new { name = "Completion recovery", expectedRevision = await Revision(owner, tenant) }));
        var candidate = JsonNode.Parse(draft.GetProperty("candidate").GetRawText())!;
        var baseline = (await factory.Services.GetRequiredService<IBuiltInAgentDefinitionStore>().GetAsync("general-assistant", 32))!;
        candidate["triggerPolicy"] = JsonSerializer.SerializeToNode(baseline.TriggerPolicy, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        string[] capabilities = [ToolCatalog.WorkspaceWrite, ToolCatalog.WorkspaceRead, ToolCatalog.ArtifactsCreateFromWorkspace];
        candidate["environment"] = JsonSerializer.SerializeToNode(new { harness = Array.Empty<string>(), knowledgeSources = Array.Empty<string>(), toolAllowlist = Array.Empty<string>(),
            capabilities = new { mode = "Selected", resolvedCapabilities = capabilities }, projection = new { alwaysCapabilities = capabilities } });
        var draftId = draft.GetProperty("draftId").GetGuid();
        var saved = await Json(await owner.PutAsJsonAsync(api + $"/definition-drafts/{draftId}", new { candidate, expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1 }));
        Assert.True(saved.TryGetProperty("revision", out _), saved.GetRawText());
        await Json(await owner.PostAsJsonAsync(api + $"/definition-drafts/{draftId}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = saved.GetProperty("revision").GetInt64() }));
        var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents/from-publication", new { definitionId = draft.GetProperty("definitionId").GetString(), version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var role = await access.SaveRoleAsync(human, tenant, null, "Completion recovery executor",
            ["automations.run", "sessions.create", "agent-runs.create", "agents.read", "agents.chat", "sessions.read", "agent-runs.read", "workspaces.read", "workspaces.write", "artifacts.read", "artifacts.create"], await Revision(owner, tenant));
        await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        var destination = reporting ? (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, mode = "text", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid() : Guid.Empty;
        var route = api + $"/agents/{instance}/automations";
        using var response = await owner.PostAsJsonAsync(route, new { enabled = true, name = "Verified artifact", instructions = "Save a verified fixture and report its result",
            reportToSessionId = reporting ? (Guid?)destination : null, requiresTools = true,
            schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = clock.GetUtcNow().AddHours(1).ToString("O") }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (tenant, instance, destination, route, (await Json(response)).GetProperty("automationId").GetGuid());
    }

    private static async Task<AgentRun> DriveCompletionRecovery(OAuthFactory factory, Guid tenant, Guid id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var runs = factory.Services.GetRequiredService<ITenantRunStore>();
        while (true)
        {
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
            var run = (await runs.FindTenantRunAsync(tenant, id))!;
            if (run.IsTerminal || run.Status == AgentRunStatus.WaitingToRetry) return run;
            Assert.NotEqual(AgentRunStatus.WaitingForApproval, run.Status);
            var model = (TenantCompletionRecoveryModel)factory.Services.GetRequiredService<ILanguageModel>();
            Assert.False(deadline.IsCancellationRequested, $"{run.Status}; attempt={run.AttemptCount}; failure={run.Failure?.Code}; retry={run.NextRetryAtUtc:O}; admissionPending={run.Checkpoint?.AwaitingRuntimeAdmission}; claim={run.Claim?.Generation}; parents={model.ParentRequests}; requests={string.Join(" | ", model.Requests.TakeLast(3))}");
            await Task.Delay(20);
        }
    }

    public static TheoryData<bool, string, AgentRunStatus> TenantCompletionRecoveryCases
    {
        get
        {
            var cases = new TheoryData<bool, string, AgentRunStatus>();
            foreach (var sqlite in new[] { false, true })
            foreach (var mode in new[] { "complete", "prose", "wrong-tool", "transport", "semantic", "incomplete-tool", "authentication", "reserve", "oversized", "oversized-twice", "oversized-transport", "oversized-transport-twice" })
                cases.Add(sqlite, mode, mode is "complete" or "transport" or "reserve" or "oversized" or "oversized-transport" ? AgentRunStatus.Completed : AgentRunStatus.Failed);
            return cases;
        }
    }

    [Theory(Timeout = 120000)] [MemberData(nameof(TenantCompletionRecoveryCases))]
    public async Task Tenant_completion_recovery_preserves_artifact_and_never_replays_effects(bool sqlite, string mode, AgentRunStatus expected)
    {
        var clock = new CompletionRecoveryClock(); var model = new TenantCompletionRecoveryModel(mode, clock);
        await using var first = new OAuthFactory(sqlite: sqlite, preserve: sqlite, execute: true, model: model, servicesOverride: s => ConfigureCompletionRecovery(s, clock));
        OAuthFactory? reopened = null; HttpClient? reopenedOwner = null;
        try
        {
            using var firstOwner = ConnectedClient(first); var owner = firstOwner; var factory = first;
            var setup = await CreateCompletionRecovery(factory, owner, clock, reporting: false);
            using var admitted = await owner.PostAsJsonAsync(setup.Route + $"/{setup.Automation}/run", new { expectedRevision = await Revision(owner, setup.Tenant), expectedInstanceRevision = 1, expectedAutomationRevision = 1 });
            Assert.Equal(HttpStatusCode.Created, admitted.StatusCode);
            var id = (await Json(admitted)).GetProperty("runId").GetGuid();
            var initial = await DriveCompletionRecovery(factory, setup.Tenant, id);
            Assert.True(initial.Status == (mode.Contains("transport", StringComparison.Ordinal) ? AgentRunStatus.WaitingToRetry : expected), $"{initial.Status}: {initial.Failure?.Code} {initial.Failure?.Summary}; recovery requests={model.RecoveryRequests.Count}");
            var state = AgentRunToolCallCheckpoint.ReadExecutionState(initial.Checkpoint);
            Assert.Equal(mode == "reserve" ? "runDeadline" : "completionRequired", state.FinalizationReason);
            Assert.Equal(mode.StartsWith("oversized", StringComparison.Ordinal), state.CompletionCorrectionAttempted);
            Assert.True(AgentRunToolCallCheckpoint.TryRead(initial.Checkpoint, out var interaction));
            Assert.DoesNotContain(interaction!, m => m.Role is ModelRole.System or ModelRole.Developer);
            if (sqlite)
            {
                Assert.Equal(0, first.Outbound); await first.DisposeAsync();
                reopened = new OAuthFactory(first.Root, sqlite: true, preserve: true, execute: true, model: model, servicesOverride: s => ConfigureCompletionRecovery(s, clock));
                factory = reopened; reopenedOwner = ConnectedClient(factory); owner = reopenedOwner;
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            }
            if (initial.Status == AgentRunStatus.WaitingToRetry)
            {
                var runStore = factory.Services.GetRequiredService<ITenantRunStore>();
                static string RetryWitness(AgentRun run) => JsonSerializer.Serialize(new { run.Revision, run.Status, run.AttemptCount, run.NextRetryAtUtc, run.Failure, run.Checkpoint });
                var before = RetryWitness((await runStore.FindTenantRunAsync(setup.Tenant, id))!);
                await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
                Assert.Equal(before, RetryWitness((await runStore.FindTenantRunAsync(setup.Tenant, id))!));
                clock.Advance(initial.NextRetryAtUtc!.Value - clock.GetUtcNow() + TimeSpan.FromMilliseconds(1));
            }
            var retained = await DriveCompletionRecovery(factory, setup.Tenant, id);
            Assert.Equal(expected, retained.Status); Assert.Equal(mode.Contains("transport", StringComparison.Ordinal) ? 2 : 1, retained.AttemptCount);
            Assert.Equal(initial.ActivationId, retained.ActivationId); Assert.Equal(initial.ResponseId, retained.ResponseId);
            Assert.Null(retained.Owner.ProfileIdOrNull); Assert.Equal(1, model.Writes); Assert.Equal(1, model.Artifacts);
            var artifacts = await factory.Services.GetRequiredService<ITenantArtifactStore>().ListTenantAsync(setup.Tenant, retained.SessionId, null, null, 100);
            var artifact = Assert.Single(artifacts.Items);
            var api = $"/api/v2/tenants/{setup.Tenant}";
            Assert.Equal("verified fixture", System.Text.Encoding.UTF8.GetString(await owner.GetByteArrayAsync(api + $"/sessions/{retained.SessionId}/artifacts/{artifact.ArtifactId}/content")));
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(api + $"/agents/{setup.Instance}/workspace/content?path=%2Fhome%2Freplay.txt")).StatusCode);
            var code = mode switch { "prose" => "completion-required", "semantic" => "semantic-response-invalid", "incomplete-tool" => "tool-call-invalid", "authentication" => "provider-authentication", _ when mode.EndsWith("twice", StringComparison.Ordinal) => "completion-invalid", _ => null };
            if (code is not null) Assert.Equal(code, retained.Failure!.Code);
            var diagnostics = RunDiagnosticProjection.From(retained)!;
            if (mode.Contains("transport", StringComparison.Ordinal)) Assert.Contains(diagnostics.FailedAttempts, a => a.Code == "Unavailable");
            if (expected == AgentRunStatus.Failed)
            {
                Assert.Equal(mode.Contains("transport", StringComparison.Ordinal) ? 2 : 1, diagnostics.FailedAttempts.Count);
                var conversation = (await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(setup.Tenant, retained.SessionId, null, null, 100))!;
                Assert.Equal(retained.Failure!.DiagnosticId, conversation.History.Items.Last(e => e.Role == ConversationRole.Assistant).Failure!.DiagnosticId);
            }
            if (mode.StartsWith("oversized", StringComparison.Ordinal))
            {
                Assert.Equal(mode.Contains("transport", StringComparison.Ordinal) ? 3 : 2, model.RecoveryRequests.Count);
                Assert.Contains(model.RecoveryRequests.ElementAt(1).Messages, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WorkComplete && m.Text.Contains("2000", StringComparison.Ordinal));
                if (expected == AgentRunStatus.Completed) Assert.Equal("Verified fixture saved.", retained.Result!.Text);
                else Assert.Contains(diagnostics.FailedAttempts, a => a.Reason == "backgroundCompletionInvalid");
            }
            Assert.NotEmpty(model.RecoveryRequests);
            foreach (var request in model.RecoveryRequests)
            {
                Assert.Equal(ToolCatalog.WorkComplete, Assert.Single(request.Tools!).Name);
                Assert.Equal(ModelToolChoice.Named, request.ToolChoice); Assert.Null(request.ResponseContract);
            }
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
            Assert.Equal(retained.Revision, (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(setup.Tenant, id))!.Revision);
            Assert.Equal(1, model.Writes); Assert.Equal(1, model.Artifacts); Assert.Equal(0, factory.Outbound);
        }
        finally
        {
            model.ReleaseParent.TrySetResult(); reopenedOwner?.Dispose(); if (reopened is not null) await reopened.DisposeAsync();
            await first.DisposeAsync(); if (Directory.Exists(first.Root)) Directory.Delete(first.Root, true);
        }
    }

    [Theory(Timeout = 120000)] [InlineData(false, "complete")] [InlineData(true, "complete")]
    [InlineData(false, "prose")] [InlineData(true, "prose")]
    public async Task Tenant_completion_waits_for_busy_parent_then_delivers_once_across_SQLite_reopen(bool sqlite, string mode)
    {
        var clock = new CompletionRecoveryClock(); var model = new TenantCompletionRecoveryModel(mode, clock);
        await using var first = new OAuthFactory(sqlite: sqlite, preserve: sqlite, execute: true, model: model, servicesOverride: s => ConfigureCompletionRecovery(s, clock));
        OAuthFactory? reopened = null; HttpClient? reopenedOwner = null;
        try
        {
            using var firstOwner = ConnectedClient(first); var owner = firstOwner; var factory = first;
            var setup = await CreateCompletionRecovery(factory, owner, clock, reporting: true);
            var api = $"/api/v2/tenants/{setup.Tenant}";
            using var parentResponse = await owner.PostAsJsonAsync(api + $"/sessions/{setup.Destination}/messages", new { messageId = Guid.NewGuid(), text = "Hold the foreground reply", expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = 1 });
            Assert.Equal(HttpStatusCode.Accepted, parentResponse.StatusCode);
            var parentId = (await Json(parentResponse)).GetProperty("runId").GetGuid();
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                while (!model.ParentStarted.Task.IsCompleted)
                {
                    await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
                    await Task.Delay(20, deadline.Token);
                }
            using var admitted = await owner.PostAsJsonAsync(setup.Route + $"/{setup.Automation}/run", new { expectedRevision = await Revision(owner, setup.Tenant), expectedInstanceRevision = 1, expectedAutomationRevision = 1 });
            Assert.Equal(HttpStatusCode.Created, admitted.StatusCode);
            var childId = (await Json(admitted)).GetProperty("runId").GetGuid();
            var child = await DriveCompletionRecovery(factory, setup.Tenant, childId);
            Assert.Equal(mode == "complete" ? AgentRunStatus.Completed : AgentRunStatus.Failed, child.Status);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            await factory.Services.GetRequiredService<TenantBackgroundCompletionService>().AdmitPendingAsync(100, CancellationToken.None);
            var pending = (await runs.FindTenantCompletionAsync(child.Owner, childId))!;
            Assert.Equal(CompletionInboxStatus.Pending, pending.Status); Assert.Null(pending.ReportActivationId);
            Assert.DoesNotContain((await runs.ListTenantSessionRunsAsync(setup.Tenant, setup.Destination, null, 0, 100)).Items, r => r.Admission.OutputContract == AgentRunOutputContract.CompletionReport);
            Assert.False((await runs.FindTenantRunAsync(setup.Tenant, parentId))!.IsTerminal);
            model.ReleaseParent.TrySetResult();
            Assert.Equal(AgentRunStatus.Completed, (await DriveCompletionRecovery(factory, setup.Tenant, parentId)).Status);
            if (sqlite)
            {
                Assert.Equal(0, first.Outbound); await first.DisposeAsync();
                reopened = new OAuthFactory(first.Root, sqlite: true, preserve: true, execute: true, model: model, servicesOverride: s => ConfigureCompletionRecovery(s, clock));
                factory = reopened; reopenedOwner = ConnectedClient(factory); owner = reopenedOwner;
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                runs = factory.Services.GetRequiredService<ITenantRunStore>();
                Assert.Equal(CompletionInboxStatus.Pending, (await runs.FindTenantCompletionAsync(child.Owner, childId))!.Status);
            }
            await factory.Services.GetRequiredService<TenantBackgroundCompletionService>().AdmitPendingAsync(100, CancellationToken.None);
            var report = Assert.Single((await runs.ListTenantSessionRunsAsync(setup.Tenant, setup.Destination, null, 0, 100)).Items, r => r.Admission.OutputContract == AgentRunOutputContract.CompletionReport);
            Assert.Equal(childId, report.Admission.ExecutionContext!.ParentRunId); Assert.Equal(child.ActivationId, report.Admission.ExecutionContext.RootActivationId);
            report = await DriveCompletionRecovery(factory, setup.Tenant, report.AgentRunId); Assert.Equal(AgentRunStatus.Completed, report.Status);
            await factory.Services.GetRequiredService<TenantBackgroundCompletionService>().AdmitPendingAsync(100, CancellationToken.None);
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
            Assert.Single((await runs.ListTenantSessionRunsAsync(setup.Tenant, setup.Destination, null, 0, 100)).Items, r => r.Admission.OutputContract == AgentRunOutputContract.CompletionReport);
            Assert.Equal(CompletionInboxStatus.Delivered, (await runs.FindTenantCompletionAsync(child.Owner, childId))!.Status);
            Assert.Equal(child.Revision, (await runs.FindTenantRunAsync(setup.Tenant, childId))!.Revision);
            var history = (await Json(await owner.GetAsync(api + $"/sessions/{setup.Destination}/messages?limit=100"))).GetProperty("items").EnumerateArray().ToArray();
            Assert.Single(history, e => e.GetProperty("text").GetString() == "Foreground reply finished.");
            Assert.Single(history, e => e.GetProperty("text").GetString() == report.Result!.Text);
            Assert.Single((await factory.Services.GetRequiredService<ITenantArtifactStore>().ListTenantAsync(setup.Tenant, child.SessionId, null, null, 100)).Items);
            Assert.Equal(1, model.Writes); Assert.Equal(1, model.Artifacts); Assert.Equal(1, model.ParentRequests); Assert.Equal(0, factory.Outbound);
        }
        finally
        {
            model.ReleaseParent.TrySetResult(); reopenedOwner?.Dispose(); if (reopened is not null) await reopened.DisposeAsync();
            await first.DisposeAsync(); if (Directory.Exists(first.Root)) Directory.Delete(first.Root, true);
        }
    }

    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_completion_retry_drains_previous_generation_before_admitting_same_Run(bool sqlite)
    {
        var clock = new CompletionRecoveryClock(); var model = new TenantCompletionRecoveryModel("transport", clock);
        await using var factory = new OAuthFactory(sqlite: sqlite, execute: true, model: model, servicesOverride: s => ConfigureCompletionRecovery(s, clock));
        var retiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = factory.Services.GetRequiredService<AgentCore.Api.Realtime.TenantSessionRuntimeHost>();
        try
        {
            using var owner = ConnectedClient(factory); var setup = await CreateCompletionRecovery(factory, owner, clock, reporting: false);
            using var admitted = await owner.PostAsJsonAsync(setup.Route + $"/{setup.Automation}/run", new { expectedRevision = await Revision(owner, setup.Tenant), expectedInstanceRevision = 1, expectedAutomationRevision = 1 });
            Assert.Equal(HttpStatusCode.Created, admitted.StatusCode);
            var id = (await Json(admitted)).GetProperty("runId").GetGuid();
            host.BeforeRuntimeRelease = async run => { if (run.AgentRunId == id && run.AttemptCount == 1) { retiring.TrySetResult(); await release.Task; } };
            var retry = await DriveCompletionRecovery(factory, setup.Tenant, id);
            Assert.Equal(AgentRunStatus.WaitingToRetry, retry.Status);
            await retiring.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Advance(retry.NextRetryAtUtc!.Value - clock.GetUtcNow() + TimeSpan.FromMilliseconds(1));
            var dispatch = factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8).AsTask();
            var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            using var claimedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); AgentRun claimed;
            do
            {
                claimed = (await runs.FindTenantRunAsync(setup.Tenant, id))!;
                Assert.False(claimedDeadline.IsCancellationRequested, $"Retry was not claimed: {claimed.Status}; attempt={claimed.AttemptCount}");
                if (claimed.Status != AgentRunStatus.Running) await Task.Delay(20);
            } while (claimed.Status != AgentRunStatus.Running);
            Assert.Equal(2, claimed.AttemptCount);
            Assert.False(dispatch.IsCompleted, "Retry dispatch completed before its previous Runtime released the Session; a claim alone cannot establish Runtime admission.");
            release.TrySetResult(); await dispatch.WaitAsync(TimeSpan.FromSeconds(10));
            var done = await DriveCompletionRecovery(factory, setup.Tenant, id);
            Assert.Equal(AgentRunStatus.Completed, done.Status); Assert.Equal(2, done.AttemptCount);
            Assert.Equal(retry.ResponseId, done.ResponseId); Assert.Equal(retry.ActivationId, done.ActivationId);
            Assert.Equal(1, model.Writes); Assert.Equal(1, model.Artifacts); Assert.Equal(2, model.RecoveryRequests.Count);
            Assert.Single((await factory.Services.GetRequiredService<ITenantArtifactStore>().ListTenantAsync(setup.Tenant, done.SessionId, null, null, 100)).Items);
            Assert.Equal(0, factory.Outbound);
        }
        finally { release.TrySetResult(); host.BeforeRuntimeRelease = null; }
    }

    private sealed class TenantCompletionRecoveryModel(string mode, CompletionRecoveryClock clock) : ILanguageModel
    {
        public int Writes { get; private set; }
        public int Artifacts { get; private set; }
        private bool failed;
        public int ParentRequests;
        public System.Collections.Concurrent.ConcurrentQueue<string> Requests { get; } = new();
        public TaskCompletionSource ParentStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseParent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public System.Collections.Concurrent.ConcurrentQueue<ModelRequest> RecoveryRequests { get; } = new();
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            Requests.Enqueue($"sections={string.Join(',', request.Context?.Sections.Select(s => s.Id) ?? [])}; tools={string.Join(',', request.Tools?.Select(t => t.Name) ?? [])}");
            if (request.Context?.Sections.Any(s => s.Id == ContextSectionId.CoreBackgroundReport) == true)
                throw new InvalidOperationException("Core must render the completion receipt without a model call.");
            if (request.Tools?.Any(t => t.Name == ToolCatalog.WorkComplete) != true)
            {
                ParentRequests++; ParentStarted.TrySetResult(); await ReleaseParent.Task.WaitAsync(ct);
                yield return new ModelSemanticResponseReady(new("Foreground reply finished.", new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            if (request.Context?.Sections.Any(section => section.Id == ContextSectionId.CoreBackgroundFinalization) == true)
            {
                RecoveryRequests.Enqueue(request);
                if (mode is "semantic" or "incomplete-tool" or "authentication")
                {
                    yield return new ModelFailed(mode switch
                    {
                        "semantic" => new(ProviderErrorCode.InvalidResponse, "Invalid structured response.", FailureReason: ProviderFailureReason.InvalidJson, ResponseChannel: ProviderResponseChannel.StructuredOutput),
                        "incomplete-tool" => new(ProviderErrorCode.InvalidResponse, "Incomplete tool arguments.", FailureReason: ProviderFailureReason.IncompleteToolCall, ResponseChannel: ProviderResponseChannel.ToolCall),
                        _ => new(ProviderErrorCode.Authentication, "Provider authentication failed.")
                    });
                    yield break;
                }
                if (mode == "transport" && !failed)
                {
                    failed = true;
                    yield return new ModelFailed(new(ProviderErrorCode.Unavailable, "Provider unavailable."));
                    yield break;
                }
                if (mode == "prose")
                {
                    yield return new ModelTextDelta("This prose must never become a successful outcome.");
                    yield return new ModelCompleted(ModelStopReason.Completed);
                    yield break;
                }
                if (mode.StartsWith("oversized"))
                {
                    var correcting = request.Messages.Any(message => message.Role == ModelRole.Tool && message.Name == ToolCatalog.WorkComplete);
                    if (correcting && mode.Contains("transport") && !failed)
                    {
                        failed = true;
                        yield return new ModelFailed(new(ProviderErrorCode.Unavailable, "Provider unavailable."));
                        yield break;
                    }
                    yield return RecoveryCall(ToolCatalog.WorkComplete, new
                    {
                        summary = !correcting || mode.EndsWith("twice") ? new string('x', 4442) : "Verified fixture saved.",
                        attentionRequired = false, outcome = "Response"
                    });
                    yield return new ModelCompleted(ModelStopReason.ToolCalls);
                    yield break;
                }
                yield return mode == "wrong-tool" ? RecoveryCall(ToolCatalog.WorkspaceWrite, new { path = "/home/replay.txt", content = "must not execute" })
                    : RecoveryCall(ToolCatalog.WorkComplete, new { summary = "Verified fixture saved.", attentionRequired = false, outcome = "Response" });
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
                yield break;
            }
            var tool = !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WorkspaceWrite) ? ToolCatalog.WorkspaceWrite
                : !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.ArtifactsCreateFromWorkspace) ? ToolCatalog.ArtifactsCreateFromWorkspace : null;
            if (tool is null)
            {
                if (mode == "reserve")
                {
                    // Enter the Standard 180-second completion reserve before its deadline, with an unexecuted substantive call.
                    clock.Advance(TimeSpan.FromSeconds(165));
                    yield return RecoveryCall(ToolCatalog.WorkspaceWrite, new { path = "/home/replay.txt", content = "must not execute" });
                    yield return new ModelCompleted(ModelStopReason.ToolCalls);
                    yield break;
                }
                yield return new ModelTextDelta("Verified fixture saved.");
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }
            if (!request.Tools!.Any(t => t.Name == tool)) yield return RecoveryCall(ToolCatalog.CapabilitiesLoad, new { query = tool, limit = 1 });
            else if (tool == ToolCatalog.WorkspaceWrite) { Writes++; yield return RecoveryCall(tool, new { path = "/home/verified.txt", content = "verified fixture" }); }
            else { Artifacts++; yield return RecoveryCall(tool, new { path = "/home/verified.txt", displayName = "verified.txt" }); }
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

}
