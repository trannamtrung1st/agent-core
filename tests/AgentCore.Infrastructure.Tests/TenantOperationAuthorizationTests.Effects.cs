using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Approved_persona_effect_commits_once_with_exact_receipt_grant_and_Run_checkpoint_and_survives_reopen(bool sqlite, bool review)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await EffectSetup(f, review);
        var service = Effects(f); var request = setup.Request;
        var result = await service.ExecuteAsync(new(setup.Environment.Tenant, setup.Run.AgentRunId), request.RequestId,
            request.Operation.OperationId, setup.Generation, request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Completed, result.Outcome); Assert.Equal("operation_completed", result.ReasonCode);
        Assert.Equal(3, result.InstanceRevision); Assert.Equal(2, result.PersonaRevision);
        var changed = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId))!;
        Assert.Equal(request.Operation.Persona, changed.Instance.Persona);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(request.TenantId, setup.Run.AgentRunId))!;
        Assert.Equal(AgentRunStatus.Running, run.Status); Assert.Equal(setup.Run.AttemptCount, run.AttemptCount);
        Assert.Equal(AgentRunSideEffectDisposition.Succeeded, run.SideEffect.Disposition); Assert.Equal(request.Operation.ActionHash, run.SideEffect.ActionHash);
        Assert.Equal(setup.Run.Revision + 1, run.Revision);
        var grant = await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(request.TenantId, request.RequestId);
        if (review) Assert.Null(grant); else Assert.Equal(OperationGrantStatus.Consumed, grant!.Status);
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, clock: f.Clock) : f.Iam;
        var replay = await Effects(f, reopened).ExecuteAsync(new(request.TenantId, setup.Run.AgentRunId), request.RequestId,
            request.Operation.OperationId, setup.Generation, request.Operation.ActionHash);
        Assert.Equal(result, replay);
        Assert.Equal(3, (await ((ITenantAgentStore)reopened).FindTenantAgentAsync(request.TenantId, request.Operation.InstanceId))!.Instance.Revision);
        Assert.Single(await f.Iam.ReadAuditAsync(request.TenantId, 200), a => a.Action == "operations.persona.apply");
        if (!review) Assert.DoesNotContain((await f.Iam.ReadTenantAsync(request.TenantId))!.Assignments, a => a.PrincipalId == request.ExecutorPrincipalId);
        ICoreEventStore events = f.Contexts is null ? ((InMemoryAgentInstanceStore)f.Instances).CoreEvents : new SqliteCoreEventStore(f.Contexts);
        Assert.DoesNotContain(await events.PendingAsync(), e => e.Event.Owner.AgentInstanceId == request.Operation.InstanceId && e.Event.Key == "instance.config_changed");
    }

    private sealed record EffectEnvironment(Environment Environment, TenantApprovalRequest Request, AgentRun Run, Guid Generation, TenantSessionInitiator Grantor);
    private static async Task<EffectEnvironment> EffectSetup(Fixture f, bool review = false, bool independentGrantor = false, bool managementEnabled = true)
    {
        var setup = await Setup(f); var state = (await f.Iam.ReadTenantAsync(setup.Tenant))!; var at = f.Clock.GetUtcNow();
        var policy = new HarnessManagementState(new(HarnessManagementMode.Assisted, [HarnessManagementScope.Settings], [], []), 2, Overrides: new(Mode: new(HarnessManagementMode.Assisted), Scopes: new([HarnessManagementScope.Settings])));
        var configured = managementEnabled ? await ((ITenantAgentStore)f.Iam).TryChangeTenantAgentConfigurationAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } },
            state.Tenant.Revision, new(setup.Agent.Instance.InstanceId, 1, HarnessManagement: policy), at,
            new(f.Ids.NewId(), setup.Tenant, setup.Owner.PrincipalId, "agents.management.update", setup.Agent.Instance.InstanceId.ToString("D"), "allowed", "configuration_updated", at)) : setup.Agent;
        Assert.NotNull(configured);
        if (review)
        {
            await Grant(f, setup, setup.Agent.PrincipalId, "Executor persona", ["agents.update"]);
            await Policies(f).SaveAsync(setup.Tenant, setup.Owner, "agents.update", new(OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 0);
        }
        var session = await f.Sessions.CreateAsync(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner, SessionMode.Text,
            (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, configured.Instance.Revision);
        var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, session.Owner.SessionId, setup.Owner, f.Ids.NewId(), "Update my persona after exact approval",
            (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        var approval = ApprovalService(f);
        var request = (await approval.PreparePersonaAsync(new(setup.Tenant, receipt.RunId), configured.Instance.Persona with { Name = "Exactly approved persona" }, 1)).Request!;
        Assert.Equal(review ? ApprovalKind.Review : ApprovalKind.ExceptionalGrant, request.Kind);
        var grantor = independentGrantor ? await User(f, setup, "grantor@example.test", ["agents.update", "approvals.read", "approvals.approve", "approvals.grant_exception"]) : setup.Owner;
        await approval.DecideAsync(setup.Tenant, request.RequestId, grantor, true, request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        Assert.Equal(1, await approval.DispatchPendingAsync());
        await f.Identity.SignOutAsync(setup.Owner.AuthenticationSessionId);
        if (independentGrantor) await f.Identity.SignOutAsync(grantor.AuthenticationSessionId);
        var generation = f.Ids.NewId(); var queued = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, receipt.RunId))!; at = f.Clock.GetUtcNow();
        var run = queued.TakeClaim(generation, at, at.AddMinutes(5));
        await SaveEffectTestRun(f, run);
        return new(setup, request, run, generation, grantor);
    }
    private static async Task SaveEffectTestRun(Fixture f, AgentRun run)
    {
        if (f.Contexts is not null)
        {
            await using var db = await f.Contexts.CreateDbContextAsync();
            var row = await db.AgentRuns.SingleAsync(r => r.AgentRunId == run.AgentRunId.ToString("D")); AgentRunStoreMapping.Apply(row, run); await db.SaveChangesAsync();
        }
        else { var memory = Assert.IsType<InMemoryMemoryStore>(f.Memory); lock (memory.AdmissionGate) memory.AgentRuns.Runs[run.AgentRunId] = run; }
    }
    private static TenantApprovedOperationService Effects(Fixture f, IIamStore? store = null, ITenantApprovedOperationStore? effects = null, TenantArtifactRuntimeReadService? artifactReads = null, TenantWorkspaceRuntimeReadService? workspaceReads = null, TenantArtifactSourceTarget? artifactSources = null, TenantBackgroundCreationTarget? backgroundCreations = null)
    {
        var current = store ?? f.Iam; var authorizer = new IamAuthorizer(current);
        return new(current, (ITenantRunStore)current, (ITenantAgentStore)current, (ITenantApprovalStore)current,
            effects ?? (ITenantApprovedOperationStore)current, new(current, (ITenantRunStore)current, (ITenantAgentStore)current, authorizer, new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, f.Clock)),
            f.RunConfiguration(), f.Ids, f.Clock, new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(current), AgentCore.Application.Ports.SyntheticProviderAliases.Default, artifactReads: artifactReads, workspaceReads: workspaceReads, artifactSources: artifactSources, backgroundCreations: backgroundCreations);
    }
}
