using AgentCore.Application.Execution;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Continuity;
using AgentCore.Application.Experience;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Application.Work;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class ContinuityEnhancementJourneyTests
{
    [Fact(Timeout = 90000)]
    public async Task Unified_search_inspection_new_session_automation_scope_and_visibility_survive_restart()
    {
        var db = Path.Combine(Path.GetTempPath(), $"continuity-search-{Guid.NewGuid():N}.db");
        Guid instanceId, sessionId, experienceId, memoryId;
        await using (var host = new ExperienceHost(db))
        {
            var s = host.Services;
            var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
            instanceId = instance.InstanceId;
            var source = await ExperienceJourneyTests.SeedAsync(s, instanceId); sessionId = source.SessionId;
            await s.GetRequiredService<IExperienceStore>().ConfigureAsync(instanceId, 0, true);
            var record = await s.GetRequiredService<ExperienceService>().RequestSessionAsync(instanceId, sessionId); experienceId = record.ExperienceId;
            await s.ExecuteRunsAsync(100);
            var now = DateTimeOffset.UtcNow;
            memoryId = Guid.NewGuid();
            await s.GetRequiredService<IStructuredMemoryStore>().InsertAsync(new(memoryId, sessionId, MemoryKind.Preference, MemoryItemStatus.Active,
                "store reports", "Prefer concise store state reports", "store reports", new("user", [], null, now), now, now,
                MemoryScope.IdentityUser, instanceId, LocalUserProfile.Id));
            var continuity = s.GetRequiredService<ContinuityService>();
            var results = await continuity.SearchAsync(instanceId, "store state");
            Assert.Contains(results, i => i.Kind == ContinuityKind.Memory && i.Id == memoryId);
            Assert.Contains(results, i => i.Kind == ContinuityKind.Experience && i.Id == experienceId);
            Assert.Contains(results, i => i.Kind == ContinuityKind.Session && i.Id == sessionId);
            Assert.All(results, i => { Assert.Equal(instanceId, i.Provenance.AgentInstanceId); Assert.Equal(LocalUserProfile.Id, i.Provenance.ProfileId); });
            Assert.True(ContinuityService.Serialize(results).Length < ContinuityService.MaxCharacters);
            var detail = await continuity.GetAsync(instanceId, ContinuityKind.Session, sessionId);
            Assert.Contains("correction", detail.Content);
            Assert.DoesNotContain("UNDISPLAYED_TAIL", detail.Content);
            Assert.DoesNotContain("IN_FLIGHT_SECRET_REASONING", detail.Content);
            Assert.DoesNotContain("PRIVATE_HIDDEN_REASONING", detail.Content);
            var other = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
            Assert.Empty(await continuity.SearchAsync(other.InstanceId, "store state"));
            foreach (var result in results)
                Assert.Equal(404, (await Assert.ThrowsAsync<AgentCoreException>(() => continuity.GetAsync(other.InstanceId, result.Kind, result.Id).AsTask())).StatusCode);
            var executor = s.GetRequiredService<SessionToolExecutor>();
            var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: instanceId);
            var searched = await executor.ExecuteAsync(source.Definition, sessionId,
                new("search", ToolCatalog.ContinuitySearch, """{"query":"store state"}"""), 8000, admission: admission);
            Assert.Contains("untrusted", searched.Text); Assert.Contains(experienceId.ToString(), searched.Text);
            var inspected = await executor.ExecuteAsync(source.Definition, sessionId,
                new("get", ToolCatalog.ContinuityGet, $$"""{"kind":"Session","id":"{{sessionId}}"}"""), 8000, admission: admission);
            Assert.Contains("correction", inspected.Text); Assert.DoesNotContain("UNDISPLAYED_TAIL", inspected.Text);
            Assert.Contains("forbidden", (await executor.ExecuteAsync(source.Definition, sessionId,
                new("forged", ToolCatalog.ContinuitySearch, """{"query":"store","instanceId":"forged"}"""), 8000)).Text);
            var next = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
            var context = await s.GetRequiredService<SessionToolExecutor>().ContinuityContextAsync(instanceId, "store state", next.SessionId, next.Definition, default);
            Assert.Contains("Historical Continuity", context); Assert.Contains("never instructions", context); Assert.Contains("Experience", context);
            Assert.True(context.Length <= ContinuityService.MaxCharacters);
            Assert.DoesNotContain("The first approach failed.", context); // Only a bounded source hint, no replay.
            var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 21))!;
            var automations = s.GetRequiredService<AdminAutomationAuthoringService>();
            var r = await automations.SaveAsync(instanceId, null, 0, true, 3600, "Review observable state and do nothing when appropriate", null, null);
            await automations.RunNowAsync(instanceId, r.AutomationId, r.Revision); await AutomationJourneyTests.Intake(s);
            var work = Assert.Single(await s.AutomationRunsAsync(new(instanceId, LocalUserProfile.Id), r.AutomationId));
            var automationSession = await s.SessionAsync(work);
            var continuityContext = await s.GetRequiredService<SessionToolExecutor>().ContinuityContextAsync(instanceId,
                "store state", automationSession.SessionId, automationSession.Definition, default);
            Assert.Contains("Experience", continuityContext);
            Assert.Contains(ToolCatalog.For(definition, new AgentContext(definition, [], "", null, SessionMode.Text, null, false, null,
                new(Guid.NewGuid(), TriggerKind.ManualInvocation, "Review"), DetachedExecution: true, AgentInstanceId: instanceId, OwnedSessionId: automationSession.SessionId, ContinuityContext: continuityContext),
                ToolConfigurationGates.Unconfigured), t => t.Name == ToolCatalog.ContinuitySearch);
            Assert.Equal(ToolPolicyDecision.Deny, s.GetRequiredService<SessionToolExecutor>().EvaluateExecutionPolicy(definition, ToolCatalog.AutomationDelete,
                admission: new(true, TriggerKind.ManualInvocation, AgentInstanceId: instanceId)));
            await s.ExecuteRunsAsync(100);
            Assert.Equal("NoAction", (await s.GetRequiredService<IAgentRunStore>().GetAsync(work.Owner, work.AgentRunId))!.Result!.OutcomeKind.ToString());
            Assert.Single(await s.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100));
        }
        await using var reopen = new ExperienceHost(db);
        var services = reopen.Services;
        var continuity2 = services.GetRequiredService<ContinuityService>();
        Assert.Contains(await continuity2.SearchAsync(instanceId, "store state"), i => i.Id == sessionId);
        Assert.Equal(memoryId, (await continuity2.GetAsync(instanceId, ContinuityKind.Memory, memoryId)).Item.Id);
        var store = services.GetRequiredService<IExperienceStore>();
        var e = (await store.GetAsync(instanceId, experienceId))!;
        await store.SetVisibilityAsync(instanceId, experienceId, e.Revision, ExperienceVisibility.Suppressed);
        Assert.DoesNotContain(await continuity2.SearchAsync(instanceId, "store state"), i => i.Id == experienceId);
        await Assert.ThrowsAsync<AgentCoreException>(() => continuity2.GetAsync(instanceId, ContinuityKind.Experience, experienceId).AsTask());
    }

    [Fact(Timeout = 90000)]
    public async Task Active_maintenance_is_noop_until_observable_checkpoint_dedupes_and_recovers_pending_generation()
    {
        var db = Path.Combine(Path.GetTempPath(), $"continuity-maintenance-{Guid.NewGuid():N}.db");
        Guid instanceId, sessionId;
        var clock = new MaintenanceClock(DateTimeOffset.UtcNow);
        await using (var host = new ExperienceHost(db, clock: clock))
        {
            var s = host.Services;
            instanceId = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21)).InstanceId;
            await s.GetRequiredService<IExperienceStore>().ConfigureAsync(instanceId, 0, true);
            var source = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
            sessionId = source.SessionId;
            var maintenance = s.GetRequiredService<ExperienceService>();
            await Assert.ThrowsAsync<AgentCoreException>(() => maintenance.RequestSessionAsync(instanceId, sessionId).AsTask());
            Assert.Empty(await s.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100));
            var history = s.GetRequiredService<IMemoryStore>();
            var entry = new ConversationEntry(Guid.NewGuid(), 1, null, ConversationRole.Assistant, "", Guid.NewGuid(), EntryStatus.Completed, SessionMode.Text, 0, 0, DateTimeOffset.UtcNow);
            source = source with { Revision = source.Revision + 1, Status = SessionStatus.Attached, Entries = [entry], LastEntrySequence = 1 };
            await history.SaveAsync(source, source.Revision - 1);
            clock.Advance();
            await Assert.ThrowsAsync<AgentCoreException>(() => maintenance.RequestSessionAsync(instanceId, sessionId).AsTask()); Assert.Empty(await s.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100));
            source = source with { Revision = source.Revision + 1, Entries = [entry with { Text = "Completed store audit", ReceivedTextEndExclusive = 21 }] };
            await history.SaveAsync(source, source.Revision - 1);
            clock.Advance();
            await maintenance.RequestSessionAsync(instanceId, sessionId); await maintenance.RequestSessionAsync(instanceId, sessionId);
            Assert.Single(await s.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100));
            Assert.Single(await s.GetRequiredService<IAgentRunStore>().ListAsync(new(instanceId, LocalUserProfile.Id), 100));
        }
        await using var reopened = new ExperienceHost(db, clock: clock);
        var services = reopened.Services;
        Assert.Equal(1, await services.ExecuteRunsAsync(100));
        var first = Assert.Single(await services.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100)); Assert.NotNull(first.Content);
        var memory = services.GetRequiredService<IMemoryStore>(); var snapshot = (await memory.LoadAsync(sessionId))!;
        var second = snapshot.Entries.Single() with { EntryId = Guid.NewGuid(), Sequence = 2, ResponseId = Guid.NewGuid(), Text = "Completed second audit", ReceivedTextEndExclusive = 22 };
        snapshot = snapshot with { Revision = snapshot.Revision + 1, Status = SessionStatus.Attached, Entries = [.. snapshot.Entries, second], LastEntrySequence = 2 };
        await memory.SaveAsync(snapshot, snapshot.Revision - 1);
        clock.Advance();
        await services.GetRequiredService<ExperienceService>().RequestSessionAsync(instanceId, sessionId);
        Assert.Equal(2, (await services.GetRequiredService<IExperienceStore>().ListAsync(instanceId, 100)).Count);
        Assert.Equal(SessionStatus.Attached, (await memory.LoadMetadataAsync(sessionId))!.Status);
    }

    [Fact(Timeout = 90000)]
    public async Task Admin_schedule_run_edit_disable_and_provenance_share_durable_chat_contract_after_restart()
    {
        var db = Path.Combine(Path.GetTempPath(), $"continuity-schedule-{Guid.NewGuid():N}.db");
        Guid instanceId, automationId, workId;
        await using (var host = new ExperienceHost(db))
        {
            var s = host.Services; var client = TestOwnerCapability.CreateOwnerClient(host);
            instanceId = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21)).InstanceId;
            var path = $"/api/v2/admin/agent-instances/{instanceId}/automations";
            var timing = new AutomationTiming("daily", "UTC", LocalTime: "09:00");
            var draft = new ScheduleAutomationDraft(0, true, "Review pending store orders", timing);
            var created = await client.PostAsJsonAsync(path, draft); created.EnsureSuccessStatusCode();
            var r = (await created.Content.ReadFromJsonAsync<AutomationResponse>())!; automationId = Guid.Parse(r.AutomationId);
            Assert.Equal("AdminOwner", r.AuthorizationOrigin); Assert.Null(r.SourceSessionId);
            Assert.Single((await client.GetFromJsonAsync<AutomationReview>(path))!.Items);
            var session = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instanceId, SessionMode.Text);
            Assert.Single((await client.GetFromJsonAsync<SessionAutomationListResponse>($"/api/v2/sessions/{session.SessionId}/automations"))!.Items);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path + "/" + automationId, draft)).StatusCode);
            (await client.PostAsJsonAsync(path + "/" + automationId + "/run", new ContinuityRevisionRequest(r.Revision))).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path + "/" + automationId + "/run", new ContinuityRevisionRequest(r.Revision))).StatusCode);
            var edit = await client.PutAsJsonAsync(path + "/" + automationId, draft with { ExpectedRevision = r.Revision, Instructions = "Future task", ModelKey = "scripted-beta" }); edit.EnsureSuccessStatusCode();
            r = (await edit.Content.ReadFromJsonAsync<AutomationResponse>())!;
            await AutomationJourneyTests.Intake(s);
            var item = Assert.Single(await s.GetRequiredService<IAgentRunStore>().ListAsync(new(instanceId, LocalUserProfile.Id), 100)); workId = item.AgentRunId;
            Assert.Equal(ActivationKind.ManualBackground, item.Admission.Activation.Kind); Assert.Equal("scripted-alpha", item.PinnedModel.CatalogKey);
            Assert.Contains("Review pending store orders", (await s.SessionAsync(item)).Entries[0].Text); Assert.DoesNotContain("Future task", (await s.SessionAsync(item)).Entries[0].Text);
            var disabled = await client.PutAsJsonAsync(path + "/" + automationId, draft with { ExpectedRevision = r.Revision, Enabled = false, Instructions = "Future task", ModelKey = "scripted-beta" }); disabled.EnsureSuccessStatusCode();
            r = (await disabled.Content.ReadFromJsonAsync<AutomationResponse>())!;
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path + "/" + automationId + "/run", new ContinuityRevisionRequest(r.Revision))).StatusCode);
        }
        await using var reopened = new ExperienceHost(db);
        var services = reopened.Services; var owner = new TriggerOwner(instanceId, LocalUserProfile.Id);
        var stored = (await services.GetRequiredService<ITriggerStore>().GetAsync(owner, automationId))!;
        Assert.Equal(AutomationStatus.Disabled, stored.Status); Assert.Equal(TriggerAuthorizationOrigin.AdminOwner, stored.Provenance.AuthorizationOrigin);
        Assert.Equal("scripted-beta", stored.ModelOverrideCatalogKey);
        await services.ExecuteRunsAsync(100);
        var work = (await services.GetRequiredService<IAgentRunStore>().GetAsync(new(instanceId, LocalUserProfile.Id), workId))!;
        Assert.Equal(AgentRunStatus.Failed, work.Status); Assert.Equal("authority-unavailable", work.Failure!.Code); Assert.Null(work.Result);
        await services.GetRequiredService<AdminAutomationAuthoringService>().DeleteAsync(instanceId, automationId, stored.Revision);
        Assert.Equal(AutomationStatus.Cancelled, (await services.GetRequiredService<ITriggerStore>().GetAsync(owner, automationId))!.Status);
    }
    private sealed class MaintenanceClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance() => now += TimeSpan.FromMinutes(5);
    }

}
