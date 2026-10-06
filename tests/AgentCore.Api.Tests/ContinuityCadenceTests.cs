using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Continuity;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class ContinuityCadenceTests
{
    [Theory]
    [InlineData(0, 60, 300, 86400)] [InlineData(3601, 60, 300, 86400)]
    [InlineData(60, 0, 300, 86400)] [InlineData(60, 600, 300, 86400)]
    [InlineData(60, 60, 300, 120)] [InlineData(60, 60, -1, 86400)]
    public async Task Invalid_operator_policy_fails_startup(int poll, int min, int standard, int max)
    {
        await using var host = new ExperienceHost(Db(), configuration: c => c.AddInMemoryCollection(Policy(poll, min, standard, max)));
        var error = Assert.ThrowsAny<Exception>(() => _ = host.Services);
        Assert.Contains("ContinuityMaintenance intervals", error.ToString());
    }

    [Fact(Timeout = 90000)]
    public async Task Admin_cadence_is_revisioned_bounded_owned_and_independent_of_other_settings()
    {
        var db = Db(); Guid id;
        await using (var host = new ExperienceHost(db))
        {
            var s = host.Services; var client = TestOwnerCapability.CreateOwnerClient(host);
            id = (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 9)).InstanceId;
            var path = PathFor(id);
            var initial = (await client.GetFromJsonAsync<ContinuityMaintenanceResponse>(path))!;
            Assert.Equal(300, initial.EffectiveIntervalSeconds); Assert.Equal(60, initial.MinimumIntervalSeconds);
            Assert.Equal(86400, initial.MaximumIntervalSeconds); Assert.True(initial.UsesDefault);
            Assert.Null(initial.ConfiguredIntervalSeconds); Assert.Equal(0, initial.Revision);
            foreach (var invalid in new[] { 59, 86401, 0, -1 })
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(path, new ContinuityMaintenanceConfigurationRequest(0, invalid))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync(path, new StringContent("{\"expectedRevision\":0,\"intervalSeconds\":\"five\"}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync(path, new StringContent("not-json"))).StatusCode);
            var savedResponse = await client.PutAsJsonAsync(path, new ContinuityMaintenanceConfigurationRequest(0, 900)); savedResponse.EnsureSuccessStatusCode();
            var saved = (await savedResponse.Content.ReadFromJsonAsync<ContinuityMaintenanceResponse>())!;
            Assert.Equal(900, saved.EffectiveIntervalSeconds); Assert.Equal(900, saved.ConfiguredIntervalSeconds);
            Assert.Equal(1, saved.Revision); Assert.False(saved.UsesDefault);
            var audit = Assert.Single(await s.GetRequiredService<IAdminEventStore>().ListAsync(new("agentInstance", id.ToString("D"))),
                e => e.SummaryJson.Contains("configureContinuityCadence"));
            Assert.Equal(1, audit.Revision);
            using (var details = System.Text.Json.JsonDocument.Parse(audit.SummaryJson))
                Assert.Equal(900, details.RootElement.GetProperty("intervalSeconds").GetInt32());
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(path, new ContinuityMaintenanceConfigurationRequest(0, 300))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync(path)).StatusCode);
            Assert.False((await s.GetRequiredService<IExperienceStore>().SettingsAsync(id)).Enabled);
            Assert.False((await s.GetRequiredService<IExperienceStore>().MaintenanceSettingsAsync(id)).AllowAgentConsolidation);
            Assert.Empty(await s.GetRequiredService<ITriggerStore>().ListAsync(new(id, LocalUserProfile.Id), null));
        }
        await using var reopen = new ExperienceHost(db, configuration: c => c.AddInMemoryCollection(Policy(30, 120, 600, 720)));
        var response = (await TestOwnerCapability.CreateOwnerClient(reopen).GetFromJsonAsync<ContinuityMaintenanceResponse>(PathFor(id)))!;
        Assert.Equal(900, response.ConfiguredIntervalSeconds); Assert.False(response.ConfiguredIntervalAllowed);
        Assert.Equal(600, response.EffectiveIntervalSeconds); Assert.Equal(120, response.MinimumIntervalSeconds); Assert.Equal(720, response.MaximumIntervalSeconds);
        var reset = await TestOwnerCapability.CreateOwnerClient(reopen).PutAsJsonAsync(PathFor(id), new ContinuityMaintenanceConfigurationRequest(1, null)); reset.EnsureSuccessStatusCode();
        Assert.True((await reset.Content.ReadFromJsonAsync<ContinuityMaintenanceResponse>())!.UsesDefault);
        var settings = reopen.Services.GetRequiredService<IContinuityMaintenanceStore>();
        await settings.TryClaimAsync(await settings.ReadAsync(id), reopen.Services.GetRequiredService<TimeProvider>().GetUtcNow());
        var instances = reopen.Services.GetRequiredService<IAgentInstanceStore>();
        var instance = (await instances.FindAsync(id))!;
        var owner = reopen.Services.GetRequiredService<AdminAgentInstanceService>();
        var archived = await owner.SetLifecycleAsync(id, AgentInstanceLifecycle.Archived, instance.Revision);
        await owner.DeleteAsync(new(id, archived.Revision, Guid.NewGuid(), DateTimeOffset.UtcNow));
        Assert.Equal(0, (await settings.ReadAsync(id)).Revision);
        Assert.Null((await settings.ReadAsync(id)).LastMaintenanceAtUtc);
    }

    [Fact(Timeout = 90000)]
    public async Task Due_boundaries_instance_intervals_restart_and_racing_scans_preserve_deterministic_admission()
    {
        var db = Db(); var clock = new CadenceClock(DateTimeOffset.FromUnixTimeMilliseconds(1791240000000));
        Guid fast, slow, disabled, archived, compatibility; Guid fastSession, slowSession;
        await using (var host = new ExperienceHost(db, clock: clock))
        {
            var s = host.Services;
            fast = await Managed(s); slow = await Managed(s); disabled = await Managed(s); archived = await Managed(s);
            var definition = (await s.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 9))!;
            compatibility = (await s.GetRequiredService<IAgentInstanceService>().ResolveCompatibilityAsync(definition)).InstanceId;
            var experiences = s.GetRequiredService<IExperienceStore>();
            foreach (var id in new[] { fast, slow, archived, compatibility }) await experiences.ConfigureAsync(id, 0, true);
            var instances = s.GetRequiredService<IAgentInstanceStore>(); var a = (await instances.FindAsync(archived))!;
            await instances.UpdateWithExpectedRevisionAsync(new(archived, a.Revision, Lifecycle: AgentInstanceLifecycle.Archived), clock.GetUtcNow());
            var settings = s.GetRequiredService<IContinuityMaintenanceStore>();
            await settings.ConfigureAsync(fast, 0, 60); await settings.ConfigureAsync(slow, 0, 900);
            fastSession = await Source(s, fast, "First fast checkpoint"); slowSession = await Source(s, slow, "First slow checkpoint");
            var maintenance = s.GetRequiredService<ContinuityMaintenance>();
            await Task.WhenAll(maintenance.RunOnceAsync().AsTask(), maintenance.RunOnceAsync().AsTask());
            foreach (var id in new[] { fast, slow }) Assert.Single(await experiences.ListAsync(id, 100));
            foreach (var id in new[] { disabled, archived, compatibility }) Assert.Null((await settings.ReadAsync(id)).LastMaintenanceAtUtc);
            await AddCheckpoint(s, fastSession, "Second fast checkpoint"); await AddCheckpoint(s, slowSession, "Second slow checkpoint");
            clock.Advance(TimeSpan.FromSeconds(59)); await maintenance.RunOnceAsync();
            Assert.Single(await experiences.ListAsync(fast, 100));
        }
        await using (var reopen = new ExperienceHost(db, clock: clock))
        {
            var s = reopen.Services; var maintenance = s.GetRequiredService<ContinuityMaintenance>(); var experiences = s.GetRequiredService<IExperienceStore>();
            // Startup crash recovery detaches Sessions; restore active attachment without changing the checkpoint.
            var history = s.GetRequiredService<IMemoryStore>();
            foreach (var session in new[] { fastSession, slowSession })
            {
                var snapshot = (await history.LoadAsync(session))!;
                await history.SaveAsync(snapshot with { Revision = snapshot.Revision + 1, Status = SessionStatus.Attached }, snapshot.Revision);
            }
            await maintenance.RunOnceAsync(); // Reopening is not a new due boundary.
            Assert.Single(await experiences.ListAsync(fast, 100)); Assert.Single(await experiences.ListAsync(slow, 100));
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.WhenAll(maintenance.RunOnceAsync().AsTask(), maintenance.RunOnceAsync().AsTask());
            Assert.Equal(2, (await experiences.ListAsync(fast, 100)).Count); Assert.Single(await experiences.ListAsync(slow, 100));
            await maintenance.RunOnceAsync(); Assert.Equal(2, (await experiences.ListAsync(fast, 100)).Count);
            // Shortening the interval retains the claim timestamp and makes this instance exactly due now.
            var settings = s.GetRequiredService<IContinuityMaintenanceStore>(); await settings.ConfigureAsync(slow, 1, 60);
            await maintenance.RunOnceAsync(); Assert.Equal(2, (await experiences.ListAsync(slow, 100)).Count);
            await AddCheckpoint(s, fastSession, "Third fast checkpoint"); await settings.ConfigureAsync(fast, 1, 900);
            clock.Advance(TimeSpan.FromSeconds(899)); await maintenance.RunOnceAsync();
            Assert.Equal(2, (await experiences.ListAsync(fast, 100)).Count);
            clock.Advance(TimeSpan.FromSeconds(1)); await maintenance.RunOnceAsync();
            Assert.Equal(3, (await experiences.ListAsync(fast, 100)).Count);
            Assert.Equal(SessionStatus.Attached, (await s.GetRequiredService<IMemoryStore>().LoadMetadataAsync(fastSession))!.Status);
        }
    }

    private static Dictionary<string, string?> Policy(int poll, int min, int standard, int max) => new()
    { ["ContinuityMaintenance:PollIntervalSeconds"] = poll.ToString(), ["ContinuityMaintenance:MinimumIntervalSeconds"] = min.ToString(),
      ["ContinuityMaintenance:DefaultIntervalSeconds"] = standard.ToString(), ["ContinuityMaintenance:MaximumIntervalSeconds"] = max.ToString() };
    private static string Db() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"continuity-cadence-{Guid.NewGuid():N}.db");
    private static string PathFor(Guid id) => $"/api/v2/admin/agent-instances/{id}/continuity-maintenance";
    private static async Task<Guid> Managed(IServiceProvider s) => (await s.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 9)).InstanceId;
    private static async Task<Guid> Source(IServiceProvider s, Guid id, string text)
    {
        var source = await s.GetRequiredService<SessionManager>().CreateForInstanceAsync(id, SessionMode.Text);
        await AddCheckpoint(s, source.SessionId, text); return source.SessionId;
    }
    private static async Task AddCheckpoint(IServiceProvider s, Guid id, string text)
    {
        var history = s.GetRequiredService<IMemoryStore>(); var snapshot = (await history.LoadAsync(id))!;
        var seq = snapshot.LastEntrySequence + 1;
        var entry = new ConversationEntry(Guid.NewGuid(), seq, null, ConversationRole.Assistant, text, Guid.NewGuid(), EntryStatus.Completed,
            SessionMode.Text, text.Length, text.Length, s.GetRequiredService<TimeProvider>().GetUtcNow());
        await history.SaveAsync(snapshot with { Revision = snapshot.Revision + 1, Status = SessionStatus.Attached,
            Entries = [.. snapshot.Entries, entry], LastEntrySequence = seq }, snapshot.Revision);
    }
    private sealed class CadenceClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; public void Advance(TimeSpan elapsed) => now += elapsed; }
}
