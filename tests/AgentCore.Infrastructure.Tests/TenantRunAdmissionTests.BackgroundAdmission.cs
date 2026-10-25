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
    private static async Task<BackgroundSetup> BackgroundParent(Fixture f, bool createAuthority = true, string template = "general-assistant", int version = 32)
    {
        var setup = await Setup(f, template, version);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var contextRole = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Read execution context", ["spaces.read"], await Revision());
        await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Agent.PrincipalId, contextRole.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        Guid? assignment = null;
        if (createAuthority)
        {
            var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Background creator", ["sessions.create", "agent-runs.create"], await Revision());
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
    private static async Task<(SessionSnapshot Session, AgentRun Run)> BackgroundChild(Fixture f, BackgroundSetup parent, string call = "tool-call", string objective = "Private child task")
    {
        var current = (await f.RunConfiguration().ResolveInCapturedSpaceAsync(parent.Parent, default, selectionRevision: 1)).Configuration;
        return BackgroundSessionAdmissionFactory.ForImmediate(f.Ids.NewSessionId(), f.Ids.NewId(), f.Ids.NewId(), f.Ids.NewId(), f.Ids.NewId(),
            parent.Snapshot, parent.Parent, call, objective, "Owned task", true, f.Clock.GetUtcNow(),
            ExecutionBudgetResolver.Resolve(current.Configuration.Definition, current.InstanceBudgets, false), current);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_immediate_background_admission_atomically_preserves_causal_owner_and_exact_replay(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var parent = await BackgroundParent(f);
        var child = await BackgroundChild(f, parent); var store = (ITenantRuntimeStore)f.Iam;
        var before = (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision;
        var result = await store.AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId()); Assert.True(result.Created);
        var saved = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(parent.Setup.Tenant, child.Session.SessionId))!;
        Assert.Null(saved.Snapshot.ProfileId); Assert.Equal(parent.Setup.Owner.PrincipalId, saved.Owner.CreatedByPrincipalId);
        Assert.Equal(parent.Parent.Owner, result.Run.Owner); Assert.Null(result.Run.Owner.ProfileIdOrNull);
        Assert.True(result.Run.Admission.Configuration!.SpaceContext!.SameLocation(parent.Parent.Admission.Configuration!.SpaceContext));
        Assert.True(result.Run.Admission.Configuration.SpaceContext.MatchesSelection(saved.Snapshot.ContextSelection));
        var context = result.Run.Admission.ExecutionContext!;
        Assert.Equal(ExecutionOrigin.ImmediateBackground, context.Origin); Assert.Equal(parent.Parent.AgentRunId, context.ParentRunId);
        Assert.Equal(parent.Parent.ActivationId, context.ParentActivationId); Assert.Equal(parent.Parent.Admission.ExecutionContext!.RootActivationId, context.RootActivationId);
        Assert.Equal(parent.Setup.Owner.PrincipalId, context.OriginalInitiatorPrincipalId);
        Assert.Equal(parent.Parent.Owner.ExecutorPrincipalId, context.AgentPrincipalId);
        var retry = await BackgroundChild(f, parent); var repeated = await store.AdmitTenantImmediateAsync(parent.Frame, retry.Session, retry.Run, f.Ids.NewId());
        Assert.False(repeated.Created); Assert.Equal(result.Run.AgentRunId, repeated.Run.AgentRunId);
        Assert.Equal(before + 1, (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision);
        Assert.Single(await f.Iam.ReadAuditAsync(parent.Setup.Tenant, 100), a => a.Action == "agent-runs.background.admit");
        Assert.DoesNotContain("Private child task", System.Text.Json.JsonSerializer.Serialize(await f.Iam.ReadAuditAsync(parent.Setup.Tenant, 100)));
        var changed = await BackgroundChild(f, parent, objective: "Different task");
        await Assert.ThrowsAsync<SecurityConflictException>(() => store.AdmitTenantImmediateAsync(parent.Frame, changed.Session, changed.Run, f.Ids.NewId()).AsTask());
        Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(Guid.NewGuid(), child.Run.AgentRunId));
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
            Assert.Equal(context, (await reopened.FindTenantRunAsync(parent.Setup.Tenant, result.Run.AgentRunId))!.Admission.ExecutionContext);
            Assert.Equal(saved.Owner, (await reopened.FindTenantSessionAsync(parent.Setup.Tenant, saved.Owner.SessionId))!.Owner);
            var replay = await reopened.AdmitTenantImmediateAsync(parent.Frame, retry.Session, retry.Run, f.Ids.NewId()); Assert.False(replay.Created);
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
    public async Task Tenant_background_prepared_admission_rechecks_parent_authority_and_shape_without_orphans(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-20T01:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true);
        var parent = await BackgroundParent(f, boundary != "authority", boundary == "capability" ? "examiner" : "general-assistant", boundary == "capability" ? 1 : 32);
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
        var before = await Revision(); await Assert.ThrowsAsync<AccessDeniedException>(() => store.AdmitTenantImmediateAsync(frame, snapshot, child.Run, f.Ids.NewId()).AsTask());
        Assert.Null(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(parent.Setup.Tenant, child.Session.SessionId));
        Assert.Null(await ((ITenantRunStore)f.Iam).FindTenantRunAsync(parent.Setup.Tenant, child.Run.AgentRunId));
        Assert.Equal(before, await Revision()); Assert.DoesNotContain(await f.Iam.ReadAuditAsync(parent.Setup.Tenant, 100), a => a.Action == "agent-runs.background.admit");
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_background_exact_retry_survives_capacity_but_new_third_child_has_no_effect(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var parent = await BackgroundParent(f);
        var store = (ITenantRuntimeStore)f.Iam;
        for (var i = 0; i < AgentRunLimits.MaxImmediateChildren; i++)
        {
            var child = await BackgroundChild(f, parent, "call-" + i);
            Assert.True((await store.AdmitTenantImmediateAsync(parent.Frame, child.Session, child.Run, f.Ids.NewId())).Created);
        }
        var retry = await BackgroundChild(f, parent, "call-0");
        Assert.False((await store.AdmitTenantImmediateAsync(parent.Frame, retry.Session, retry.Run, f.Ids.NewId())).Created);
        var extra = await BackgroundChild(f, parent, "over-capacity");
        var revision = (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision;
        await Assert.ThrowsAsync<AgentCoreException>(() => store.AdmitTenantImmediateAsync(parent.Frame, extra.Session, extra.Run, f.Ids.NewId()).AsTask());
        Assert.Null(await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(parent.Setup.Tenant, extra.Session.SessionId));
        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(parent.Setup.Tenant))!.Tenant.Revision);
    }

}
