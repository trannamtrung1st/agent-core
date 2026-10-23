using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Email;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    public static IEnumerable<object[]> TenantMailWatchCases => new[] { false, true }.SelectMany(sqlite => new[] { "review", "off", "coalesce", "pending-off", "accepted-off", "revoke-use", "disconnect", "lease", "refresh", "pause", "complete", "completion-revoke", "scoped-reader", "delete-account", "coalesce-none", "coalesce-second", "coalesce-all", "late-subscription", "remove-original" }.Select(mode => new object[] { sqlite, mode })).Concat(new[] { new object[] { true, "pending-reopen" }, new object[] { true, "accepted-reopen" } });
    [Theory] [MemberData(nameof(TenantMailWatchCases))]
    public async Task Tenant_email_watch_baselines_then_commits_owned_metadata_and_fences_unadmitted_watch_off(bool sqlite, string mode)
    {
        var turnOff = mode is "off" or "revoke-use" or "disconnect"; var coalesce = mode.StartsWith("coalesce", StringComparison.Ordinal);
        var withheld = turnOff || mode is "coalesce-all" or "remove-original";
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await using var factory = new OAuthFactory(sqlite: sqlite, preserve: mode.EndsWith("reopen", StringComparison.Ordinal), servicesOverride: services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); });
        using var owner = ConnectedClient(factory); var s = await ConnectedReadAccount(factory, owner, "EmailAccount", tools: true);
        foreach (var worker in factory.Services.GetServices<IHostedService>().Where(w => w is AgentCore.Api.AgentRunHostedService or AgentCore.Api.TriggerSchedulerHostedService or AgentCore.Api.EmailWatchHostedService)) await worker.StopAsync(CancellationToken.None);
        var api = $"/api/v2/tenants/{s.Tenant}"; var path = api + $"/credentials/{s.Credential}/monitoring";
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(s.Tenant, s.Instance))!;
        var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var role = await access.SaveRoleAsync(human, s.Tenant, null, "Mailbox event executor", ["automations.run", "sessions.create", "agent-runs.create", "core-events.read", "credentials.read", "credentials.use", "email.read"], await Revision(owner, s.Tenant));
        await access.AssignRoleAsync(human, s.Tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), await Revision(owner, s.Tenant));
        var registration = await owner.PostAsJsonAsync(api + $"/agents/{s.Instance}/automations", new { enabled = true, name = "Observe new mail", instructions = "Process only the observed account reference.", requiresTools = false,
            expectedRevision = await Revision(owner, s.Tenant), expectedInstanceRevision = agent.Instance.Revision, expectedAutomationRevision = 0,
            triggers = new[] { new { kind = "event", enabled = true, source = new { kind = "builtin", key = "email.message.received" }, filterExpression = "true", dispatch = new { mode = coalesce ? "coalesceLatest" : "everyMatch", windowSeconds = coalesce ? (int?)60 : null } } } });
        Assert.True(registration.IsSuccessStatusCode, await registration.Content.ReadAsStringAsync());
        var automationId = (await Json(registration)).GetProperty("automationId").GetGuid();
        var automation = (await factory.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(s.Tenant, automationId))!;
        var view = await Json(await owner.GetAsync(path)); Assert.Equal("Off", view.GetProperty("status").GetString());
        var saved = await owner.PutAsJsonAsync(path, new { expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedCredentialRevision = view.GetProperty("credentialRevision").GetInt64(), expectedWatchRevision = 0, enabled = true, intervalSeconds = 60 });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var watches = factory.Services.GetRequiredService<ITenantEmailWatchStore>(); var monitoring = factory.Services.GetRequiredService<TenantEmailMonitoringService>();
        var feed = factory.Services.GetRequiredService<TenantConnectedAccountReader>(); var events = factory.Services.GetRequiredService<ITenantEventStore>();
        feed.AddTenantSyntheticArrival(s.Tenant, s.Credential, new("historical", "thread", "private@example.test", [], [], clock.GetUtcNow(), "Private header", "Private body", false, []));
        if (mode == "lease")
        {
            var first = Assert.Single(await watches.ClaimDueTenantEmailWatchesAsync(clock.GetUtcNow(), 3));
            Assert.Empty(await watches.ClaimDueTenantEmailWatchesAsync(clock.GetUtcNow(), 3));
            clock.Advance(TimeSpan.FromSeconds(91)); var replacement = Assert.Single(await watches.ClaimDueTenantEmailWatchesAsync(clock.GetUtcNow(), 3));
            Assert.NotEqual(first.State.LeaseId, replacement.State.LeaseId);
            Assert.False(await watches.TryCommitTenantEmailWatchAsync(replacement with { State = replacement.State with { Cursor = new(EmailWatchCursorKind.Synthetic, Offset: 10) } }, new([], new(EmailWatchCursorKind.Synthetic, Offset: 10)), clock.GetUtcNow()));
            Assert.False(await watches.TryCommitTenantEmailWatchAsync(first, new([], new(EmailWatchCursorKind.Synthetic)), clock.GetUtcNow()));
            Assert.True(await watches.TryCommitTenantEmailWatchAsync(replacement, new([], new(EmailWatchCursorKind.Synthetic, Offset: 1)), clock.GetUtcNow()));
            Assert.False(await watches.TryCommitTenantEmailWatchAsync(replacement, new([], new(EmailWatchCursorKind.Synthetic, Offset: 1)), clock.GetUtcNow()));
            Assert.Empty((await events.ListTenantCoreEventsAsync(s.Tenant, s.Instance, "email.message.received", 0, 100)).Items); return;
        }
        Assert.Equal(1, await monitoring.RunOnceAsync());
        Assert.Empty((await events.ListTenantCoreEventsAsync(s.Tenant, s.Instance, "email.message.received", 0, 100)).Items);
        Assert.NotNull((await watches.ReadTenantEmailWatchAsync(s.Tenant, s.Credential))!.Cursor);
        if (mode is "refresh" or "pause")
        {
            clock.Advance(TimeSpan.FromSeconds(60)); var active = Assert.Single(await watches.ClaimDueTenantEmailWatchesAsync(clock.GetUtcNow(), 3));
            if (mode == "refresh")
            {
                var before = active.Account; Assert.True(await watches.TryMaintainTenantEmailWatchAsync(active, before.SecretRevision,
                    new(RotatedProtectedPayload: before.ProtectedPayload), clock.GetUtcNow()));
                var refreshed = (await factory.Services.GetRequiredService<ITenantCredentialStore>().FindTenantCredentialAsync(s.Tenant, s.Credential))!.Value;
                Assert.Equal(before.Revision, refreshed.Revision); Assert.Equal(before.SecretRevision + 1, refreshed.SecretRevision);
                Assert.NotNull(await watches.RecheckTenantEmailWatchAsync(active, clock.GetUtcNow()));
                Assert.True(await watches.TryCommitTenantEmailWatchAsync(active, new([], active.State.Cursor!), clock.GetUtcNow()));
            }
            else
            {
                await watches.RecordTenantEmailWatchFailureAsync(active, new("email_watch_access_required", Guid.NewGuid(), true), clock.GetUtcNow());
                Assert.True((await watches.ReadTenantEmailWatchAsync(s.Tenant, s.Credential))!.Paused);
                view = await Json(await owner.GetAsync(path));
                using var queued = await owner.PostAsJsonAsync(path + "/check", new { expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedCredentialRevision = view.GetProperty("credentialRevision").GetInt64(), expectedWatchRevision = view.GetProperty("configurationRevision").GetInt64() });
                Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode); Assert.Equal(1, await monitoring.RunOnceAsync()); Assert.False((await watches.ReadTenantEmailWatchAsync(s.Tenant, s.Credential))!.Paused);
            }
        }
        feed.AddTenantSyntheticArrival(s.Tenant, s.Credential, new("arrival", "thread", "private@example.test", [], [], clock.GetUtcNow(), "Private header", "Private body", false, []));
        clock.Advance(TimeSpan.FromSeconds(60)); Assert.Equal(1, await monitoring.RunOnceAsync());
        var fact = Assert.Single((await events.ListTenantCoreEventsAsync(s.Tenant, s.Instance, "email.message.received", 0, 100)).Items);
        Assert.Equal(TriggerOwner.ForTenant(s.Tenant, s.Instance, agent.PrincipalId), fact.Event.Owner);
        using (var metadata = JsonDocument.Parse(fact.Event.DataJson)) Assert.Equal(new[] { "accountId", "accountRef", "messageRef", "provider" }, metadata.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.DoesNotContain("Private", fact.Event.DataJson); Assert.DoesNotContain("private@", fact.Event.DataJson);
        Assert.True(await watches.CanDeliverTenantEmailEventAsync(s.Tenant, fact.Event.EventId));
        async Task Configure(bool enabled)
        {
            view = await Json(await owner.GetAsync(path));
            using var changed = await owner.PutAsJsonAsync(path, new { expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedCredentialRevision = view.GetProperty("credentialRevision").GetInt64(), expectedWatchRevision = view.GetProperty("configurationRevision").GetInt64(), enabled, intervalSeconds = 60 });
            Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());
        }
        if (turnOff)
        {
            if (mode == "off") await Configure(false);
            else if (mode == "disconnect")
            {
                using var disconnected = await owner.PostAsJsonAsync(api + $"/credentials/{s.Credential}/oauth/disconnect", new { expectedRevision = await Revision(owner, s.Tenant), expectedCredentialRevision = 2 });
                Assert.True(disconnected.IsSuccessStatusCode, await disconnected.Content.ReadAsStringAsync());
            }
            else await access.SaveRoleAsync(human, s.Tenant, role.RoleId, "Mailbox event executor", ["automations.run", "sessions.create", "agent-runs.create", "core-events.read", "credentials.read", "email.read"], await Revision(owner, s.Tenant));
            if (mode != "revoke-use") Assert.False(await watches.CanDeliverTenantEmailEventAsync(s.Tenant, fact.Event.EventId));
            if (mode == "off") Assert.True(await watches.CanReadRetainedTenantEmailEventAsync(s.Tenant, fact.Event.EventId));
        }
        Guid? secondId = null;
        if (coalesce)
        {
            await factory.Services.GetRequiredService<TenantEventProcessor>().RunOnceAsync();
            if (mode is not ("coalesce-none" or "coalesce-second")) await Configure(false);
            using var created = await owner.PostAsJsonAsync(api + "/credentials", new TenantCredentialCreateRequest("Independent second mailbox", "Password", "EmailAccount", await Revision(owner, s.Tenant), ProtectedValue: "synthetic-mail-secret",
                EmailAccount: new("Custom", "second@example.test", "second@example.test", "second@example.test", new("mail.example.test", 993, "ImplicitTls"), new("mail.example.test", 465, "ImplicitTls"))));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode); var secondAccount = (await Json(created)).GetProperty("credential").GetProperty("credentialId").GetGuid();
            var currentAgent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(s.Tenant, s.Instance))!;
            using var linked = await owner.PostAsJsonAsync(api + $"/agents/{s.Instance}/credential-bindings", new TenantBindCredentialRequest(secondAccount, "other-account", await Revision(owner, s.Tenant), currentAgent.Instance.Revision, 1));
            Assert.True(linked.IsSuccessStatusCode, await linked.Content.ReadAsStringAsync());
            var secondPath = api + $"/credentials/{secondAccount}/monitoring"; view = await Json(await owner.GetAsync(secondPath));
            using var enabled = await owner.PutAsJsonAsync(secondPath, new { expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedCredentialRevision = view.GetProperty("credentialRevision").GetInt64(), expectedWatchRevision = 0, enabled = true, intervalSeconds = 60 });
            Assert.True(enabled.IsSuccessStatusCode, await enabled.Content.ReadAsStringAsync()); Assert.Equal(1, await monitoring.RunOnceAsync());
            clock.Advance(TimeSpan.FromSeconds(10)); feed.AddTenantSyntheticArrival(s.Tenant, secondAccount, new("second-arrival", "thread", "private@example.test", [], [], clock.GetUtcNow(), "Private header", "Private body", false, []));
            view = await Json(await owner.GetAsync(secondPath));
            using var check = await owner.PostAsJsonAsync(secondPath + "/check", new { expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedCredentialRevision = view.GetProperty("credentialRevision").GetInt64(), expectedWatchRevision = view.GetProperty("configurationRevision").GetInt64() });
            Assert.Equal(HttpStatusCode.Accepted, check.StatusCode); Assert.Equal(1, await monitoring.RunOnceAsync());
            secondId = Assert.Single((await events.ListTenantCoreEventsAsync(s.Tenant, s.Instance, "email.message.received", 0, 100)).Items, e => e.Event.EventId != fact.Event.EventId).Event.EventId;
            await factory.Services.GetRequiredService<TenantEventProcessor>().RunOnceAsync();
            if (mode is "coalesce-second" or "coalesce-all")
            {
                view = await Json(await owner.GetAsync(secondPath));
                using var disabled = await owner.PutAsJsonAsync(secondPath, new { expectedRevision = view.GetProperty("securityRevision").GetInt64(),
                    expectedCredentialRevision = view.GetProperty("credentialRevision").GetInt64(), expectedWatchRevision = view.GetProperty("configurationRevision").GetInt64(), enabled = false, intervalSeconds = 60 });
                Assert.True(disabled.IsSuccessStatusCode, await disabled.Content.ReadAsStringAsync());
            }
            clock.Advance(TimeSpan.FromSeconds(50));
        }
        Automation? later = null;
        if (mode is "late-subscription" or "remove-original")
        {
            var currentAgent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(s.Tenant, s.Instance))!;
            using var added = await owner.PostAsJsonAsync(api + $"/agents/{s.Instance}/automations", new { enabled = true, name = "Later subscription", instructions = "Inspect only its own accepted mail events.", requiresTools = false,
                expectedRevision = await Revision(owner, s.Tenant), expectedInstanceRevision = currentAgent.Instance.Revision,
                triggers = new[] { new { kind = "event", enabled = true, source = new { kind = "builtin", key = "email.message.received" }, filterExpression = "true" } } });
            Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
            later = (await factory.Services.GetRequiredService<ITenantAutomationStore>().FindTenantAutomationAsync(s.Tenant, (await Json(added)).GetProperty("automationId").GetGuid()))!;
            if (mode == "remove-original")
            {
                using var removed = await owner.DeleteAsync(api + $"/agents/{s.Instance}/automations/{automationId}?expectedRevision={await Revision(owner, s.Tenant)}&expectedInstanceRevision={currentAgent.Instance.Revision}&expectedAutomationRevision={automation.Revision}");
                Assert.True(removed.IsSuccessStatusCode, await removed.Content.ReadAsStringAsync());
            }
        }
        await factory.Services.GetRequiredService<TenantEventProcessor>().RunOnceAsync();
        if (later is not null) Assert.Null(await events.FindTenantEventDeliveryAsync(s.Tenant, TriggerSourceKind.CoreEvent, fact.Event.EventId, later.Triggers[0].TriggerId));
        var retainedEmailIds = !coalesce ? new[] { fact.Event.EventId } : mode == "coalesce-none" ? new[] { fact.Event.EventId, secondId!.Value }
            : mode == "coalesce-second" ? new[] { fact.Event.EventId } : new[] { secondId!.Value };
        var delivery = (await events.FindTenantEventDeliveryAsync(s.Tenant, TriggerSourceKind.CoreEvent, fact.Event.EventId, automation.Triggers[0].TriggerId))!;
        if (!withheld)
        {
            Assert.Equal(coalesce ? EventMatchStatus.Coalesced : EventMatchStatus.AwaitingAdmission, delivery.Status);
            var request = Assert.Single((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(s.Tenant, null, null, new())).Items);
            Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Contains(request.Permission, new[] { "credentials.read", "credentials.use", "email.read" });
            Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
            Assert.Equal(retainedEmailIds.Order(), request.Operation.AutomationAdmission!.EventSource!.EventIds.Order());
            if (mode == "scoped-reader")
            {
                var identity = factory.Services.GetRequiredService<IdentityService>(); var account = await identity.RegisterAsync("mail-fact-reader@example.test", "Scoped source reader", "strong-mail-reader-password");
                var baseRole = await access.SaveRoleAsync(human, s.Tenant, null, "Mail fact prerequisites", ["agents.read", "automations.read", "automations.run", "approvals.read", "sessions.create", "agent-runs.create", "core-events.read"], await Revision(owner, s.Tenant));
                var invitation = await access.InviteAsync(human, s.Tenant, account.LoginKey, baseRole.RoleId, await Revision(owner, s.Tenant));
                var member = await access.AcceptInvitationAsync(account.AccountId, s.Tenant, invitation.Invitation.InvitationId, invitation.Token);
                using var reader = ConnectedClient(factory); await Login(reader, account.LoginKey, "strong-mail-reader-password");
                async Task Hidden(int expected)
                {
                    Assert.Equal(expected, (await Json(await reader.GetAsync(api + "/approvals?pageSize=1"))).GetProperty("total").GetInt32());
                    Assert.Equal(expected, (await Json(await reader.GetAsync(api + $"/agents/{s.Instance}/events/core/email.message.received?pageSize=1"))).GetProperty("total").GetInt32());
                    Assert.Equal(expected, (await Json(await reader.GetAsync(api + $"/agents/{s.Instance}/automations/{automationId}/events?pageSize=1"))).GetProperty("total").GetInt32());
                }
                await Hidden(0); Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(api + $"/events/core/{fact.Event.EventId}")).StatusCode);
                var rawRole = await access.SaveRoleAsync(human, s.Tenant, null, "One exact source account", ["credentials.read", "credentials.use", "email.read"], await Revision(owner, s.Tenant));
                await access.AssignRoleAsync(human, s.Tenant, member.PrincipalId, rawRole.RoleId, TenantCredentialRules.Scope(s.Tenant, s.Credential), await Revision(owner, s.Tenant));
                await Hidden(1); Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
            }
            if (mode == "pending-reopen")
            {
                var root = factory.Root; await factory.DisposeAsync();
                await using var reopened = new OAuthFactory(existingRoot: root, sqlite: true, servicesOverride: services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); });
                using var restartedOwner = ConnectedClient(reopened); await Login(restartedOwner, "owner@example.test", "explicit-synthetic-test-password");
                foreach (var worker in reopened.Services.GetServices<IHostedService>().Where(w => w is AgentCore.Api.AgentRunHostedService or AgentCore.Api.TriggerSchedulerHostedService or AgentCore.Api.EmailWatchHostedService)) await worker.StopAsync(CancellationToken.None);
                var retained = (await reopened.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(s.Tenant, request.RequestId))!;
                Assert.Equal(request.Operation.ActionHash, retained.Operation.ActionHash); Assert.Equal(TenantApprovalStatus.Pending, retained.Status);
                using var approved = await restartedOwner.PostAsJsonAsync(api + $"/approvals/{retained.RequestId}/decision", new { decision = "approve", actionHash = retained.Operation.ActionHash, expectedRevision = await Revision(restartedOwner, s.Tenant), expectedRequestRevision = retained.Revision });
                Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync()); await reopened.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
                var completed = Assert.Single((await reopened.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(s.Tenant, null, null, 0, 100, instanceId: s.Instance)).Items);
                Assert.Equal(request.Operation.AutomationAdmission!.RunId, completed.AgentRunId); await reopened.Services.GetRequiredService<TenantEventReadAuthority>().RequireAsync(completed, CancellationToken.None); Assert.Equal(0, reopened.Outbound); return;
            }
            if (mode == "pending-off")
            {
                await Configure(false);
                await Assert.ThrowsAsync<AccessDeniedException>(() => factory.Services.GetRequiredService<TenantAutomationOperationTarget>().RequireAsync(request, CancellationToken.None).AsTask());
                Assert.Empty((await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(s.Tenant, null, null, 0, 100, instanceId: s.Instance)).Items); return;
            }
            using var consent = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                expectedRevision = await Revision(owner, s.Tenant), expectedRequestRevision = request.Revision });
            Assert.True(consent.IsSuccessStatusCode, await consent.Content.ReadAsStringAsync());
            await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
            delivery = (await events.FindTenantEventDeliveryAsync(s.Tenant, TriggerSourceKind.CoreEvent, fact.Event.EventId, automation.Triggers[0].TriggerId))!;
        }
        // A coalesced delivery retains its original match; current admission is
        // represented by the bucket/occurrence and public Activity projection.
        Assert.Equal(coalesce ? EventMatchStatus.Coalesced : withheld ? EventMatchStatus.PolicySkipped : EventMatchStatus.Admitted, delivery.Status);
        Assert.Equal(withheld ? 0 : 1, (await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(s.Tenant, null, null, 0, 100, instanceId: s.Instance)).Total);
        if (withheld) Assert.Empty((await factory.Services.GetRequiredService<ITenantApprovalStore>().ListApprovalsAsync(s.Tenant, null, null, new())).Items);
        if (mode == "coalesce-all")
        {
            var skipped = await Json(await owner.GetAsync(api + $"/agents/{s.Instance}/automations/{automationId}/events"));
            Assert.Equal(2, skipped.GetProperty("total").GetInt32());
            foreach (var entry in skipped.GetProperty("items").EnumerateArray())
            {
                Assert.Equal("policySkipped", entry.GetProperty("status").GetString());
                Assert.Equal(2, entry.GetProperty("batchCount").GetInt32()); Assert.Equal(JsonValueKind.Null, entry.GetProperty("runId").ValueKind);
                var bucket = (await events.FindTenantEventBucketAsync(s.Tenant, entry.GetProperty("bucketId").GetGuid()))!.Bucket;
                Assert.True(bucket.Flushed); Assert.False(string.IsNullOrWhiteSpace(bucket.CompletionCode));
            }
        }
        if (!withheld)
        {
            var run = Assert.Single((await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(s.Tenant, null, null, 0, 100, instanceId: s.Instance)).Items);
            if (mode == "accepted-off") await Configure(false);
            if (mode == "accepted-reopen")
            {
                var root = factory.Root; await factory.DisposeAsync();
                await using var reopened = new OAuthFactory(existingRoot: root, sqlite: true, servicesOverride: services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); });
                using var restartedOwner = ConnectedClient(reopened); await Login(restartedOwner, "owner@example.test", "explicit-synthetic-test-password");
                foreach (var worker in reopened.Services.GetServices<IHostedService>().Where(w => w is AgentCore.Api.AgentRunHostedService or AgentCore.Api.TriggerSchedulerHostedService or AgentCore.Api.EmailWatchHostedService)) await worker.StopAsync(CancellationToken.None);
                var retained = Assert.Single((await reopened.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(s.Tenant, null, null, 0, 100, instanceId: s.Instance)).Items);
                Assert.Equal(run.AgentRunId, retained.AgentRunId); Assert.NotNull((await reopened.Services.GetRequiredService<ITenantEmailWatchStore>().ReadTenantEmailWatchAsync(s.Tenant, s.Credential))!.Cursor);
                await reopened.Services.GetRequiredService<TenantEventReadAuthority>().RequireAsync(retained, CancellationToken.None); Assert.Equal(0, reopened.Outbound); return;
            }
            await factory.Services.GetRequiredService<TenantEventReadAuthority>().RequireAsync(run, CancellationToken.None);
            if (mode is "complete" or "completion-revoke")
            {
                var runtime = factory.Services.GetRequiredService<ITenantRuntimeStore>();
                var claimed = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentCore.Application.Execution.AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), Guid.NewGuid(), clock.GetUtcNow().AddMinutes(5)), Guid.NewGuid());
                var finish = new AgentCore.Application.Execution.AgentRunCommand.Complete(claimed.Revision, clock.GetUtcNow(), claimed.Claim!.Generation, "Observed source completed", AgentCore.Domain.Conversation.AgentRunOutcomeKind.NoAction, null);
                if (mode == "completion-revoke")
                {
                    await access.SaveRoleAsync(human, s.Tenant, role.RoleId, "Mailbox event executor", ["automations.run", "sessions.create", "agent-runs.create", "core-events.read", "credentials.read", "email.read"], await Revision(owner, s.Tenant));
                    await Assert.ThrowsAsync<AccessDeniedException>(() => runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, finish, Guid.NewGuid()).AsTask());
                }
                else
                {
                    var completed = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, finish, Guid.NewGuid()); Assert.Equal(AgentCore.Domain.Conversation.AgentRunStatus.Completed, completed.Status);
                    Assert.Contains((await events.ListTenantCoreEventsAsync(s.Tenant, s.Instance, "run.completed", 0, 100)).Items, item => item.Event.RootAgentRunId == run.AgentRunId);
                }
            }
            if (mode == "delete-account")
            {
                var currentAgent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(s.Tenant, s.Instance))!;
                using var unlinked = await owner.DeleteAsync(api + $"/agents/{s.Instance}/credential-bindings/{s.Binding}?expectedRevision={await Revision(owner, s.Tenant)}&expectedInstanceRevision={currentAgent.Instance.Revision}&expectedBindingRevision=1");
                Assert.True(unlinked.IsSuccessStatusCode, await unlinked.Content.ReadAsStringAsync());
                await Assert.ThrowsAsync<AccessDeniedException>(() => factory.Services.GetRequiredService<TenantEventReadAuthority>().RequireAsync(run, CancellationToken.None).AsTask());
                using var deleted = await owner.DeleteAsync(api + $"/credentials/{s.Credential}?expectedRevision={await Revision(owner, s.Tenant)}&expectedCredentialRevision=2");
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode); Assert.Null(await watches.ReadTenantEmailWatchAsync(s.Tenant, s.Credential));
                Assert.Equal(0, (await Json(await owner.GetAsync(api + $"/agents/{s.Instance}/events/core/email.message.received"))).GetProperty("total").GetInt32());
                Assert.Equal(0, (await Json(await owner.GetAsync(api + "/approvals"))).GetProperty("total").GetInt32()); return;
            }
            if (coalesce)
            {
                var activity = await Json(await owner.GetAsync(api + $"/agents/{s.Instance}/automations/{automationId}/events"));
                foreach (var eventId in new[] { fact.Event.EventId, secondId!.Value })
                {
                    var entry = Assert.Single(activity.GetProperty("items").EnumerateArray(), r => r.GetProperty("eventId").GetGuid() == eventId);
                    if (retainedEmailIds.Contains(eventId))
                    { Assert.Equal(retainedEmailIds.Length, entry.GetProperty("batchCount").GetInt32()); Assert.Equal(run.AgentRunId, entry.GetProperty("runId").GetGuid()); }
                    else
                    { Assert.Equal("policySkipped", entry.GetProperty("status").GetString()); Assert.Equal(JsonValueKind.Null, entry.GetProperty("batchCount").ValueKind); Assert.Equal(JsonValueKind.Null, entry.GetProperty("runId").ValueKind); }
                }
            }
        }
        var read = await owner.GetAsync(api + $"/events/core/{fact.Event.EventId}"); Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        if (sqlite)
        {
            await using var db = factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
            if (mode == "review")
            {
                var row = await db.CoreEvents.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.EventId == fact.Event.EventId.ToString("D"));
                var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fact.Event.DataJson)!;
                var priorReference = data["messageRef"].GetString()!; var missingProof = fact.Event with { EventId = Guid.NewGuid(), DedupeKey = fact.Event.DedupeKey.Replace(priorReference, "unproved-reference", StringComparison.Ordinal),
                    DataJson = JsonSerializer.Serialize(new { accountId = s.Credential, accountRef = s.Alias, messageRef = "unproved-reference", provider = "Gmail" }) };
                await using (var tx = await db.Database.BeginTransactionAsync())
                {
                    CoreEventPersistence.Stage(db, missingProof, JsonSerializer.Deserialize<EventSubscriptionSnapshot[]>(row.SubscriptionsJson!)!);
                    await db.SaveChangesAsync();
                    await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => tx.CommitAsync()); await tx.RollbackAsync();
                }
                db.ChangeTracker.Clear(); Assert.False(await db.CoreEvents.IgnoreQueryFilters().AnyAsync(e => e.EventId == missingProof.EventId.ToString("D")));
                await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE EmailWatchEvents SET AuthorityJson = '{{}}' WHERE EventId = {fact.Event.EventId.ToString("D")}"));
                await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM EmailWatchEvents WHERE EventId = {fact.Event.EventId.ToString("D")}"));
                await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE EmailWatches SET TenantId = NULL WHERE AccountId = {s.Credential.ToString("D")}"));
                await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE EmailWatches SET StateJson = json_set(StateJson, '$.Unexpected', 'forged'), RuntimeRevision = RuntimeRevision + 1 WHERE AccountId = {s.Credential.ToString("D")}"));
            }
            Assert.Equal(coalesce ? 2 : 1, await db.EmailWatchEvents.IgnoreQueryFilters().CountAsync(e => e.TenantId == s.Tenant)); Assert.Empty(await db.EmailWatchEvents.ToArrayAsync());
        }
        Assert.Equal(0, factory.Outbound);
    }
}
