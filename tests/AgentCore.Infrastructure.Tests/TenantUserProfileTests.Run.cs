using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantUserProfileTests
{
    private static async Task<(Guid Tenant, HumanSessionReference Human, TenantAgent Agent, TenantRuntimeFrame Frame)> ProfileRun(Fixture f, bool memory = true, bool sessionRead = true)
    {
        var (tenant, human) = await Start(f); await Service(f).UpdateAsync(tenant, human, Change(await Service(f).ReadAsync(tenant, human), "Exact Run requester"));
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        var permissions = new List<string> { "agents.chat" }; if (memory) permissions.Add("memory.read"); if (sessionRead) permissions.Add("sessions.read");
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Run Profile reads", permissions, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        var created = await f.Sessions.CreateAsync(tenant, agent.Instance.InstanceId, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId), SessionMode.Text, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 1);
        var receipt = await f.RunService().SubmitTextAsync(tenant, created.Owner.SessionId, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId), f.Ids.NewId(), "Actual owned Profile source", (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 1);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, receipt.RunId))!;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        return (tenant, human, agent, new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation));
    }
    [Theory] [InlineData(false, true)] [InlineData(true, true)] [InlineData(false, false)] [InlineData(true, false)]
    public async Task Tenant_Run_Profile_requires_independent_raw_memory_and_Session_reads_without_a_global_Profile_fallback(bool sqlite, bool memory)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await ProfileRun(f, memory: memory, sessionRead: !memory);
        var reader = (ITenantRunProfileStore)f.Iam; Assert.Null(await reader.ReadTenantRunProfileAsync(s.Frame));
        var own = await Service(f).ReadAsync(s.Tenant, s.Human); Assert.Equal("Exact Run requester", own.Profile.Preferences["preferredName"].Value);
        Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new(0, 100))).Items);
    }
    [Theory] [InlineData(false, "tenant")] [InlineData(true, "tenant")] [InlineData(false, "principal")] [InlineData(true, "principal")]
    [InlineData(false, "account")] [InlineData(true, "account")] [InlineData(false, "revision")] [InlineData(true, "revision")]
    [InlineData(false, "time")] [InlineData(true, "time")] [InlineData(false, "generation")] [InlineData(true, "generation")]
    public async Task Tenant_Run_Profile_witness_cannot_change_actual_owner_version_time_or_live_generation(bool sqlite, string field)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await ProfileRun(f); var reader = (ITenantRunProfileStore)f.Iam;
        var view = Assert.IsType<TenantRunProfileObservation>(await reader.ReadTenantRunProfileAsync(s.Frame)); Assert.Equal(s.Human.PrincipalId, view.Source.PrincipalId);
        var source = field switch { "tenant" => view.Source with { TenantId = f.Ids.NewId() }, "principal" => view.Source with { PrincipalId = f.Ids.NewId() },
            "account" => view.Source with { AccountId = f.Ids.NewId() }, "revision" => view.Source with { Revision = view.Source.Revision + 1 },
            "time" => view.Source with { UpdatedAt = view.Source.UpdatedAt.AddTicks(1) }, _ => view.Source };
        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.RequireTenantProfileSourceAsync(new(s.Tenant, s.Frame.RunId), source,
            field == "generation" ? f.Ids.NewId() : s.Frame.Generation).AsTask());
        await reader.RequireTenantProfileProjectionAsync(s.Frame, view);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Run_Profile_refreshes_exact_values_and_refuses_old_projection_witnesses_without_mutating_preferences(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await ProfileRun(f); var reader = (ITenantRunProfileStore)f.Iam;
        var first = Assert.IsType<TenantRunProfileObservation>(await reader.ReadTenantRunProfileAsync(s.Frame));
        await reader.RequireTenantProfileSourceAsync(new(s.Tenant, s.Frame.RunId), first.Source, s.Frame.Generation);
        await Service(f).UpdateAsync(s.Tenant, s.Human, Change(await Service(f).ReadAsync(s.Tenant, s.Human), "Updated exact requester"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.RequireTenantProfileProjectionAsync(s.Frame, first).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => reader.RequireTenantProfileSourceAsync(new(s.Tenant, s.Frame.RunId), first.Source, s.Frame.Generation).AsTask());
        if (sqlite) reader = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns);
        var current = Assert.IsType<TenantRunProfileObservation>(await reader.ReadTenantRunProfileAsync(s.Frame)); Assert.Equal(first.Source.Revision + 1, current.Source.Revision);
        Assert.Equal("Updated exact requester", current.Profile.Preferences["preferredName"].Value); await reader.RequireTenantProfileProjectionAsync(s.Frame, current);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Run_Profile_checkpoint_cannot_discard_an_already_read_source_or_replace_its_owner(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await ProfileRun(f); var reader = (ITenantRunProfileStore)f.Iam;
        var view = Assert.IsType<TenantRunProfileObservation>(await reader.ReadTenantRunProfileAsync(s.Frame));
        var store = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam; var run = (await runs.FindTenantRunAsync(s.Tenant, s.Frame.RunId))!;
        AgentRunCheckpoint Checkpoint(TenantProfileSource? source) => new(AgentRunToolCallCheckpoint.Write([], executionState: new(TenantProfileSource: source)), 0, 0, 10000);
        run = await store.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), s.Frame.Generation, Checkpoint(view.Source), null), f.Ids.NewId());
        foreach (var changed in new[] { null, view.Source with { PrincipalId = f.Ids.NewId() } })
            await Assert.ThrowsAsync<AccessDeniedException>(() => store.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
                new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), s.Frame.Generation, Checkpoint(changed), null), f.Ids.NewId()).AsTask());
        Assert.Equal(view.Source, AgentRunToolCallCheckpoint.ReadExecutionState((await runs.FindTenantRunAsync(s.Tenant, run.AgentRunId))!.Checkpoint).TenantProfileSource);
    }
}
