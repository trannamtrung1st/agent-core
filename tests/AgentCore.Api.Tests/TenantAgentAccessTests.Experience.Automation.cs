using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;
public sealed partial class TenantAgentAccessTests
{
    private static async Task ExperienceAutomationRights(HttpClient owner, Guid tenant, Guid instance)
    {
        var api = $"/api/v2/tenants/{tenant}";
        var actor = (await Json(await owner.GetAsync(api + $"/agents/{instance}"))).GetProperty("principalId").GetGuid();
        var role = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Independent Experience automation", permissions = new[] { "automations.run", "sessions.create", "agent-runs.create" }, expectedRevision = await Revision(owner, tenant) }));
        await Json(await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = actor, roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
    }
    private static async Task<AgentRun> ExperienceAutomation(RuntimeStateFactory factory, HttpClient owner, FakeTimeProvider clock,
        Guid tenant, Guid instance, long instanceRevision, bool scheduled = false)
    {
        var route = $"/api/v2/tenants/{tenant}/agents/{instance}/automations";
        var value = await Json(await owner.PostAsJsonAsync(route, new { enabled = true, name = "Review owned evidence", instructions = "Inspect and retain only owned evidence, then finish.",
            schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = clock.GetUtcNow().AddMinutes(30).ToString("O") }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision, requiresTools = true }));
        var automation = value.GetProperty("automationId").GetGuid(); Guid run;
        if (scheduled)
        {
            clock.Advance(TimeSpan.FromMinutes(31)); var count = await factory.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync();
            // The real hosted scheduler may win the shared admission gate after clock advancement.
            // The saved exact occurrence graph, rather than this caller's count, is the outcome.
            Assert.InRange(count, 0, 1);
            run = Assert.Single((await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items,
                r => r.Admission.TaskIntent?.Source == AgentRunTaskSource.Automation && TenantAutomationRules.SourceAutomation(r) == automation).AgentRunId;
        }
        else
        {
            var admitted = await Json(await owner.PostAsJsonAsync(route + $"/{automation}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision, expectedAutomationRevision = value.GetProperty("revision").GetInt64() }));
            run = admitted.GetProperty("runId").GetGuid();
        }
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        return await ProfileTerminal(factory, tenant, run);
    }

    [Theory(Timeout = 120000)] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_experience_independent_Automation_records_Instance_facts_recalls_them_and_keeps_Instance_only_Run_sources_readable(bool sqlite, bool scheduled)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var model = new ExperienceModel();
        await using var factory = new RuntimeStateFactory(sqlite, clock, model); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: false, automation: true); await MemoryExecutor(owner, tenant, instance, operations: true); await ExperienceAutomationRights(owner, tenant, instance);
        var revision = await ExperienceOptIn(owner, tenant, instance, true); var seed = await MemoryRequest(factory, owner, tenant, instance, instanceRevision: revision);
        await SetOwnProfile(owner, tenant, "AUTOMATION_CREATOR_PRIVATE_PROFILE"); model.Source = seed.Run.AgentRunId;
        var recorded = await ExperienceAutomation(factory, owner, clock, tenant, instance, revision, scheduled); Assert.True(recorded.Status == AgentRunStatus.Completed, $"{recorded.Status}: {recorded.Failure}");
        Assert.Equal(scheduled ? ExecutionOrigin.ScheduledOccurrence : ExecutionOrigin.ManualInvocation, recorded.Admission.ExecutionContext!.Origin);
        Assert.Null(recorded.Owner.ProfileIdOrNull); Assert.Null(AgentRunToolCallCheckpoint.ReadExecutionState(recorded.Checkpoint).TenantProfileSource);
        var api = $"/api/v2/tenants/{tenant}/agents/{instance}/experience";
        var item = Assert.Single((await Json(await owner.GetAsync(api))).GetProperty("items").EnumerateArray()); Assert.Equal("Instance", item.GetProperty("scope").GetString());
        model.Source = Guid.Empty; var before = model.Requests.Count;
        var recalled = await ExperienceAutomation(factory, owner, clock, tenant, instance, revision, scheduled); Assert.Equal(AgentRunStatus.Completed, recalled.Status);
        Assert.Single(TenantExperienceReferences.Facts(recalled.Checkpoint));
        Assert.All(model.Requests.Skip(before), request => {
            Assert.DoesNotContain(request.Context!.Sections, s => s.Id is ContextSectionId.Profile or ContextSectionId.Memory);
            Assert.Contains(request.Context.Sections, s => s.Id == ContextSectionId.Experience && s.Message.Text.Contains("\"scope\":\"Instance\"", StringComparison.Ordinal));
        });
        var selected = await owner.GetAsync(api + $"/review-source?sourceKind=AgentRun&sourceId={recalled.AgentRunId:D}");
        Assert.True(selected.StatusCode == HttpStatusCode.OK, await selected.Content.ReadAsStringAsync());
        model.Source = recalled.AgentRunId;
        var human = await MemoryRequest(factory, owner, tenant, instance, instanceRevision: revision);
        Assert.Null(Assert.Single(TenantExperienceReferences.Sources(human.Run.Checkpoint)).Witness.PrivateUserPrincipalId);
        var page = (await Json(await owner.GetAsync(api))).GetProperty("items").EnumerateArray().ToArray(); Assert.Equal(2, page.Length);
        Assert.Single(page, row => row.GetProperty("scope").GetString() == "AgentUser"); Assert.Single(page, row => row.GetProperty("scope").GetString() == "Instance");
        // Current source proof remains mandatory even for Instance-owned evidence.
        item = page.Single(row => row.GetProperty("scope").GetString() == "Instance");
        var current = await Json(await owner.GetAsync(api));
        await Json(await owner.PostAsJsonAsync(api + "/visibility", new { expectedRevision = current.GetProperty("securityRevision").GetInt64(), experienceId = item.GetProperty("experienceId").GetGuid(), expectedExperienceRevision = item.GetProperty("revision").GetInt64(), visibility = "Suppressed" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(api + $"/review-source?sourceKind=AgentRun&sourceId={recalled.AgentRunId:D}")).StatusCode);
    }

    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_independent_Automation_cannot_adopt_private_human_Run_evidence_or_creator_Experience(bool sqlite)
    {
        var model = new ExperienceModel(); var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new RuntimeStateFactory(sqlite, clock, model); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: false, automation: true); await MemoryExecutor(owner, tenant, instance, operations: true); await ExperienceAutomationRights(owner, tenant, instance);
        var revision = await ExperienceOptIn(owner, tenant, instance, true); var seed = await MemoryRequest(factory, owner, tenant, instance, instanceRevision: revision);
        model.Source = seed.Run.AgentRunId; await MemoryRequest(factory, owner, tenant, instance, instanceRevision: revision);
        model.Source = Guid.Empty; var privateSource = await MemoryRequest(factory, owner, tenant, instance, instanceRevision: revision); Assert.Single(TenantExperienceReferences.Facts(privateSource.Run.Checkpoint));
        model.Source = privateSource.Run.AgentRunId; var before = model.Requests.Count;
        var refused = await ExperienceAutomation(factory, owner, clock, tenant, instance, revision); Assert.NotEqual(AgentRunStatus.Completed, refused.Status);
        Assert.All(model.Requests.Skip(before), request => Assert.DoesNotContain(request.Context!.Sections, s => s.Id is ContextSectionId.Profile or ContextSectionId.Memory or ContextSectionId.Experience));
        var page = await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/agents/{instance}/experience"));
        Assert.Equal("AgentUser", Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("scope").GetString());
    }

    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_experience_human_Run_with_only_Instance_evidence_remains_an_Instance_readable_source_without_adopting_the_human_Profile(bool sqlite)
    {
        var model = new ExperienceModel(); var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = new RuntimeStateFactory(sqlite, clock, model); using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance) = await OwnedMemoryAgent(owner, enabled: false, automation: true); await MemoryExecutor(owner, tenant, instance, operations: true); await ExperienceAutomationRights(owner, tenant, instance);
        var revision = await ExperienceOptIn(owner, tenant, instance, true); var seed = await MemoryRequest(factory, owner, tenant, instance, instanceRevision: revision);
        model.Source = seed.Run.AgentRunId; Assert.Equal(AgentRunStatus.Completed, (await ExperienceAutomation(factory, owner, clock, tenant, instance, revision)).Status);
        model.Source = Guid.Empty; var human = await MemoryRequest(factory, owner, tenant, instance, instanceRevision: revision);
        Assert.Single(TenantExperienceReferences.Facts(human.Run.Checkpoint)); Assert.Null(AgentRunToolCallCheckpoint.ReadExecutionState(human.Run.Checkpoint).TenantProfileSource);
        await SetOwnProfile(owner, tenant, "AUTOMATION_CREATOR_PRIVATE_PROFILE"); var before = model.Requests.Count;
        model.Source = human.Run.AgentRunId; var recorded = await ExperienceAutomation(factory, owner, clock, tenant, instance, revision);
        Assert.True(recorded.Status == AgentRunStatus.Completed, $"{recorded.Status}: {recorded.Failure}");
        Assert.Null(Assert.Single(TenantExperienceReferences.Sources(recorded.Checkpoint)).Witness.PrivateUserPrincipalId);
        Assert.All(model.Requests.Skip(before), request => {
            Assert.DoesNotContain(request.Context!.Sections, s => s.Id is ContextSectionId.Profile or ContextSectionId.Memory);
            Assert.DoesNotContain("AUTOMATION_CREATOR_PRIVATE_PROFILE", string.Join('\n', request.Messages.Select(m => m.Text)));
        });
        var rows = (await Json(await owner.GetAsync($"/api/v2/tenants/{tenant}/agents/{instance}/experience"))).GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length); Assert.All(rows, row => Assert.Equal("Instance", row.GetProperty("scope").GetString()));
    }
}
