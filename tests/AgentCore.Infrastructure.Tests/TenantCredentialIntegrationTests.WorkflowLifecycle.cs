using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Workflows;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Workflows_source_Session_deletion_closes_the_actual_bookmark_and_cannot_reappear(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Closure", "workflow-closure");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        var initiator = new TenantSessionInitiator(human.AccountId, human.AuthenticationSessionId, human.PrincipalId);
        var session = await f.Sessions.CreateAsync(tenant, agent.Instance.InstanceId, initiator, SessionMode.Text, await Revision(), agent.Instance.Revision);
        await using var vendor = WorkflowRuntimeProvider(f, sqlite);
        if (sqlite) await vendor.MigrateFormalWorkflowRuntimeAsync(); await vendor.PopulateFormalWorkflowActivitiesAsync();
        var runtime = vendor.GetRequiredService<IFormalWorkflowRuntime>(); var catalog = Workflows(f, runtime); var workflow = Guid.NewGuid();
        var graph = HumanWorkflow(human.PrincipalId) with { Inputs = [new("private", "Private source data", WorkflowInputKind.Text, true)] };
        await catalog.SaveAsync(tenant, workflow, human, new("Source-owned process", "", graph, false, 0, await Revision(), "draft"));
        await catalog.PublishAsync(tenant, workflow, human, new(1, await Revision(), "publish"));
        var bridge = new TenantWorkflowExecutionService(f.Iam, (ITenantWorkflowCatalogStore)f.Iam, (ITenantWorkflowExecutionStore)f.Iam,
            (ITenantWorkflowTaskStore)f.Iam, (ITenantTaskStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, (ITenantSpaceStore)f.Iam,
            catalog, f.RunConfiguration(), new SyntheticTenantModelCatalogReader(f.Iam), runtime, f.Identity, f.Authorizer, f.Ids, f.Clock);
        using var input = JsonDocument.Parse("{\"private\":\"source-only confidential input\"}");
        var start = new WorkflowStart(1, input.RootElement, "start", await Revision(), SourceSessionId: session.Owner.SessionId);
        var execution = await bridge.StartHumanAsync(tenant, workflow, human, start);
        var completedExecution = await bridge.StartHumanAsync(tenant, workflow, human, start with { RequestKey = "completed-before-closure", ExpectedSecurityRevision = await Revision() });
        await catalog.SaveAsync(tenant, workflow, human, new("Source-owned process", "", graph, true, 2, await Revision(), "retire"));
        Assert.Equal(execution, await bridge.StartHumanAsync(tenant, workflow, human, start));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bridge.StartHumanAsync(tenant, workflow, human, start with { RequestKey = "new-after-retirement", ExpectedSecurityRevision = 0 }).AsTask());
        await bridge.ReconcileAsync(4, default);
        Assert.Single((await runtime.ReadAsync(tenant, execution.ExecutionId))!.Bookmarks);
        var processes = (ITenantWorkflowExecutionStore)f.Iam;
        var completedWaiting = (await processes.FindWorkflowExecutionAsync(tenant, completedExecution.ExecutionId))!;
        var completedStep = Assert.Single(await processes.ReadWorkflowStepsAsync(tenant, completedExecution.ExecutionId));
        var decision = await bridge.DecideHumanAsync(new(tenant, completedExecution.ExecutionId, "review", human, true,
            "Private source-specific sign-off rationale", "accept-before-closure", completedWaiting.Revision, completedStep.Revision, await Revision(), Guid.NewGuid()));
        for (var pass = 0; pass < 8; pass++) await bridge.ReconcileAsync(4, default);
        Assert.Equal(WorkflowExecutionStatus.Completed, (await processes.FindWorkflowExecutionAsync(tenant, completedExecution.ExecutionId))!.Status);
        var current = (await processes.FindWorkflowExecutionAsync(tenant, execution.ExecutionId))!;
        await Assert.ThrowsAsync<AccessDeniedException>(() => processes.WriteWorkflowExecutionAsync(new(current with { Revision = current.Revision + 1, SourceClosure = new("workflow_source_session_ended", f.Clock.GetUtcNow()) }, current.Revision, Guid.NewGuid())).AsTask());
        var savedSession = await f.Sessions.ReadAsync(tenant, session.Owner.SessionId, initiator);
        await f.Sessions.ChangeAsync(tenant, initiator, new(session.Owner.SessionId, savedSession.Revision, TenantSessionAction.Delete), await Revision());
        await bridge.ReconcileAsync(4, default);
        current = (await processes.FindWorkflowExecutionAsync(tenant, execution.ExecutionId))!;
        Assert.Equal(WorkflowExecutionStatus.Expired, current.Status); Assert.Equal("workflow_source_session_ended", current.SourceClosure!.ReasonCode);
        Assert.True((await runtime.ReadAsync(tenant, execution.ExecutionId))!.Cancelled);
        Assert.Null(current.Cancellation); Assert.False(current.CancellationRequested);
        await bridge.ReconcileAsync(4, default); Assert.Equal(current, await processes.FindWorkflowExecutionAsync(tenant, execution.ExecutionId));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bridge.ReadHumanAsync(tenant, current.ExecutionId, human).AsTask());
        Assert.Equal(0, (await bridge.ListHumanAsync(tenant, human, 0, 20)).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => bridge.RecoverHumanStartAsync(tenant, human, "start").AsTask());
        Assert.Null(current.ContentRetiredAt);
        Assert.True(await ((ITenantWorkingCleanupStore)f.Iam).RetireTenantRunContentAsync(session.Owner));
        var minimized = (await processes.FindWorkflowExecutionAsync(tenant, execution.ExecutionId))!;
        Assert.NotNull(minimized.ContentRetiredAt); Assert.Equal("{}", minimized.InputsJson);
        Assert.Equal(current.InputsHash, minimized.InputsHash); Assert.Equal(current.Fingerprint, minimized.Fingerprint);
        Assert.Equal(current.Revision + 1, minimized.Revision);
        WorkflowExecutionRules.Validate(minimized, (await ((ITenantWorkflowCatalogStore)f.Iam).FindWorkflowVersionAsync(tenant, workflow, 1))!);
        var minimizedDecision = (await processes.FindWorkflowDecisionAsync(tenant, completedExecution.ExecutionId, "review"))!;
        Assert.Null(minimizedDecision.Rationale); Assert.NotNull(minimizedDecision.ContentRetiredAt);
        Assert.Equal(decision.Fingerprint, minimizedDecision.Fingerprint); Assert.Equal(decision.Approved, minimizedDecision.Approved);
        current = minimized;
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, clock: f.Clock);
            Assert.Equal(current, await reopened.FindWorkflowExecutionAsync(tenant, execution.ExecutionId));
        }
    }
}
