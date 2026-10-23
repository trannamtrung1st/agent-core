using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private sealed class OwnedProtocolModel(string mode) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: mode != "marker-contract");
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public TaskCompletionSource WorkEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWork { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes; public int Reads; public int Corrections; public int Foreground;
        public bool Evidence => mode is not ("response" or "no-action" or "needs-attention" or "native-contract" or "marker-contract");
        public static ModelToolCall Completion(string id = "protocol-finish", bool invalid = false) => new(id, ToolCatalog.WorkComplete,
            JsonSerializer.Serialize(new { summary = "Partial owned task; remaining work needs attention.", attentionRequired = true, outcome = invalid ? "Response" : "NeedsAttention" }));
        private static (string Reason, string Channel) Failure(string value) => value switch
        {
            "format-json" => (ProviderFailureReason.InvalidJson, ProviderResponseChannel.StructuredOutput),
            "format-arguments" => (ProviderFailureReason.ResponseFunctionArgumentsInvalid, ProviderResponseChannel.ResponseFunction),
            "format-marker" => (ProviderFailureReason.InvalidMarkerEnvelope, ProviderResponseChannel.MarkerCompatibility),
            "format-limit" => (ProviderFailureReason.OutputLimit, ProviderResponseChannel.StructuredOutput),
            _ => (ProviderFailureReason.InvalidBlocks, ProviderResponseChannel.ResponseFunction)
        };
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request); await Task.Yield(); ct.ThrowIfCancellationRequested();
            if (request.Tools?.Any(t => t.Name == ToolCatalog.WorkComplete) != true)
            {
                Foreground++;
                yield return new ModelSemanticResponseReady(new("Continued the same owned task.", new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            Assert.Null(request.ResponseContract);
            Assert.DoesNotContain(request.Tools, t => t.Name == AssistantResponseSchema.ResponseFunctionName);
            Assert.DoesNotContain(request.Messages, m => m.Text.Contains("required response protocol", StringComparison.Ordinal));
            var final = request.Context?.Sections.Any(s => s.Id == ContextSectionId.CoreBackgroundFinalization) == true;
            if (final)
            {
                Corrections++;
                Assert.Equal(ToolCatalog.WorkComplete, Assert.Single(request.Tools).Name); Assert.Equal(ModelToolChoice.Named, request.ToolChoice);
                if (mode is "retry" or "saved" or "interrupted" && Corrections == 1)
                { yield return new ModelFailed(new(ProviderErrorCode.Unavailable, "Controlled correction transport failure.")); yield break; }
                if (mode == "exhaust")
                { yield return new ModelFailed(new(ProviderErrorCode.InvalidResponse, "Repeated invalid blocks.", FailureReason: ProviderFailureReason.InvalidBlocks, ResponseChannel: ProviderResponseChannel.ResponseFunction)); yield break; }
                yield return new ModelToolCallEvent(Completion(invalid: mode == "invalid-completion"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (!Evidence)
            {
                if (mode is "native-contract" or "marker-contract")
                {
                    yield return new ModelTextDelta("Conversational formatting cannot complete background work.");
                    yield return new ModelCompleted(ModelStopReason.Completed); yield break;
                }
                yield return new ModelToolCallEvent(new("initial-outcome", ToolCatalog.WorkComplete, JsonSerializer.Serialize(new {
                    summary = mode == "no-action" ? "No action was necessary." : "Owned background task finished.",
                    attentionRequired = mode == "needs-attention", outcome = mode == "no-action" ? "NoAction" : mode == "needs-attention" ? "NeedsAttention" : "Response" })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WorkspaceWrite))
            {
                Writes++; Assert.Contains(request.Tools, t => t.Name == ToolCatalog.WorkspaceWrite);
                yield return new ModelToolCallEvent(new("protocol-write", ToolCatalog.WorkspaceWrite, """{"path":"/home/verified.txt","content":"verified fixture"}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WorkspaceRead))
            {
                Reads++; Assert.Contains(request.Tools, t => t.Name == ToolCatalog.WorkspaceRead);
                yield return new ModelToolCallEvent(new("protocol-read", ToolCatalog.WorkspaceRead, """{"path":"/home/verified.txt"}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            Assert.Contains("verified fixture", request.Messages.Single(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WorkspaceRead).Text);
            WorkEntered.TrySetResult();
            if (mode is "cutoff" or "deadline") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            if (mode is "retry" or "saved" or "interrupted") await ReleaseWork.Task.WaitAsync(ct);
            var failure = Failure(mode);
            yield return new ModelFailed(new(ProviderErrorCode.InvalidResponse, "Controlled invalid response after an owned read.", FailureReason: failure.Reason, ResponseChannel: failure.Channel));
        }
    }
    public static TheoryData<bool, string> OwnedProtocolCases
    {
        get
        {
            var cases = new TheoryData<bool, string>();
            foreach (var sqlite in new[] { false, true })
            foreach (var mode in new[] { "response", "no-action", "needs-attention", "native-contract", "marker-contract", "format-blocks", "format-json", "format-arguments", "format-marker", "format-limit", "exhaust", "invalid-completion", "retry", "saved", "interrupted", "cutoff", "deadline" })
                cases.Add(sqlite, mode);
            return cases;
        }
    }
    private static async Task<AgentRun> DriveOwnedProtocol(OAuthFactory factory, Guid tenant, Guid id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var runs = factory.Services.GetRequiredService<ITenantRunStore>();
        do
        {
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
            var run = (await runs.FindTenantRunAsync(tenant, id, deadline.Token))!;
            if (run.IsTerminal || run.Status == AgentRunStatus.WaitingToRetry) return run;
            Assert.NotEqual(AgentRunStatus.WaitingForApproval, run.Status);
            await Task.Delay(20, deadline.Token);
        } while (true);
    }
    [Theory(Timeout = 120000)] [MemberData(nameof(OwnedProtocolCases))]
    public async Task Tenant_background_protocol_recovery_preserves_owned_evidence_outcomes_and_same_Run_boundaries(bool sqlite, string mode)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var model = new OwnedProtocolModel(mode);
        ILanguageModel semantic = new SemanticResponseLanguageModel(model);
        await using var first = new OAuthFactory(sqlite: sqlite, preserve: sqlite, execute: true, model: semantic, servicesOverride: s => ConfigureCompletionRecovery(s, clock));
        OAuthFactory? reopened = null; HttpClient? reopenedOwner = null;
        try
        {
            using var firstOwner = ConnectedClient(first); var owner = firstOwner; var factory = first;
            var setup = await CreateCompletionRecovery(factory, owner, clock, false);
            var admitted = await owner.PostAsJsonAsync(setup.Route + $"/{setup.Automation}/run", new { expectedRevision = await Revision(owner, setup.Tenant), expectedInstanceRevision = 1, expectedAutomationRevision = 1 });
            Assert.Equal(HttpStatusCode.Created, admitted.StatusCode); var id = (await Json(admitted)).GetProperty("runId").GetGuid();
            var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            var initial = (await runs.FindTenantRunAsync(setup.Tenant, id))!;
            AgentRun? firstClaim = null;
            if (mode is "cutoff" or "deadline" or "retry" or "saved" or "interrupted")
            {
                await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
                await model.WorkEntered.Task.WaitAsync(TimeSpan.FromSeconds(25));
                firstClaim = (await runs.FindTenantRunAsync(setup.Tenant, id))!;
                Assert.Equal(AgentRunStatus.Running, firstClaim.Status); Assert.NotNull(firstClaim.Claim);
                if (mode is "cutoff" or "deadline") clock.Advance(TimeSpan.FromSeconds(mode == "cutoff" ? 150 : 181));
                else model.ReleaseWork.TrySetResult();
            }
            var current = await DriveOwnedProtocol(factory, setup.Tenant, id);
            var beforeResume = model.Requests.Count;
            if (mode is "retry" or "saved" or "interrupted")
            {
                Assert.Equal(AgentRunStatus.WaitingToRetry, current.Status);
                await factory.Services.GetRequiredService<AgentCore.Api.Realtime.TenantSessionRuntimeHost>().WaitForGenerationReleaseAsync(
                    current.SessionId, id, firstClaim!.Claim!.Generation, CancellationToken.None);
                Assert.Equal("retryPending", AgentRunToolCallCheckpoint.ReadExecutionState(current.Checkpoint).ProtocolRecovery!.Outcome);
                clock.Advance(current.NextRetryAtUtc!.Value - clock.GetUtcNow() + TimeSpan.FromMilliseconds(1));
                if (mode is "saved" or "interrupted")
                {
                    // Seed only a crash-point checkpoint on the actual admitted owner,
                    // retaining its real protected read, file bytes and source witnesses.
                    var storage = factory.Services.GetRequiredService<ITenantRuntimeStore>(); var at = clock.GetUtcNow();
                    current = await storage.ApplyTenantRuntimeCommandAsync(current.Owner, id,
                        new AgentRunCommand.Claim(current.Revision, at, Guid.NewGuid(), at.AddMinutes(5)), Guid.NewGuid());
                    var checkpoint = current.Checkpoint!; var state = AgentRunToolCallCheckpoint.ReadExecutionState(checkpoint);
                    Assert.True(AgentRunToolCallCheckpoint.TryRead(checkpoint, out var messages));
                    var retained = messages!.ToList();
                    if (mode == "saved") retained.Add(new(ModelRole.Assistant, "", ToolCalls: [OwnedProtocolModel.Completion("saved-protocol-finish")]));
                    var payload = AgentRunToolCallCheckpoint.Write(retained, protocolRepairReason: state.ProtocolRecovery!.Reason,
                        executionState: state with { ProtocolRecovery = state.ProtocolRecovery with { Outcome = "started" } });
                    current = await storage.ApplyTenantRuntimeCommandAsync(current.Owner, id, new AgentRunCommand.Checkpoint(current.Revision, at, current.Claim!.Generation,
                        new(payload, checkpoint.StepCount, checkpoint.OutputBytes, checkpoint.RemainingOverallBudgetMs, checkpoint.ActiveExecutionMs, at), null), Guid.NewGuid());
                    current = await storage.ApplyTenantRuntimeCommandAsync(current.Owner, id, new AgentRunCommand.Fail(current.Revision, at, current.Claim!.Generation,
                        "provider-unavailable", "Controlled persisted recovery boundary.", true, at.AddSeconds(1), Guid.NewGuid()), Guid.NewGuid());
                    clock.Advance(TimeSpan.FromSeconds(2));
                }
                if (sqlite)
                {
                    await first.DisposeAsync(); reopened = new(first.Root, sqlite: true, preserve: true, execute: true, model: semantic, servicesOverride: s => ConfigureCompletionRecovery(s, clock));
                    factory = reopened; reopenedOwner = ConnectedClient(factory); owner = reopenedOwner;
                    await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); runs = factory.Services.GetRequiredService<ITenantRunStore>();
                }
                current = await DriveOwnedProtocol(factory, setup.Tenant, id);
                Assert.Equal(mode == "retry" ? beforeResume + 1 : beforeResume, model.Requests.Count);
            }
            var failed = mode is "exhaust" or "invalid-completion" or "interrupted" or "deadline";
            Assert.True(current.Status == (failed ? AgentRunStatus.Failed : AgentRunStatus.Completed), $"{mode}: {current.Status} {current.Failure?.Code} {current.Failure?.Summary}");
            Assert.Equal(initial.ActivationId, current.ActivationId); Assert.Equal(initial.ResponseId, current.ResponseId); Assert.Null(current.Owner.ProfileIdOrNull);
            Assert.Equal(mode is "saved" or "interrupted" ? 3 : mode == "retry" ? 2 : 1, current.AttemptCount);
            Assert.Equal(model.Evidence ? 1 : 0, model.Writes); Assert.Equal(model.Evidence ? 1 : 0, model.Reads);
            Assert.True(AgentRunToolCallCheckpoint.TryRead(current.Checkpoint, out var interaction));
            Assert.Equal(model.Evidence ? 1 : 0, interaction!.Count(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WorkspaceRead));
            var stateAfter = AgentRunToolCallCheckpoint.ReadExecutionState(current.Checkpoint);
            if (mode.StartsWith("format-") || mode is "retry" or "saved")
            { Assert.Equal("completionOnly", stateAfter.ProtocolRecovery!.Strategy); Assert.Equal("succeeded", stateAfter.ProtocolRecovery.Outcome); Assert.Equal(AgentRunOutcomeKind.NeedsAttention, current.Result!.OutcomeKind); }
            if (mode == "interrupted") Assert.Equal("interrupted", stateAfter.ProtocolRecovery!.Outcome);
            if (mode == "invalid-completion") Assert.Equal("completion-invalid", current.Failure!.Code);
            if (mode == "deadline") { Assert.Equal("run-deadline", current.Failure!.Code); Assert.Equal(0, model.Corrections); }
            if (mode == "cutoff") { Assert.Equal(1, model.Corrections); Assert.Equal(AgentRunOutcomeKind.NeedsAttention, current.Result!.OutcomeKind); }
            if (mode is "native-contract" or "marker-contract") { Assert.Equal(2, model.Requests.Count); Assert.Equal(1, model.Corrections); }
            if (mode is "response" or "no-action" or "needs-attention")
            {
                Assert.Equal(mode == "no-action" ? AgentRunOutcomeKind.NoAction : mode == "needs-attention" ? AgentRunOutcomeKind.NeedsAttention : AgentRunOutcomeKind.Response, current.Result!.OutcomeKind);
                var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var saved = (await sessions.FindTenantSessionAsync(setup.Tenant, current.SessionId))!;
                var sessionPath = $"/api/v2/tenants/{setup.Tenant}/sessions/{current.SessionId}";
                var history = (await Json(await owner.GetAsync(sessionPath + "/messages?limit=100"))).GetProperty("items").EnumerateArray().ToArray();
                var assistants = history.Count(e => string.Equals(e.GetProperty("role").GetString(), "assistant", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(mode == "no-action" ? 0 : 1, assistants);
                var unavailable = await owner.PostAsJsonAsync(sessionPath + "/messages", new { messageId = Guid.NewGuid(), text = "Continue this owned task",
                    expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = saved.Snapshot.Revision });
                Assert.Equal(HttpStatusCode.BadRequest, unavailable.StatusCode);
                Assert.Single((await runs.ListTenantSessionRunsAsync(setup.Tenant, current.SessionId, null, 0, 100)).Items);
                var visible = await owner.PostAsJsonAsync(sessionPath + "/continue-in-chat", new { expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = saved.Snapshot.Revision });
                Assert.True(visible.IsSuccessStatusCode, await visible.Content.ReadAsStringAsync());
                var continued = (await sessions.FindTenantSessionAsync(setup.Tenant, current.SessionId))!;
                Assert.Equal(saved.Owner, continued.Owner); Assert.Equal(saved.Snapshot.Origin, continued.Snapshot.Origin);
                Assert.True(continued.Snapshot.Surfaces.HasFlag(SessionSurface.ChatList)); Assert.True(continued.Snapshot.Surfaces.HasFlag(SessionSurface.BackgroundWork));
                var response = await owner.PostAsJsonAsync($"/api/v2/tenants/{setup.Tenant}/sessions/{current.SessionId}/messages", new { messageId = Guid.NewGuid(), text = "Continue this owned task",
                    expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = continued.Snapshot.Revision });
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
                var next = await DriveOwnedProtocol(factory, setup.Tenant, (await Json(response)).GetProperty("runId").GetGuid());
                Assert.Equal(AgentRunStatus.Completed, next.Status); Assert.Equal(ActivationKind.UserTurn, next.Admission.Activation.Kind); Assert.Equal(current.SessionId, next.SessionId);
                Assert.Equal(1, model.Foreground); Assert.Equal(saved.Owner, (await sessions.FindTenantSessionAsync(setup.Tenant, current.SessionId))!.Owner);
                Assert.Equal(2, (await runs.ListTenantSessionRunsAsync(setup.Tenant, current.SessionId, null, 0, 100)).Items.Count);
                var after = (await Json(await owner.GetAsync(sessionPath + "/messages?limit=100"))).GetProperty("items").EnumerateArray().ToArray();
                Assert.Equal(assistants + 1, after.Count(e => string.Equals(e.GetProperty("role").GetString(), "assistant", StringComparison.OrdinalIgnoreCase)));
            }
            Assert.Equal(0, factory.Outbound);
        }
        finally { model.ReleaseWork.TrySetResult(); reopenedOwner?.Dispose(); if (reopened is not null) await reopened.DisposeAsync(); await first.DisposeAsync(); if (Directory.Exists(first.Root)) Directory.Delete(first.Root, true); }
    }
}
