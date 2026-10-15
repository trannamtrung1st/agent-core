using System.Net.Http.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Continuity;
using AgentCore.Application.Experience;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Experience;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class StateIsolationContinuityTests
{
    [Fact]
    public async Task Tightening_blocks_recall_record_and_maintenance_without_deleting_historical_sources()
    {
        await using var host = new AgentCoreApiFactory();
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var services = host.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 31);
        var source = await ExperienceJourneyTests.SeedAsync(services, instance.InstanceId);
        var store = services.GetRequiredService<IExperienceStore>();
        await store.ConfigureAsync(instance.InstanceId, 0, true);
        await store.ConfigureMaintenanceAsync(instance.InstanceId, 0, true);
        var experience = services.GetRequiredService<ExperienceService>();
        var record = await experience.RequestSessionAsync(instance.InstanceId, source.SessionId);
        await store.CompleteAsync(instance.InstanceId, record.ExperienceId, new("Historical customer A", [], [], ["Observed"], [], [], [], []));
        Assert.Contains("Historical customer A", await experience.RecallAsync(instance.InstanceId));
        var root = $"/api/v2/admin/agent-instances/{instance.InstanceId}/settings/stateIsolationPolicy";
        var current = (await client.GetFromJsonAsync<InstanceSettingsSection>(root))!;
        (await client.PatchAsJsonAsync(root, new { expectedInstanceRevision = current.InstanceRevision,
            set = new { experienceRecord = false, experienceRecall = false, crossSessionContinuity = false }, clear = Array.Empty<string>() })).EnsureSuccessStatusCode();
        Assert.Empty(await experience.RecallAsync(instance.InstanceId));
        Assert.Equal("Forbidden", (await Assert.ThrowsAsync<AgentCoreException>(async () =>
            await experience.RequestSessionAsync(instance.InstanceId, source.SessionId))).Code);
        var continuity = services.GetRequiredService<ContinuityService>();
        Assert.Empty(await continuity.SearchAsync(instance.InstanceId, null, 5, source.SessionId, source.Definition));
        Assert.Equal("Forbidden", (await Assert.ThrowsAsync<AgentCoreException>(async () =>
            await continuity.GetAsync(instance.InstanceId, ContinuityKind.Experience, record.ExperienceId, definition: source.Definition))).Code);
        Assert.False(await services.GetRequiredService<IdentityMaintenanceService>().AllowsAutonomousAsync(instance.InstanceId, default));
        Assert.NotNull((await store.GetAsync(instance.InstanceId, record.ExperienceId))!.Content);
        Assert.NotEmpty(await services.GetRequiredService<IMemoryStore>().ReadHistoryAsync(source.SessionId, 0, 50));
    }
}
