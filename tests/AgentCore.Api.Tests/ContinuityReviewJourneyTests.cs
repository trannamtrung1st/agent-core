using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Continuity;
using AgentCore.Application.Experience;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class ContinuityReviewJourneyTests
{
    [Fact(Timeout = 90000)]
    public async Task Owner_can_disable_unchanged_schedule_after_trigger_policy_is_removed_but_cannot_enable_or_reconfigure()
    {
        OverrideDefinitions? definitions = null;
        await using var host = new ExperienceHost(Database(), configure: services =>
            services.AddSingleton<IAgentDefinitionStore>(sp => definitions = new(sp.GetRequiredService<IBuiltInAgentDefinitionStore>())));
        var instance = await host.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 9);
        var client = TestOwnerCapability.CreateOwnerClient(host);
        var path = $"/api/v2/admin/agent-instances/{instance.InstanceId}/schedules";
        var draft = new AdminScheduleRequest(0, true, "Known task", new("daily", "UTC", LocalTime: "09:00"));
        var created = await client.PostAsJsonAsync(path, draft); created.EnsureSuccessStatusCode();
        var row = (await created.Content.ReadFromJsonAsync<AdminScheduleResponse>())!;
        definitions!.RemovePolicy = true;
        var disabledDraft = draft with { ExpectedRevision = row.Revision, Enabled = false };
        var disabled = await client.PutAsJsonAsync(path + "/" + row.RegistrationId, disabledDraft); disabled.EnsureSuccessStatusCode();
        row = (await disabled.Content.ReadFromJsonAsync<AdminScheduleResponse>())!;
        Assert.Equal("Disabled", row.Status); Assert.Null(row.NextRunAt); Assert.Equal("AdminOwner", row.AuthorizationOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path + "/" + row.RegistrationId,
            draft with { ExpectedRevision = row.Revision })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(path + "/" + row.RegistrationId,
            disabledDraft with { ExpectedRevision = row.Revision, Schedule = draft.Schedule with { Interval = 2 } })).StatusCode);
        var retained = Assert.Single((await client.GetFromJsonAsync<AdminScheduleReview>(path))!.Items);
        Assert.Equal("Disabled", retained.Status); Assert.Equal(row.Revision, retained.Revision);
    }

    [Fact(Timeout = 90000)]
    public async Task Admin_enforces_definition_timing_limits_capacity_and_reenable_without_user_scheduling()
    {
        OverrideDefinitions? definitions = null;
        await using var host = new ExperienceHost(Database(), configure: services =>
            services.AddSingleton<IAgentDefinitionStore>(sp => definitions = new(sp.GetRequiredService<IBuiltInAgentDefinitionStore>())));
        var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 9);
        var client = TestOwnerCapability.CreateOwnerClient(host);
        var path = $"/api/v2/admin/agent-instances/{instance.InstanceId}/schedules";
        var policy = new TriggerPolicy(true, false, true, true, true, true, 1, 2, 1, ["schedule"], true, 300);
        var now = DateTimeOffset.UtcNow;
        AdminScheduleTiming daily = new("daily", "UTC", Interval: 1, LocalTime: "09:00");
        AdminScheduleRequest Draft(AdminScheduleTiming timing) => new(0, true, "Known task", timing);
        async Task Denied(TriggerPolicy p, AdminScheduleTiming timing)
        {
            definitions!.Policy = p;
            var response = await client.PostAsJsonAsync(path, Draft(timing));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Empty((await client.GetFromJsonAsync<AdminScheduleReview>(path))!.Items);
        }
        await Denied(policy with { AllowDaily = false }, daily);
        await Denied(policy with { AllowWeekly = false }, new("weekly", "UTC", LocalTime: "09:00", Weekdays: [1]));
        await Denied(policy with { AllowFixedInterval = false }, new("fixedInterval", Interval: 600, AnchorAtUtc: now.AddHours(1).ToString("O")));
        await Denied(policy with { AllowOneShot = false }, new("oneShot", AtUtc: now.AddDays(1).ToString("O")));
        await Denied(policy, new("oneShot", AtUtc: now.AddDays(3).ToString("O")));
        await Denied(policy with { MinRecurrenceDays = 3 }, daily);
        await Denied(policy with { MinRecurrenceDays = 8 }, new("weekly", "UTC", LocalTime: "09:00", Weekdays: [1]));
        await Denied(policy, new("fixedInterval", Interval: 60, AnchorAtUtc: now.AddHours(1).ToString("O")));
        await Denied(policy with { AllowIndefiniteRecurrence = false }, daily);
        definitions!.Policy = policy with { AllowIndefiniteRecurrence = false };
        var triggers = s.GetRequiredService<ITriggerStore>();
        var owner = new TriggerOwner(instance.InstanceId, LocalUserProfile.Id);
        for (var i = 0; i < 3; i++)
            await triggers.CreateAsync(new(Guid.NewGuid(), owner, TriggerRegistrationStatus.Active, "Existing event subscription",
                new OneShotSchedule(now.AddDays(1), "UTC"), now.AddDays(1), null, 0, 1, 1,
                new(TriggerAuthorizationOrigin.ApplicationEvent, null, null, now, now), null, eventSourceId: Guid.NewGuid(), eventType: "order.placed"));
        Assert.Equal(0, await triggers.CountActiveAsync(owner));
        var finite = Draft(daily with { MaxOccurrences = 2 });
        var created = await client.PostAsJsonAsync(path, finite); created.EnsureSuccessStatusCode();
        var row = (await created.Content.ReadFromJsonAsync<AdminScheduleResponse>())!;
        Assert.Equal("AdminOwner", row.AuthorizationOrigin);
        Assert.False((await client.GetFromJsonAsync<AdminScheduleReview>(path))!.Policy!.AllowIndefiniteRecurrence);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(path, finite)).StatusCode);
        var disabled = await client.PutAsJsonAsync(path + "/" + row.RegistrationId, finite with { ExpectedRevision = row.Revision, Enabled = false });
        disabled.EnsureSuccessStatusCode(); row = (await disabled.Content.ReadFromJsonAsync<AdminScheduleResponse>())!;
        definitions.Policy = policy with { AllowDaily = false };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path + "/" + row.RegistrationId, finite with { ExpectedRevision = row.Revision })).StatusCode);
        var retained = Assert.Single((await client.GetFromJsonAsync<AdminScheduleReview>(path))!.Items);
        Assert.Equal("Disabled", retained.Status); Assert.Equal(row.Revision, retained.Revision);
    }

    [Fact(Timeout = 90000)]
    public async Task Automatic_recall_uses_summary_shortlist_and_does_not_duplicate_cross_session_memory()
    {
        await using var host = new ExperienceHost(Database()); var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 9);
        var history = s.GetRequiredService<IMemoryStore>();
        var template = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        var now = DateTimeOffset.UtcNow;
        var sourceId = Guid.NewGuid();
        for (var i = 0; i < 101; i++)
        {
            var id = i == 0 ? sourceId : Guid.NewGuid();
            var entry = new ConversationEntry(Guid.NewGuid(), 150, null, ConversationRole.User, "Recent unrelated conversation", null,
                EntryStatus.Completed, SessionMode.Text, 0, 0, now);
            await history.SaveAsync(template with { SessionId = id, Revision = 1, Entries = [entry], LastEntrySequence = 150,
                Summary = i == 0 ? "Previously discussed certificate renewal" : "Unrelated topic", SummarizedThroughEntrySequence = 100,
                UpdatedAt = now.AddMinutes(i == 0 ? 50 : i), Title = "Earlier conversation" }, 0);
        }
        var memoryId = Guid.NewGuid();
        await s.GetRequiredService<IStructuredMemoryStore>().InsertAsync(new(memoryId, sourceId, MemoryKind.Preference, MemoryItemStatus.Active,
            "Reports", "Prefer concise store reports", "reports", new("user", [], null, now), now, now, MemoryScope.IdentityUser, instance.InstanceId, LocalUserProfile.Id));
        var counted = new CountingHistory(history);
        var continuity = new ContinuityService(s.GetRequiredService<ExperienceService>(), s.GetRequiredService<IExperienceStore>(), counted,
            s.GetRequiredService<IStructuredMemoryStore>(), s.GetRequiredService<IAgentDefinitionStore>());
        var context = await continuity.ContextAsync(instance.InstanceId, "certificate renewal", template.SessionId, template.Definition, default);
        Assert.Contains("Previously discussed certificate renewal", context);
        Assert.Contains("Persisted session summary", context);
        Assert.Contains("\"throughCursor\":100", context);
        Assert.InRange(counted.Reads, 1, ContinuityService.AutomaticSessionLimit);
        Assert.DoesNotContain("Prefer concise store reports", context);
        var explicitSearch = await continuity.SearchAsync(instance.InstanceId, "concise store reports");
        Assert.Contains(explicitSearch, item => item.Kind == ContinuityKind.Memory && item.Id == memoryId);
        var profile = await history.LoadProfileAsync(LocalUserProfile.Id);
        var learned = await SessionMemoryPrompt.LoadAsync(s.GetRequiredService<IStructuredMemoryService>(), template.SessionId, template.Definition,
            profile, [], agentInstanceId: instance.InstanceId);
        Assert.Contains(learned, item => item.Content == "Prefer concise store reports");
    }

    [Fact(Timeout = 90000)]
    public async Task Maintenance_reaches_instances_and_sessions_beyond_first_page_and_dedupes()
    {
        await using var host = new ExperienceHost(Database()); var s = host.Services;
        var instance = await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 9);
        var instances = s.GetRequiredService<IAgentInstanceStore>();
        Guid Key(int i) => Guid.Parse($"ffffffff-ffff-ffff-ffff-{i:000000000000}");
        for (var i = 1; i <= 101; i++) await instances.InsertAsync(instance with { InstanceId = Key(i) });
        var target = Key(101);
        await s.GetRequiredService<IExperienceStore>().ConfigureAsync(target, 0, true);
        var template = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(target, SessionMode.Text);
        var history = s.GetRequiredService<IMemoryStore>();
        for (var i = 1; i <= 101; i++)
        {
            var entry = new ConversationEntry(Guid.NewGuid(), 1, null, ConversationRole.Assistant, "Completed meaningful audit", Guid.NewGuid(),
                EntryStatus.Completed, SessionMode.Text, 0, 26, DateTimeOffset.UtcNow);
            await history.SaveAsync(template with { SessionId = Key(i), Revision = 1, Status = SessionStatus.Attached,
                Entries = i == 101 ? [entry] : [], LastEntrySequence = i == 101 ? 1 : 0 }, 0);
        }
        await s.GetRequiredService<ContinuityMaintenance>().RunOnceAsync();
        await s.GetRequiredService<ContinuityMaintenance>().RunOnceAsync();
        var record = Assert.Single(await s.GetRequiredService<IExperienceStore>().ListAsync(target, 100));
        Assert.Equal(Key(101), record.SourceId);
        Assert.Single(await s.GetRequiredService<IWorkItemStore>().ListAsync(new(target, LocalUserProfile.Id), 100));
    }

    private static string Database() => Path.Combine(Path.GetTempPath(), $"continuity-review-{Guid.NewGuid():N}.db");
    private sealed class OverrideDefinitions(IAgentDefinitionStore inner) : IAgentDefinitionStore
    {
        public TriggerPolicy? Policy { get; set; }
        public bool RemovePolicy { get; set; }
        public async ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default)
        { var definition = await inner.GetAsync(id, version, cancellationToken); return definition is null ? null : RemovePolicy ? definition with { TriggerPolicy = null } : Policy is null ? definition : definition with { TriggerPolicy = Policy }; }
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
    }
    private sealed class CountingHistory(IMemoryStore inner) : IMemoryStore
    {
        public int Reads { get; private set; }
        public async ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(Guid id, long after, int limit, CancellationToken cancellationToken = default)
        { Reads++; return await inner.ReadHistoryAsync(id, after, limit, cancellationToken); }
        public ValueTask<SessionSnapshot?> LoadAsync(Guid id, CancellationToken cancellationToken = default) => inner.LoadAsync(id, cancellationToken);
        public ValueTask<SessionSnapshot?> LoadMetadataAsync(Guid id, CancellationToken cancellationToken = default) => inner.LoadMetadataAsync(id, cancellationToken);
        public ValueTask SaveAsync(SessionSnapshot snapshot, long revision, CancellationToken cancellationToken = default) => inner.SaveAsync(snapshot, revision, cancellationToken);
        public ValueTask<UserProfile?> LoadProfileAsync(Guid id, CancellationToken cancellationToken = default) => inner.LoadProfileAsync(id, cancellationToken);
        public ValueTask SaveProfileAsync(UserProfile profile, long revision, CancellationToken cancellationToken = default) => inner.SaveProfileAsync(profile, revision, cancellationToken);
        public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) => inner.RecoverCrashedSessionsAsync(cancellationToken);
        public ValueTask<IReadOnlyList<SessionSnapshot>> ListOwnedSessionsAsync(Guid id, Guid profile, int limit, bool activeOnly = false, CancellationToken cancellationToken = default) => inner.ListOwnedSessionsAsync(id, profile, limit, activeOnly, cancellationToken);
    }
}
