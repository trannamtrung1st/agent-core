using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using AgentCore.Domain.Triggers;
using AgentCore.Application.Triggers;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public Task Pinned_Space_and_Environment_Automation_retains_exact_consent_and_completes_the_Synthetic_worker(bool sqlite, bool environment, bool review) =>
        PinnedAutomationWorkflow(sqlite, environment, review);

    [Theory]
    [InlineData(false, "gateOff")]
    [InlineData(true, "gateOff")]
    [InlineData(false, "profileDeleted")]
    [InlineData(true, "profileDeleted")]
    [InlineData(false, "membershipRemoved")]
    [InlineData(true, "membershipRemoved")]
    [InlineData(false, "spaceDeleted")]
    [InlineData(true, "spaceDeleted")]
    [InlineData(false, "spaceReadRevoked")]
    [InlineData(true, "spaceReadRevoked")]
    [InlineData(false, "activateRevoked")]
    [InlineData(true, "activateRevoked")]
    [InlineData(false, "healthySuspension")]
    [InlineData(true, "healthySuspension")]
    public Task Invalid_pinned_Automation_context_suspends_once_without_fallback_or_false_suspension(bool sqlite, string failure) =>
        PinnedAutomationWorkflow(sqlite, true, false, failure);

    [Theory]
    [InlineData(false, "review:gateOff")] [InlineData(true, "review:gateOff")]
    [InlineData(false, "review:epoch")] [InlineData(true, "review:epoch")]
    [InlineData(false, "review:profileDeleted")] [InlineData(true, "review:profileDeleted")]
    [InlineData(false, "review:membershipRemoved")] [InlineData(true, "review:membershipRemoved")]
    [InlineData(false, "approved:gateOff")] [InlineData(true, "approved:gateOff")]
    [InlineData(false, "approved:membershipRemoved")] [InlineData(true, "approved:membershipRemoved")]
    public Task Pinned_Automation_consent_never_admits_changed_context_before_decision_or_dispatch(bool sqlite, string boundary) =>
        PinnedAutomationWorkflow(sqlite, true, true, boundary);

    private async Task PinnedAutomationWorkflow(bool sqlite, bool environment, bool review, string? failure = null)
    {
        var model = new OwnedAutomationModel(); var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new OAuthFactory(sqlite: sqlite, execute: true, model: model,
            servicesOverride: services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); });
        using var owner = ConnectedClient(factory);
        await factory.Services.GetServices<IHostedService>().OfType<AgentCore.Api.AgentRunHostedService>().Single().StopAsync(CancellationToken.None);
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 36,
            expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var role = await access.SaveRoleAsync(human, tenant, null, "Pinned occurrence executor", ["automations.run", "sessions.create", "agent-runs.create"], await Revision(owner, tenant));
        await access.AssignRoleAsync(human, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        var spaces = factory.Services.GetRequiredService<TenantSpaceService>();
        var shared = await spaces.CreateAsync(human, tenant, Guid.NewGuid(), "Pinned workroom", "", "Use the exact Shared workroom.", await Revision(owner, tenant));
        await spaces.SetMemberAsync(human, tenant, shared.SpaceId, instance, "Occurrence worker", "Use the captured task.", [], [], await Revision(owner, tenant), shared.Revision, 0);
        var contextRole = await access.SaveRoleAsync(human, tenant, null, "Exact pinned context", ["spaces.read", "environments.read", "environments.activate"], await Revision(owner, tenant));
        var contextGrant = await access.AssignRoleAsync(human, tenant, agent.PrincipalId, contextRole.RoleId, TenantSpaceService.Scope(tenant, shared.SpaceId), await Revision(owner, tenant));
        Guid? profile = null;
        if (environment)
        {
            shared = await spaces.ReadAsync(human, tenant, shared.SpaceId);
            shared = await spaces.UpdateAsync(human, tenant, shared.SpaceId, shared.Name, shared.Description, shared.Instructions, true, await Revision(owner, tenant), shared.Revision);
            profile = (await factory.Services.GetRequiredService<TenantEnvironmentService>().SaveAsync(human, tenant, shared.SpaceId, Guid.NewGuid(), "Exact profile", "", await Revision(owner, tenant), shared.Revision, 0)).EnvironmentId;
        }
        if (review) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/agent-runs.create",
            new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
        var route = api + $"/agents/{instance}/automations";
        var created = await owner.PostAsJsonAsync(route, new { enabled = true, name = "Pinned task", instructions = "Complete only this exact occurrence.",
            pinnedSpaceId = shared.SpaceId, pinnedEnvironmentId = profile, schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = clock.GetUtcNow().AddMinutes(2).ToString("O") },
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision, requiresTools = true });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var saved = await Json(created); var automation = saved.GetProperty("automationId").GetGuid();
        Assert.Equal(shared.SpaceId, saved.GetProperty("pinnedSpaceId").GetGuid());
        if (failure is not null && !failure.Contains(':'))
        {
            var store = factory.Services.GetRequiredService<ITenantAutomationStore>();
            var original = (await store.FindTenantAutomationAsync(tenant, automation))!;
            var beforeRuns = (await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Total;
            if (failure == "healthySuspension")
            {
                var state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(tenant))!; var at = TriggerScheduleCalculator.Truncate(clock.GetUtcNow());
                var forged = original.WithScheduleAdvance(AutomationStatus.SuspendedPolicy, original.NextOccurrenceAtUtc, original.OccurrenceCount, original.Revision + 1, at, "context:environment_unavailable");
                Assert.Equal("automation_context_available", (await Assert.ThrowsAsync<SecurityConflictException>(() => store.TryAdvanceTenantAutomationAsync(
                    state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, original, forged,
                    new(Guid.NewGuid(), tenant, agent.PrincipalId, "automations.schedule.advance", automation.ToString("D"), "allowed", "automation_context_suspended", at)).AsTask())).ReasonCode);
                Assert.True(TenantAutomationRules.Same(original, (await store.FindTenantAutomationAsync(tenant, automation))!)); return;
            }
            shared = await spaces.ReadAsync(human, tenant, shared.SpaceId);
            if (failure == "gateOff") await spaces.UpdateAsync(human, tenant, shared.SpaceId, shared.Name, shared.Description, shared.Instructions, false, await Revision(owner, tenant), shared.Revision);
            else if (failure == "profileDeleted") await factory.Services.GetRequiredService<TenantEnvironmentService>().DeleteAsync(human, tenant, shared.SpaceId, profile!.Value, await Revision(owner, tenant), shared.Revision, 1);
            else if (failure is "membershipRemoved" or "spaceDeleted")
            {
                await spaces.SetMemberAsync(human, tenant, shared.SpaceId, instance, "", "", [], [], await Revision(owner, tenant), shared.Revision, 1, remove: true);
                if (failure == "spaceDeleted") { shared = await spaces.ReadAsync(human, tenant, shared.SpaceId); await spaces.DeleteAsync(human, tenant, shared.SpaceId, await Revision(owner, tenant), shared.Revision); }
            }
            else if (failure == "spaceReadRevoked") await access.RevokeAssignmentAsync(human, tenant, contextGrant.AssignmentId, await Revision(owner, tenant));
            else if (failure == "activateRevoked") await access.SaveRoleAsync(human, tenant, contextRole.RoleId, contextRole.Name, ["spaces.read", "environments.read"], await Revision(owner, tenant));
            clock.Advance(TimeSpan.FromMinutes(3));
            var service = factory.Services.GetRequiredService<TenantAutomationService>(); Assert.Equal(0, await service.AdmitDueAsync());
            var suspended = (await store.FindTenantAutomationAsync(tenant, automation))!;
            Assert.Equal(AutomationStatus.SuspendedPolicy, suspended.Status); Assert.StartsWith("context:", suspended.SuspensionReason);
            Assert.Equal(original.PinnedSpaceId, suspended.PinnedSpaceId); Assert.Equal(original.PinnedEnvironmentId, suspended.PinnedEnvironmentId);
            Assert.Equal(original.OccurrenceCount, suspended.OccurrenceCount); Assert.Equal(original.NextOccurrenceAtUtc, suspended.NextOccurrenceAtUtc);
            Assert.Equal(beforeRuns, (await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Total);
            Assert.Equal(0, await service.AdmitDueAsync()); Assert.True(TenantAutomationRules.Same(suspended, (await store.FindTenantAutomationAsync(tenant, automation))!));
            Assert.Empty(model.Requests); Assert.Equal(0, factory.Outbound); return;
        }
        var submitted = await owner.PostAsJsonAsync(route + $"/{automation}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision, expectedAutomationRevision = 1 });
        Assert.True(submitted.StatusCode == (review ? HttpStatusCode.Accepted : HttpStatusCode.Created), await submitted.Content.ReadAsStringAsync());
        var result = await Json(submitted); Guid runId;
        if (review)
        {
            var requestId = result.GetProperty("approvalRequestId").GetGuid();
            var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, requestId))!;
            Assert.Equal(shared.SpaceId, request.Operation.SpaceContext!.SpaceId); Assert.Equal(profile, request.Operation.SpaceContext.EnvironmentId);
            var preview = await Json(await owner.GetAsync(api + $"/approvals/{requestId}"));
            var contextRows = preview.GetProperty("review").GetProperty("changes").EnumerateArray().ToDictionary(c => c.GetProperty("field").GetString()!, c => c.GetProperty("after").GetString());
            Assert.Contains("Pinned workroom", contextRows["admissionSpace"]); Assert.Contains(shared.SpaceId.ToString("D"), contextRows["admissionSpace"]);
            if (profile is not null) { Assert.Contains("Exact profile", contextRows["admissionEnvironment"]); Assert.Contains(profile.Value.ToString("D"), contextRows["admissionEnvironment"]); }
            else Assert.Equal("No Environment", contextRows["admissionEnvironment"]);
            var restored = JsonSerializer.Deserialize<TenantApprovalRequest>(JsonSerializer.Serialize(request))!;
            Assert.Equal(shared.SpaceId, restored.Operation.AutomationAdmission!.Current.PinnedSpaceId);
            Assert.Equal(profile, restored.Operation.AutomationAdmission.Current.PinnedEnvironmentId);
            Assert.Equal(request.Operation.ActionHash, TenantApprovalRules.Rebuild(restored.Operation).ActionHash);
            async Task ChangeConsentContext()
            {
                shared = await spaces.ReadAsync(human, tenant, shared.SpaceId);
                if (failure!.EndsWith("gateOff", StringComparison.Ordinal) || failure.EndsWith("epoch", StringComparison.Ordinal))
                {
                    shared = await spaces.UpdateAsync(human, tenant, shared.SpaceId, shared.Name, shared.Description, shared.Instructions, false, await Revision(owner, tenant), shared.Revision);
                    if (failure.EndsWith("epoch", StringComparison.Ordinal)) shared = await spaces.UpdateAsync(human, tenant, shared.SpaceId, shared.Name, shared.Description, shared.Instructions, true, await Revision(owner, tenant), shared.Revision);
                }
                else if (failure.EndsWith("profileDeleted", StringComparison.Ordinal))
                    await factory.Services.GetRequiredService<TenantEnvironmentService>().DeleteAsync(human, tenant, shared.SpaceId, profile!.Value, await Revision(owner, tenant), shared.Revision, 1);
                else await spaces.SetMemberAsync(human, tenant, shared.SpaceId, instance, "", "", [], [], await Revision(owner, tenant), shared.Revision, 1, remove: true);
            }
            if (failure?.StartsWith("review:", StringComparison.Ordinal) == true)
            {
                await ChangeConsentContext(); var beforeDecision = await Revision(owner, tenant);
                var refused = await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                    expectedRevision = beforeDecision, expectedRequestRevision = request.Revision });
                Assert.True(refused.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict, await refused.Content.ReadAsStringAsync());
                Assert.Equal(beforeDecision, await Revision(owner, tenant));
                Assert.Equal(TenantApprovalStatus.Pending, (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(tenant, requestId))!.Status);
                Assert.Null(await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, request.RunId));
                Assert.Equal(0, await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync());
                Assert.Empty(model.Requests); Assert.Equal(0, factory.Outbound); return;
            }
            var decided = await owner.PostAsJsonAsync(api + $"/approvals/{requestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision });
            Assert.True(decided.IsSuccessStatusCode, await decided.Content.ReadAsStringAsync());
            if (failure?.StartsWith("approved:", StringComparison.Ordinal) == true)
            {
                await ChangeConsentContext(); clock.Advance(TimeSpan.FromSeconds(1));
                await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
                Assert.Null(await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, request.RunId));
                var consentOccurrence = (await factory.Services.GetRequiredService<ITenantAutomationStore>().FindTenantOccurrenceAsync(tenant, request.Operation.AutomationAdmission!.Occurrence.OccurrenceId))!;
                Assert.Equal(OccurrenceRoutingDisposition.Rejected, consentOccurrence.Disposition);
                Assert.Equal(shared.SpaceId, consentOccurrence.PinnedSpaceId); Assert.Equal(profile, consentOccurrence.PinnedEnvironmentId);
                Assert.Equal(0, await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync());
                Assert.Empty(model.Requests); Assert.Equal(0, factory.Outbound); return;
            }
            clock.Advance(TimeSpan.FromSeconds(1));
            await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync(); runId = request.RunId;
        }
        else runId = result.GetProperty("runId").GetGuid();
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var execution = factory.Services.GetRequiredService<ITenantRunExecution>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); AgentRun run;
        do
        {
            await execution.ExecuteRunnableAsync(8, deadline.Token);
            run = (await runs.FindTenantRunAsync(tenant, runId, deadline.Token))!;
            if (!run.IsTerminal) await Task.Delay(20, deadline.Token);
        } while (!run.IsTerminal);
        Assert.True(run.Status == AgentRunStatus.Completed, $"{run.Status}: {run.Failure?.Code} {run.Failure?.Summary}");
        var capture = run.Admission.Configuration!.SpaceContext!; Assert.Equal(shared.SpaceId, capture.SpaceId); Assert.Equal(profile, capture.EnvironmentId);
        var snapshot = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, run.SessionId))!.Snapshot;
        Assert.True(capture.MatchesSelection(snapshot.ContextSelection));
        var occurrence = (await factory.Services.GetRequiredService<ITenantAutomationStore>().FindTenantOccurrenceAsync(tenant, run.Admission.Activation.TriggerOccurrenceId!.Value))!;
        Assert.Equal(shared.SpaceId, occurrence.PinnedSpaceId); Assert.Equal(profile, occurrence.PinnedEnvironmentId);
        Assert.NotEmpty(model.Requests); Assert.Equal(0, factory.Outbound);
    }
}
