using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Hosting;

namespace AgentCore.Api.Tests;
public sealed partial class TenantAgentAccessTests
{
    private sealed class EventExperienceModel : ILanguageModel
    {
        public ExperienceModel Seed { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<string> Trace { get; } = new();
        public ModelCapabilities Capabilities => Seed.Capabilities;
        public Guid Source; public bool Review, Record, Hold;
        public int FailAfterConsent, Failures; public bool RepeatRejected;
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!Review) { await foreach (var item in Seed.GenerateAsync(request, ct)) yield return item; yield break; }
            Trace.Enqueue(string.Join(";", request.Messages.Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + (m.Text.Contains("tenantExperienceSourceRead", StringComparison.Ordinal) ? "source-witness" : m.Text.Length < 500 ? m.Text : "bounded-observation"))));
            var args = JsonSerializer.Serialize(new { sourceKind = "AgentRun", sourceId = Source });
            var read = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == TenantExperienceToolContract.Source);
            if (read is null) { yield return new ModelToolCallEvent(new("event-source", TenantExperienceToolContract.Source, args)); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break; }
            using var direct = JsonDocument.Parse(read.Text); var value = direct.RootElement;
            if (value.TryGetProperty("decision", out var decision) && decision.GetString() == "requiresApproval")
            {
                var consent = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization);
                if (consent is null) { yield return new ModelToolCallEvent(new("event-source-consent", ToolCatalog.RequestAuthorization,
                    JsonSerializer.Serialize(new { operation = "inspect_experience_source", experience = new { tool = TenantExperienceToolContract.Source, arguments = JsonSerializer.Deserialize<JsonElement>(args) } }))); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break; }
                using var approved = JsonDocument.Parse(consent.Text);
                value = approved.RootElement.TryGetProperty("experienceSourceObservation", out var observation) ? observation.Clone() : approved.RootElement.Clone();
            }
            if (RepeatRejected && request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization)
                && !request.Messages.Any(m => m.ToolCallId == "repeat-rejected-source"))
            {
                yield return new ModelToolCallEvent(new("repeat-rejected-source", ToolCatalog.RequestAuthorization,
                    JsonSerializer.Serialize(new { operation = "inspect_experience_source", experience = new { tool = TenantExperienceToolContract.Source, arguments = JsonSerializer.Deserialize<JsonElement>(args) } })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization)
                && Interlocked.Exchange(ref FailAfterConsent, 0) == 1)
            {
                Interlocked.Increment(ref Failures);
                yield return new ModelFailed(new(ProviderErrorCode.Unavailable, "Controlled transient after exact consent settlement.", TimeSpan.FromSeconds(2)));
                yield break;
            }
            var inspected = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("tenantExperienceSourceRead", out var marker) && marker.ValueKind == JsonValueKind.True;
            if (inspected)
            {
                Observed.TrySetResult(); if (Hold) await Release.Task.WaitAsync(ct);
                if (Record && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == TenantExperienceToolContract.Record))
                {
                    yield return new ModelToolCallEvent(new("event-record", TenantExperienceToolContract.Record, JsonSerializer.Serialize(new { sourceKind = "AgentRun", sourceId = Source,
                        throughCursor = value.GetProperty("throughCursor").GetInt64(), goal = "Owned event source observation", attempts = new[] { "Read the exact represented Run" },
                        decisions = Array.Empty<string>(), outcomes = new[] { "Observed actual retained outcome" }, corrections = Array.Empty<string>(), unresolved = Array.Empty<string>(), difficulties = Array.Empty<string>(), lessons = new[] { "Keep every source independently accounted for" } })));
                    yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
                }
            }
            yield return new ModelToolCallEvent(new("event-finish", ToolCatalog.WorkComplete, JsonSerializer.Serialize(new { outcome = !inspected ? "NeedsAttention" : !Record ? "NoAction" : "Response",
                summary = inspected ? Record ? "Recorded one inspected event source; other sources remain for later review." : "Inspected one source and found nothing useful to record." : "Source inspection was not authorized; no evidence was adopted.", attentionRequired = !inspected })));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
    public static TheoryData<bool, string> EventExperienceCases => new() { { false, "record" }, { true, "record" }, { false, "source-only" }, { true, "source-only" },
        { false, "review-approve" }, { true, "review-approve" }, { false, "review-reject" }, { true, "review-reject" },
        { false, "review-approve-retry" }, { true, "review-approve-retry" }, { false, "review-reject-retry" }, { true, "review-reject-retry" },
        { true, "review-approve-restart" }, { true, "review-reject-restart" }, { false, "review-reject-changed" }, { true, "review-reject-changed" }, { false, "source-denied" }, { true, "source-denied" }, { false, "event-revoked" }, { true, "event-revoked" } };
    [Theory(Timeout = 120000)] [MemberData(nameof(EventExperienceCases))]
    public async Task Tenant_event_Experience_retains_exact_source_coverage_only_after_authorized_durable_inspection(bool sqlite, string mode)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var model = new EventExperienceModel(); TenantApprovalRequest? consentRequest = null;
        await using var factory = new RuntimeStateFactory(sqlite, clock, model);
        await using var recovery = mode.EndsWith("-restart", StringComparison.Ordinal) ? new RuntimeStateFactory(true, clock, model, factory.Root) : null;
        var active = factory;
        using var initialOwner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        HttpClient owner = initialOwner;
        // This fixture drives the actual durable processors explicitly. A fake-time
        // jump must not race a hosted dispatch before the scripted review is configured.
        foreach (var host in active.Services.GetServices<IHostedService>().Where(s => s is AgentCore.Api.AgentRunHostedService or AgentCore.Api.TriggerSchedulerHostedService)) await host.StopAsync(CancellationToken.None);
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: false, automation: true, events: true, automationReads: mode == "source-only"); await MemoryExecutor(owner, tenant, instance, operations: true, runRead: mode != "source-denied");
        await ExperienceAutomationRights(owner, tenant, instance); var revision = await ExperienceOptIn(owner, tenant, instance, true);
        var api = $"/api/v2/tenants/{tenant}"; var route = api + $"/agents/{instance}/automations";
        var principal = (await Json(await owner.GetAsync(api + $"/agents/{instance}"))).GetProperty("principalId").GetGuid();
        var role = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Event source reads", permissions = new[] { "core-events.read" }, expectedRevision = await Revision(owner, tenant) }));
        var assignment = await Json(await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = principal, roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        var registered = await Json(await owner.PostAsJsonAsync(route, new { enabled = true, name = "Grouped actual Run review", instructions = "Inspect represented owned Run sources and retain only useful observations.", requiresTools = true,
            triggers = new[] { new { kind = "event", enabled = true, source = new { kind = "builtin", key = "run.completed" }, filterExpression = "event.data.activationKind === 'UserTurn' && event.data.outcomeKind !== 'NoAction'", dispatch = new { mode = "coalesceLatest", windowSeconds = 60 } } },
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = revision }));
        var automation = registered.GetProperty("automationId").GetGuid();
        var first = await MemoryRequest(factory, owner, tenant, instance, instanceRevision: revision); var second = await MemoryRequest(factory, owner, tenant, instance, instanceRevision: revision);
        var processor = active.Services.GetRequiredService<TenantEventProcessor>(); await processor.RunOnceAsync(); clock.Advance(TimeSpan.FromSeconds(61)); await processor.RunOnceAsync();
        var events = active.Services.GetRequiredService<ITenantEventStore>(); var registrations = active.Services.GetRequiredService<ITenantAutomationStore>();
        var rows = await events.ListTenantEventActivityAsync(tenant, instance, automation, new(null, null, null), 0, 20);
        Assert.Equal(2, rows.Total); var bucket = Assert.Single(rows.Items.Select(i => i.Bucket!.Bucket.BucketId).Distinct());
        var occurrence = Assert.Single(rows.Items.Select(i => i.Occurrence!.OccurrenceId).Distinct()); var runId = (await registrations.FindTenantOccurrenceAsync(tenant, occurrence))!.AcceptedAgentRunId!.Value;
        var coverage = route + $"/{automation}/unreviewed-sources"; var pending = await Json(await owner.GetAsync(coverage)); Assert.Equal(2, pending.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(coverage + "?limit=0")).StatusCode); Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(coverage + "?cursor=bad")).StatusCode);
        var one = await Json(await owner.GetAsync(coverage + "?limit=1")); Assert.Single(one.GetProperty("items").EnumerateArray());
        var tail = await Json(await owner.GetAsync(coverage + "?limit=1&cursor=" + Uri.EscapeDataString(one.GetProperty("nextCursor").GetString()!)));
        Assert.Single(tail.GetProperty("items").EnumerateArray()); Assert.NotEqual(one.GetProperty("items")[0].GetProperty("source").GetProperty("eventId").GetGuid(), tail.GetProperty("items")[0].GetProperty("source").GetProperty("eventId").GetGuid());
        using (var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") })) Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(coverage)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(coverage.Replace(instance.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal))).StatusCode);
        var eventId = rows.Items[0].Delivery.EventId;
        var scoped = await events.ListTenantEventCoverageAsync(tenant, instance, automation, new([eventId], []), null, 24); Assert.Single(scoped.Items); Assert.Equal(eventId, scoped.Items[0].Source.EventId);
        Assert.Empty((await events.ListTenantEventCoverageAsync(tenant, instance, automation, new([], []), null, 24)).Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => events.ListTenantEventCoverageAsync(Guid.NewGuid(), instance, automation, new(null, null), null, 24).AsTask());
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(coverage.Replace(tenant.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal))).StatusCode);
        var rootRun = (await active.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, runId))!;
        var retainedOccurrence = (await registrations.FindTenantOccurrenceAsync(tenant, occurrence))!;
        var retainedBucket = (await events.FindTenantEventBucketAsync(tenant, bucket))!.Bucket;
        var bounded = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.Tool, AgentRunToolCallCheckpoint.FinishRequired, Name: TenantExperienceToolContract.Source, ToolCallId: "bounded")]), 1, 100, 1000);
        Assert.Empty(TenantEventExperienceCoverageRules.Acknowledged(rootRun, retainedOccurrence, retainedBucket, bounded));
        Assert.Throws<AccessDeniedException>(() => TenantEventExperienceCoverageRules.Acknowledged(rootRun, retainedOccurrence, retainedBucket with { ReservedOccurrenceId = Guid.NewGuid() }, bounded));
        if (mode.StartsWith("review-", StringComparison.Ordinal))
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/agent-runs.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
        if (mode == "event-revoked") Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(api + $"/role-assignments/{assignment.GetProperty("assignmentId").GetGuid()}?expectedRevision={await Revision(owner, tenant)}")).StatusCode);
        model.RepeatRejected = mode == "review-reject-changed";
        var expectRetry = mode.EndsWith("-retry", StringComparison.Ordinal) || recovery is not null;
        model.FailAfterConsent = expectRetry ? 1 : 0;
        model.Source = first.Run.AgentRunId; model.Review = true; model.Record = mode == "record"; model.Hold = mode == "source-only";
        await active.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        var runs = active.Services.GetRequiredService<ITenantRunStore>(); AgentRun? observed = null;
        for (var pass = 0; pass < 600; pass++) { observed = await runs.FindTenantRunAsync(tenant, runId); if (observed!.IsTerminal || observed.Status == AgentRunStatus.WaitingForApproval || model.Observed.Task.IsCompleted && model.Hold) break; await Task.Delay(25); }
        Assert.NotNull(observed);
        if (mode.StartsWith("review-", StringComparison.Ordinal))
        {
            Assert.Equal(AgentRunStatus.WaitingForApproval, observed.Status); Assert.Equal(2, (await Json(await owner.GetAsync(coverage))).GetProperty("items").GetArrayLength());
            var request = (await active.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, observed.ApprovalRequestId!.Value))!;
            consentRequest = request;
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode.StartsWith("review-approve", StringComparison.Ordinal) ? "approve" : "reject", actionHash = request.Operation.ActionHash,
                expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            if (model.RepeatRejected)
            {
                var agent = (await active.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
                Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/agents/{instance}/persona", new { persona = agent.Instance.Persona with { Name = "Changed after exact refusal" },
                    expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision, expectedPersonaRevision = agent.Instance.PersonaRevision })).StatusCode);
            }
            AgentRun? knownProviderRetry = null;
            // The model consumes the fault flag; it cannot decide whether this fixture advances time.
            if (expectRetry)
            {
                await active.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
                var source = (await runs.FindTenantRunAsync(tenant, runId))!;
                var successorId = source.Result!.ContinuationRunId!.Value;
                await active.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                AgentRun retry;
                while (true)
                {
                    retry = (await runs.FindTenantRunAsync(tenant, successorId, deadline.Token))!;
                    if (retry.Status == AgentRunStatus.WaitingToRetry || retry.IsTerminal) break;
                    await Task.Delay(25, deadline.Token);
                }
                Assert.Equal(AgentRunStatus.WaitingToRetry, retry.Status); Assert.Equal(1, model.Failures);
                knownProviderRetry = retry;
                Assert.NotNull(retry.NextRetryAtUtc); Assert.True(retry.NextRetryAtUtc > clock.GetUtcNow());
                Assert.Null(retry.Claim); Assert.Equal(source.ResponseId, retry.ResponseId);
                Assert.Equal(source.Admission.Activation.RootActivationId, retry.Admission.Activation.RootActivationId);
                AgentRunToolCallCheckpoint.TryRead(retry.Checkpoint, out var retained);
                Assert.Single(retained!, message => message.Role == ModelRole.Tool && message.Name == ToolCatalog.RequestAuthorization);
                var traceCount = model.Trace.Count;
                await active.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
                Assert.Equal(retry.Revision, (await runs.FindTenantRunAsync(tenant, successorId))!.Revision); Assert.Equal(traceCount, model.Trace.Count);
                Assert.Equal(mode.StartsWith("review-approve", StringComparison.Ordinal) ? 1 : 2, (await Json(await owner.GetAsync(coverage))).GetProperty("items").GetArrayLength());
                if (recovery is not null)
                {
                    await factory.DisposeAsync(); active = recovery;
                    owner = recovery.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
                    await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                    foreach (var host in active.Services.GetServices<IHostedService>().Where(s => s is AgentCore.Api.AgentRunHostedService or AgentCore.Api.TriggerSchedulerHostedService)) await host.StopAsync(CancellationToken.None);
                    runs = active.Services.GetRequiredService<ITenantRunStore>();
                    var restored = (await runs.FindTenantRunAsync(tenant, successorId))!;
                    Assert.Equal(AgentRunStatus.WaitingToRetry, restored.Status); Assert.Equal(retry.Revision, restored.Revision);
                    Assert.Equal(JsonSerializer.Serialize(retry.Checkpoint), JsonSerializer.Serialize(restored.Checkpoint)); Assert.Equal(retry.ResponseId, restored.ResponseId);
                    Assert.Equal(retry.NextRetryAtUtc, restored.NextRetryAtUtc);
                }
                clock.Advance(retry.NextRetryAtUtc!.Value - clock.GetUtcNow() + TimeSpan.FromMilliseconds(1));
                // Exercise a persisted unaccepted dispatch deterministically, including
                // its retained failure, before the ordinary worker resumes this retry.
                var runtimeStore = active.Services.GetRequiredService<ITenantRuntimeStore>();
                var generation = Guid.NewGuid(); var at = clock.GetUtcNow();
                var claimed = await runtimeStore.ApplyTenantRuntimeCommandAsync(retry.Owner, successorId,
                    new AgentCore.Application.Execution.AgentRunCommand.Claim(retry.Revision, at, generation, at.AddMinutes(1)), Guid.NewGuid());
                var deferred = await runtimeStore.ApplyTenantRuntimeCommandAsync(claimed.Owner, successorId,
                    new AgentCore.Application.Execution.AgentRunCommand.DeferApprovalDispatch(claimed.Revision, at, generation, at.AddSeconds(5)), Guid.NewGuid());
                Assert.True(deferred.Checkpoint!.AwaitingRuntimeAdmission);
                Assert.Equal(JsonSerializer.Serialize(retry.Failure), JsonSerializer.Serialize(deferred.Failure));
            }
            try { observed = await ExperienceMaintenanceFinal(active, tenant, runId, knownProviderRetry); }

            catch (TimeoutException) {
                var chain = (await runs.FindTenantRunAsync(tenant, runId))!; if (chain.Result?.ContinuationRunId is { } continuation) chain = (await runs.FindTenantRunAsync(tenant, continuation))!;
                throw new InvalidOperationException($"Event review continuation: {chain.Status}; {chain.Failure?.Code}; {chain.Failure?.Summary}; diagnostics={JsonSerializer.Serialize(AgentRunToolCallCheckpoint.ReadExecutionState(chain.Checkpoint).FailedAttempts)}; model={string.Join(" | ", model.Trace.TakeLast(3))}");
            }
        }
        else if (model.Hold)
        {
            Assert.True(model.Observed.Task.IsCompleted); observed = (await runs.FindTenantRunAsync(tenant, runId))!; Assert.Equal(AgentRunStatus.Running, observed.Status);
            Assert.Single((await Json(await owner.GetAsync(coverage))).GetProperty("items").EnumerateArray());
            var metadataRole = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Owned Automation metadata", permissions = new[] { "automations.read" }, expectedRevision = await Revision(owner, tenant) }));
            await Json(await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = principal, roleId = metadataRole.GetProperty("roleId").GetGuid(), resourceType = "automation", resourceId = automation, expectedRevision = await Revision(owner, tenant) }));
            var reads = active.Services.GetRequiredService<TenantAutomationReadService>(); var readReference = new TenantRunReference(tenant, runId);
            var readCall = new ModelToolCall("owned-event-metadata", ToolCatalog.AutomationInspect, JsonSerializer.Serialize(new { automationId = automation }));
            var frame = new TenantRuntimeFrame(observed.Owner, observed.SessionId, runId, observed.Claim!.Generation);
            var metadata = await reads.PrepareAsync(readReference, frame.Generation, readCall.Name, readCall.ArgumentsJson, CancellationToken.None);
            var eventSource = Assert.Single(TenantAutomationReadRules.Events(metadata.Value)); Assert.Equal(TriggerSourceKind.CoreEvent, eventSource.Kind);
            using var savedMetadata = JsonDocument.Parse(metadata.Value.ObservationJson);
            Assert.Equal(second.Run.AgentRunId, Assert.Single(savedMetadata.RootElement.GetProperty("coverage").GetProperty("items").EnumerateArray()).GetProperty("sourceRunId").GetGuid());
            var proof = TenantAutomationReadService.Reference(metadata.Value, tenant);
            await active.Services.GetRequiredService<ITenantAutomationReadStore>().RequireTenantAutomationReadSourcesAsync(readReference, [proof], frame.Generation);
            Assert.DoesNotContain("automationReadSource", await reads.ExecuteAsync(frame, readCall, 512, CancellationToken.None));
            Assert.Single((await Json(await owner.GetAsync(coverage))).GetProperty("items").EnumerateArray());
            Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(api + $"/role-assignments/{assignment.GetProperty("assignmentId").GetGuid()}?expectedRevision={await Revision(owner, tenant)}")).StatusCode);
            await Assert.ThrowsAsync<AccessDeniedException>(() => active.Services.GetRequiredService<ITenantAutomationReadStore>().RequireTenantAutomationReadSourcesAsync(readReference, [proof], null).AsTask());
            await Json(await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = principal, roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
            await Json(await owner.PutAsJsonAsync(api + "/operation-policies/automations.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 }));
            var withheld = await reads.ExecuteAsync(frame, readCall, 16000, CancellationToken.None); Assert.Contains("requiresApproval", withheld); Assert.DoesNotContain("sourceRunId", withheld);
            var remainingWitness = TenantExperienceSourceProjection.RunEvidence(second.Run, null).Witness;
            var proposedSources = TenantExperienceReferences.Sources(observed.Checkpoint).Append(new(tenant, remainingWitness)).ToArray();
            var staleCheckpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([], executionState: new(TenantExperienceSourceReads: proposedSources)), 2, 100, 1000);
            await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => active.Services.GetRequiredService<ITenantRuntimeStore>().ApplyTenantRuntimeCommandAsync(observed.Owner, runId,
                new AgentRunCommand.Checkpoint(observed.Revision + 5, clock.GetUtcNow(), observed.Claim!.Generation, staleCheckpoint, null), Guid.NewGuid()).AsTask());
            Assert.Single((await Json(await owner.GetAsync(coverage))).GetProperty("items").EnumerateArray());
            model.Release.TrySetResult(); observed = await ProfileTerminal(active, tenant, runId);
        }
        else observed = await ProfileTerminal(active, tenant, runId);
        var inspected = mode is "record" or "source-only" || mode.StartsWith("review-approve", StringComparison.Ordinal);
        var final = await Json(await owner.GetAsync(coverage)); Assert.Equal(inspected ? 1 : 2, final.GetProperty("items").GetArrayLength());
        if (inspected)
        {
            Assert.Equal(AgentRunStatus.Completed, observed.Status); Assert.Single(TenantExperienceReferences.Sources(observed.Checkpoint));
            Assert.Equal(second.Run.AgentRunId, final.GetProperty("items")[0].GetProperty("source").GetProperty("dataJson").GetString() is { } raw ? JsonDocument.Parse(raw).RootElement.GetProperty("agentRunId").GetGuid() : Guid.Empty);
        }
        if (expectRetry)
        {
            Assert.True(observed.Status == AgentRunStatus.Completed, JsonSerializer.Serialize(new { observed.Status, observed.Failure, Attempts = AgentRunToolCallCheckpoint.ReadExecutionState(observed.Checkpoint).FailedAttempts, Trace = model.Trace.TakeLast(5) })); Assert.Equal(2, observed.AttemptCount); Assert.Equal(1, model.Failures);
            var retainedApproval = (await active.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, consentRequest!.RequestId))!;
            Assert.Equal(inspected ? TenantApprovalStatus.Approved : TenantApprovalStatus.Rejected, retainedApproval.Status); Assert.Equal(2, retainedApproval.Revision);
            Assert.Equal(consentRequest.RequestId, (await active.Services.GetRequiredService<ITenantApprovalStore>().FindRootApprovalAsync(tenant, consentRequest.RootActivationId, consentRequest.ExecutorPrincipalId, consentRequest.Operation))!.RequestId);
            var resumed = AgentRunToolCallCheckpoint.TryRead(observed.Checkpoint, out var messages);
            Assert.True(resumed); Assert.Single(messages!, message => message.Role == ModelRole.Tool && message.Name == ToolCatalog.RequestAuthorization);
            Assert.Equal(inspected ? 1 : 0, TenantExperienceReferences.Sources(observed.Checkpoint).Count);
        }
        if (mode.StartsWith("review-reject", StringComparison.Ordinal))
        {
            Assert.True(observed.Status == AgentRunStatus.Completed, JsonSerializer.Serialize(observed.Failure));
            if (model.RepeatRejected)
            {
                Assert.True(AgentRunToolCallCheckpoint.TryRead(observed.Checkpoint, out var refused));
                Assert.Contains(refused!, message => message.ToolCallId == "repeat-rejected-source" && message.Text.Contains("previously_rejected", StringComparison.Ordinal));
                Assert.Equal(consentRequest!.RequestId, (await active.Services.GetRequiredService<ITenantApprovalStore>().FindRootApprovalAsync(tenant, consentRequest.RootActivationId, consentRequest.ExecutorPrincipalId, consentRequest.Operation))!.RequestId);
            }
        }
        var facts = await Json(await owner.GetAsync(api + $"/agents/{instance}/experience")); Assert.Equal(mode == "record" ? 1 : 0, facts.GetProperty("items").GetArrayLength());
        if (mode == "record") Assert.Equal("Instance", facts.GetProperty("items")[0].GetProperty("scope").GetString());
        var stored = await runs.FindTenantRunAsync(tenant, runId); Assert.Equal(ExecutionOrigin.CoreEvent, stored!.Admission.ExecutionContext!.Origin); Assert.Null(stored.Admission.ExecutionContext.OriginalInitiatorPrincipalId); Assert.Null(stored.Owner.ProfileIdOrNull);
        if (sqlite && mode == "record")
        {
            await factory.DisposeAsync(); await using var reopened = new RuntimeStateFactory(true, clock, new EventExperienceModel(), factory.Root);
            using var again = reopened.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }); await Login(again, "owner@example.test", "explicit-synthetic-test-password");
            Assert.Single((await Json(await again.GetAsync(coverage))).GetProperty("items").EnumerateArray());
        }
    }
}
