using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private sealed record BackgroundSetup(Environment Setup, AgentRun Parent, SessionSnapshot Snapshot, TenantRuntimeFrame Frame, Guid? Assignment);
    private static async Task<BackgroundSetup> BackgroundParent(Fixture f, bool createAuthority = true, string template = "general-assistant", int version = 35)
    {
        var setup = await Setup(f, template, version);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var contextRole = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Read execution context", ["spaces.read"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Agent.PrincipalId, contextRole.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        Guid? assignment = null;
        if (createAuthority)
        {
            var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Explicit Task fixture execution", ["sessions.create", "agent-runs.create", "tasks.delegate", "tasks.read", "tasks.cancel", "agents.read", "agents.chat", "sessions.read", "agent-messages.read", "agent-messages.send"], await Revision());
            assignment = (await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision())).AssignmentId;
        }
        var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Session, setup.Owner, f.Ids.NewId(), "Perform a background check", await Revision(), 1);
        var store = (ITenantRuntimeStore)f.Iam;
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, receipt.RunId))!;
        run = await store.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
        return new(setup, run, (await store.LoadTenantRuntimeAsync(frame))!, frame, assignment);
    }
    private sealed record PreparedTaskFixture(SessionSnapshot Session, AgentRun Run, TaskAdmission Admission);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Fixture, Dictionary<Guid, PreparedTaskFixture>> TaskFixtures = new();
    private static AgentCore.Application.Tasks.TenantTaskService FixtureTasks(Fixture f) => new(f.Iam, (ITenantTaskStore)f.Iam,
        (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, (ITenantSpaceStore)f.Iam, f.RunConfiguration(),
        new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(f.Iam), f.Authorizer, f.Ids, f.Clock);
    private static async Task<PreparedTaskFixture> BackgroundChild(Fixture f, BackgroundSetup parent, string call = "tool-call", string objective = "Private child task", Guid? targetInstance = null)
    {
        var tenant = parent.Setup.Tenant; var source = parent.Parent;
        var worker = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(tenant, targetInstance ?? source.AgentInstanceId))!;
        if (worker.Instance.Cooperation?.AcceptTasks != true)
            worker = await f.AgentConfiguration().UpdateCooperationAsync(parent.Setup.Owner.PrincipalId, tenant, worker.Instance.InstanceId,
                new(AcceptTasks: true), (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, worker.Instance.Revision, initiator: parent.Setup.Owner);
        var prepared = await FixtureTasks(f).PrepareAsync(tenant, parent.Frame.SessionId, new(Agent: parent.Frame, Tool: "tasks.delegate"),
            new(objective, "Owned task", [worker.Instance.InstanceId], false, null, null, [], false, null, call),
            (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        if (prepared.Replay is { } replay) return TaskFixtures.GetOrCreateValue(f)[replay.Task.InitialWorkerRunId!.Value];
        var admission = prepared.Admission!; var fixture = new PreparedTaskFixture(admission.WorkerSession!, admission.WorkerRun!, admission);
        TaskFixtures.GetOrCreateValue(f)[fixture.Run.AgentRunId] = fixture; return fixture;
    }
    private static async ValueTask<AgentRunAdmissionResult> AdmitTaskFixture(Fixture f, TenantRuntimeFrame frame, SessionSnapshot session, AgentRun run, Guid audit)
    {
        var fixture = TaskFixtures.GetOrCreateValue(f)[run.AgentRunId];
        var result = await ((ITenantTaskStore)f.Iam).AdmitTaskAsync(fixture.Admission with { Actor = new(Agent: frame, Tool: "tasks.delegate"),
            WorkerSession = session, WorkerRun = run, AuditId = audit, ExpectedSecurityRevision = (await f.Iam.ReadTenantAsync(fixture.Admission.TenantId))!.Tenant.Revision });
        return new(!result.Replayed, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(result.Task.Source.TenantId, result.Task.InitialWorkerRunId!.Value))!);
    }

    private static async Task<(SessionSnapshot Session, AgentRun Run)> IndependentBackgroundChild(Fixture f, BackgroundSetup parent, string call = "independent", string objective = "Independent report source")
    {
        var tenant = parent.Setup.Tenant; var human = parent.Setup.Owner;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Independent occurrence fixture", ["automations.run"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, parent.Setup.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision());
        var models = new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(f.Iam);
        var service = new TenantAutomationService(f.Iam, (ITenantAutomationStore)f.Iam, (ITenantAgentStore)f.Iam,
            f.RunConfiguration(), f.Identity, f.Authorizer, models, f.Ids, f.Clock, sessions: (ITenantSessionStore)f.Iam);
        var current = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(tenant, parent.Setup.Agent.Instance.InstanceId))!;
        var catalog = await models.ReadAsync((await f.Iam.ReadTenantAsync(tenant))!);
        var automation = await service.SaveAsync(tenant, current.Instance.InstanceId, null, human,
            new(true, "Independent " + call, objective, new AgentCore.Domain.Triggers.OneShotSchedule(f.Clock.GetUtcNow().AddHours(1), "UTC"),
                AgentCore.Domain.Models.ModelPreference.Fixed(catalog.DefaultOfferingId), ReportToSessionId: parent.Frame.SessionId),
            await Revision(), current.Instance.Revision, 0);
        var result = await service.RunAsync(tenant, automation.AutomationId, human, await Revision(), current.Instance.Revision, automation.Revision);
        Assert.True(result.Created); Assert.Null(result.ApprovalRequestId);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, result.RunId!.Value))!;
        return ((await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, run.SessionId))!.Snapshot, run);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Task_admission_atomically_preserves_causal_owner_and_exact_replay(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var parent = await BackgroundParent(f);
        var child = await BackgroundChild(f, parent); var store = (ITenantRuntimeStore)f.Iam;
        var before = (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision;
        var result = await AdmitTaskFixture(f, parent.Frame, child.Session, child.Run, f.Ids.NewId()); Assert.True(result.Created);
        var saved = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(parent.Setup.Tenant, child.Session.SessionId))!;
        Assert.Null(saved.Snapshot.ProfileId); Assert.Equal(parent.Setup.Owner.PrincipalId, saved.Owner.CreatedByPrincipalId);
        Assert.Equal(parent.Parent.Owner, result.Run.Owner); Assert.Null(result.Run.Owner.ProfileIdOrNull);
        Assert.True(result.Run.Admission.Configuration!.SpaceContext!.SameLocation(parent.Parent.Admission.Configuration!.SpaceContext));
        Assert.True(result.Run.Admission.Configuration.SpaceContext.MatchesSelection(saved.Snapshot.ContextSelection));
        var context = result.Run.Admission.ExecutionContext!;
        Assert.Equal(ExecutionOrigin.DelegatedTask, context.Origin); Assert.Equal(parent.Parent.AgentRunId, context.ParentRunId);
        Assert.Equal(parent.Parent.ActivationId, context.ParentActivationId); Assert.Equal(parent.Parent.Admission.ExecutionContext!.RootActivationId, context.RootActivationId);
        Assert.Equal(parent.Setup.Owner.PrincipalId, context.OriginalInitiatorPrincipalId);
        Assert.Equal(parent.Parent.Owner.ExecutorPrincipalId, context.AgentPrincipalId);
        var retry = await BackgroundChild(f, parent); var repeated = await AdmitTaskFixture(f, parent.Frame, retry.Session, retry.Run, f.Ids.NewId());
        Assert.False(repeated.Created); Assert.Equal(result.Run.AgentRunId, repeated.Run.AgentRunId);
        Assert.Equal(before + 1, (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision);
        Assert.Single(await f.Iam.ReadAuditAsync(parent.Setup.Tenant, 100), a => a.Action == "tasks.delegate");
        Assert.DoesNotContain("Private child task", System.Text.Json.JsonSerializer.Serialize(await f.Iam.ReadAuditAsync(parent.Setup.Tenant, 100)));
        await Assert.ThrowsAsync<SecurityConflictException>(() => BackgroundChild(f, parent, objective: "Different task"));
        Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(Guid.NewGuid(), child.Run.AgentRunId));
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
            Assert.Equal(context, (await reopened.FindTenantRunAsync(parent.Setup.Tenant, result.Run.AgentRunId))!.Admission.ExecutionContext);
            Assert.Equal(saved.Owner, (await reopened.FindTenantSessionAsync(parent.Setup.Tenant, saved.Owner.SessionId))!.Owner);
            var replay = await AdmitTaskFixture(f, parent.Frame, retry.Session, retry.Run, f.Ids.NewId()); Assert.False(replay.Created);
        }
    }

    [Theory]
    [InlineData(false,"generation")] [InlineData(true,"generation")]
    [InlineData(false,"tenant")] [InlineData(true,"tenant")]
    [InlineData(false,"authority")] [InlineData(true,"authority")]
    [InlineData(false,"revoked")] [InlineData(true,"revoked")]
    [InlineData(false,"expired")] [InlineData(true,"expired")]
    [InlineData(false,"cancelled")] [InlineData(true,"cancelled")]
    [InlineData(false,"review")] [InlineData(true,"review")]
    [InlineData(false,"snapshot")] [InlineData(true,"snapshot")]
    [InlineData(false,"capability")] [InlineData(true,"capability")]
    public async Task Task_prepared_admission_rechecks_parent_authority_and_shape_without_orphans(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T01:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true);
        var parent = await BackgroundParent(f, boundary != "authority", boundary == "capability" ? "examiner" : "general-assistant", boundary == "capability" ? 1 : 35);
        if (boundary is "authority" or "capability")
        {
            var revision = (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision;
            await Assert.ThrowsAsync<AccessDeniedException>(() => BackgroundChild(f, parent));
            Assert.Equal(revision + 1, (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision); // explicit target opt-in only
            Assert.Single((await ((ITenantSessionStore)f.Iam).ListTenantSessionsAsync(parent.Setup.Tenant, null, 0, 100)).Items);
            return;
        }
        var child = await BackgroundChild(f, parent); var frame = parent.Frame; var snapshot = child.Session; var store = (ITenantRuntimeStore)f.Iam;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision;
        switch (boundary)
        {
            case "generation": frame = frame with { Generation = f.Ids.NewId() }; break;
            case "tenant": frame = frame with { Owner = AgentRunOwner.ForTenant(f.Ids.NewId(), frame.Owner.AgentInstanceId, frame.Owner.ExecutorPrincipalId!.Value) }; break;
            case "revoked": await f.Access.RevokeAssignmentAsync(parent.Setup.Owner.PrincipalId, parent.Setup.Tenant, parent.Assignment!.Value, await Revision()); break;
            case "expired": clock.Advance(TimeSpan.FromMinutes(6)); break;
            case "cancelled": await store.ApplyTenantRuntimeCommandAsync(frame.Owner, frame.RunId, new AgentRunCommand.RequestCancellation(parent.Parent.Revision, clock.GetUtcNow(), "test"), f.Ids.NewId()); break;
            case "snapshot": snapshot = snapshot with { Surfaces = SessionSurface.ChatList, Summary = "Injected unrelated content" }; break;
            case "review": var service = new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock); await service.SaveAsync(parent.Setup.Tenant, parent.Setup.Owner, "agent-runs.create", new(OperationApprovalMode.Always, false), await Revision(), 0); break;
        }
        var before = await Revision(); await Assert.ThrowsAsync<AccessDeniedException>(() => AdmitTaskFixture(f, frame, snapshot, child.Run, f.Ids.NewId()).AsTask());
        Assert.Null(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(parent.Setup.Tenant, child.Session.SessionId));
        Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(parent.Setup.Tenant, child.Run.AgentRunId));
        Assert.Equal(before, await Revision()); Assert.DoesNotContain(await f.Iam.ReadAuditAsync(parent.Setup.Tenant, 100), a => a.Action == "tasks.delegate");
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Task_exact_retry_survives_capacity_but_new_third_child_has_no_effect(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var parent = await BackgroundParent(f);
        var store = (ITenantRuntimeStore)f.Iam;
        for (var i = 0; i < AgentCore.Domain.Tasks.TaskLimits.MaxChildrenPerRun; i++)
        {
            var child = await BackgroundChild(f, parent, "call-" + i);
            Assert.True((await AdmitTaskFixture(f, parent.Frame, child.Session, child.Run, f.Ids.NewId())).Created);
        }
        var retry = await BackgroundChild(f, parent, "call-0");
        Assert.False((await AdmitTaskFixture(f, parent.Frame, retry.Session, retry.Run, f.Ids.NewId())).Created);
        var revision = (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision;
        await Assert.ThrowsAsync<AgentCoreException>(() => BackgroundChild(f, parent, "over-capacity"));
        Assert.Equal(3, (await ((ITenantSessionStore)f.Iam).ListTenantSessionsAsync(parent.Setup.Tenant, null, 0, 100)).Items.Count);
        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision);
    }

}
