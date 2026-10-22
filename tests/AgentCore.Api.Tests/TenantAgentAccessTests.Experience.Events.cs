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
            var inspected = value.TryGetProperty("tenantExperienceSourceRead", out var marker) && marker.ValueKind == JsonValueKind.True;
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
            yield return new ModelToolCallEvent(new("event-finish", ToolCatalog.WorkComplete, JsonSerializer.Serialize(new { outcome = inspected && !Record ? "NoAction" : "Response",
                summary = inspected ? Record ? "Recorded one inspected event source; other sources remain for later review." : "Inspected one source and found nothing useful to record." : "Source inspection was not authorized; no evidence was adopted.", attentionRequired = !inspected })));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
    public static TheoryData<bool, string> EventExperienceCases => new() { { false, "record" }, { true, "record" }, { false, "source-only" }, { true, "source-only" },
        { false, "review-approve" }, { true, "review-approve" }, { false, "review-reject" }, { true, "review-reject" }, { false, "source-denied" }, { true, "source-denied" }, { false, "event-revoked" }, { true, "event-revoked" } };
    [Theory(Timeout = 120000)] [MemberData(nameof(EventExperienceCases))]
    public async Task Tenant_event_Experience_retains_exact_source_coverage_only_after_authorized_durable_inspection(bool sqlite, string mode)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var model = new EventExperienceModel();
        await using var factory = new RuntimeStateFactory(sqlite, clock, model); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        // This fixture drives the actual durable processors explicitly. A fake-time
        // jump must not race a hosted dispatch before the scripted review is configured.
        foreach (var host in factory.Services.GetServices<IHostedService>().Where(s => s is AgentCore.Api.AgentRunHostedService or AgentCore.Api.TriggerSchedulerHostedService)) await host.StopAsync(CancellationToken.None);
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: false, automation: true, events: true); await MemoryExecutor(owner, tenant, instance, operations: true, runRead: mode != "source-denied");
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
        var processor = factory.Services.GetRequiredService<TenantEventProcessor>(); await processor.RunOnceAsync(); clock.Advance(TimeSpan.FromSeconds(61)); await processor.RunOnceAsync();
        var events = factory.Services.GetRequiredService<ITenantEventStore>(); var registrations = factory.Services.GetRequiredService<ITenantAutomationStore>();
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
        var rootRun = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, runId))!;
        var retainedOccurrence = (await registrations.FindTenantOccurrenceAsync(tenant, occurrence))!;
        var retainedBucket = (await events.FindTenantEventBucketAsync(tenant, bucket))!.Bucket;
        var bounded = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([new(ModelRole.Tool, AgentRunToolCallCheckpoint.FinishRequired, Name: TenantExperienceToolContract.Source, ToolCallId: "bounded")]), 1, 100, 1000);
        Assert.Empty(TenantEventExperienceCoverageRules.Acknowledged(rootRun, retainedOccurrence, retainedBucket, bounded));
        Assert.Throws<AccessDeniedException>(() => TenantEventExperienceCoverageRules.Acknowledged(rootRun, retainedOccurrence, retainedBucket with { ReservedOccurrenceId = Guid.NewGuid() }, bounded));
        if (mode.StartsWith("review-", StringComparison.Ordinal))
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/agent-runs.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
        if (mode == "event-revoked") Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync(api + $"/role-assignments/{assignment.GetProperty("assignmentId").GetGuid()}?expectedRevision={await Revision(owner, tenant)}")).StatusCode);
        model.Source = first.Run.AgentRunId; model.Review = true; model.Record = mode == "record"; model.Hold = mode == "source-only";
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); AgentRun? observed = null;
        for (var pass = 0; pass < 600; pass++) { observed = await runs.FindTenantRunAsync(tenant, runId); if (observed!.IsTerminal || observed.Status == AgentRunStatus.WaitingForApproval || model.Observed.Task.IsCompleted && model.Hold) break; await Task.Delay(25); }
        Assert.NotNull(observed);
        if (mode.StartsWith("review-", StringComparison.Ordinal))
        {
            Assert.Equal(AgentRunStatus.WaitingForApproval, observed.Status); Assert.Equal(2, (await Json(await owner.GetAsync(coverage))).GetProperty("items").GetArrayLength());
            var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, observed.ApprovalRequestId!.Value))!;
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode == "review-approve" ? "approve" : "reject", actionHash = request.Operation.ActionHash,
                expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            try { observed = await ExperienceMaintenanceFinal(factory, tenant, runId); }
            catch (TimeoutException) {
                var chain = (await runs.FindTenantRunAsync(tenant, runId))!; if (chain.Result?.ContinuationRunId is { } continuation) chain = (await runs.FindTenantRunAsync(tenant, continuation))!;
                throw new InvalidOperationException($"Event review continuation: {chain.Status}; {chain.Failure?.Code}; {chain.Failure?.Summary}; diagnostics={JsonSerializer.Serialize(AgentRunToolCallCheckpoint.ReadExecutionState(chain.Checkpoint).FailedAttempts)}; model={string.Join(" | ", model.Trace.TakeLast(3))}");
            }
        }
        else if (model.Hold)
        {
            Assert.True(model.Observed.Task.IsCompleted); observed = (await runs.FindTenantRunAsync(tenant, runId))!; Assert.Equal(AgentRunStatus.Running, observed.Status);
            Assert.Single((await Json(await owner.GetAsync(coverage))).GetProperty("items").EnumerateArray());
            var remainingWitness = TenantExperienceSourceProjection.RunEvidence(second.Run, null).Witness;
            var proposedSources = TenantExperienceReferences.Sources(observed.Checkpoint).Append(new(tenant, remainingWitness)).ToArray();
            var staleCheckpoint = new AgentRunCheckpoint(AgentRunToolCallCheckpoint.Write([], executionState: new(TenantExperienceSourceReads: proposedSources)), 2, 100, 1000);
            await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => factory.Services.GetRequiredService<ITenantRuntimeStore>().ApplyTenantRuntimeCommandAsync(observed.Owner, runId,
                new AgentRunCommand.Checkpoint(observed.Revision + 5, clock.GetUtcNow(), observed.Claim!.Generation, staleCheckpoint, null), Guid.NewGuid()).AsTask());
            Assert.Single((await Json(await owner.GetAsync(coverage))).GetProperty("items").EnumerateArray());
            model.Release.TrySetResult(); observed = await ProfileTerminal(factory, tenant, runId);
        }
        else observed = await ProfileTerminal(factory, tenant, runId);
        var inspected = mode is "record" or "source-only" or "review-approve";
        var final = await Json(await owner.GetAsync(coverage)); Assert.Equal(inspected ? 1 : 2, final.GetProperty("items").GetArrayLength());
        if (inspected)
        {
            Assert.Equal(AgentRunStatus.Completed, observed.Status); Assert.Single(TenantExperienceReferences.Sources(observed.Checkpoint));
            Assert.Equal(second.Run.AgentRunId, final.GetProperty("items")[0].GetProperty("source").GetProperty("dataJson").GetString() is { } raw ? JsonDocument.Parse(raw).RootElement.GetProperty("agentRunId").GetGuid() : Guid.Empty);
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
