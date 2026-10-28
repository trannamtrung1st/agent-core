using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Workflows;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Workflows_application_start_receipt_and_recovery_wait_for_genuine_human_decision(bool sqlite, bool approved)
    {
        var clock = new NativeEvaluationClock(); await using var f = await Fixture.Create(sqlite, clock);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Workflow bridge", "workflow-bridge");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new HumanSessionReference(owner.AccountId, login.SessionId, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var catalog = Workflows(f); var id = Guid.NewGuid();
        var graph = HumanWorkflow(human.PrincipalId);
        graph = graph with { Nodes = graph.Nodes.Append(new WorkflowNode("branch", "Recorded decision route", WorkflowNodeKind.Branch,
                Branch: new("review", WorkflowFactKind.DecisionApproved))).ToArray(),
            Edges = graph.Edges.Select(e => e.Id == "b" ? e with { To = "branch" } : e).Concat([
                new WorkflowEdge("yes", "branch", "done", "true"), new WorkflowEdge("no", "branch", "refused", "false")]).ToArray() };
        await catalog.SaveAsync(tenant, id, human, new("Human process", "", graph, false, 0, await Revision(), "bridge-draft"));
        await catalog.PublishAsync(tenant, id, human, new(1, await Revision(), "bridge-publish"));
        await using var provider = WorkflowRuntimeProvider(f, sqlite);
        if (sqlite) await provider.MigrateFormalWorkflowRuntimeAsync(); await provider.PopulateFormalWorkflowActivitiesAsync();
        var engine = provider.GetRequiredService<IFormalWorkflowRuntime>();
        var service = new TenantWorkflowExecutionService(f.Iam, (ITenantWorkflowCatalogStore)f.Iam, (ITenantWorkflowExecutionStore)f.Iam,
            (ITenantWorkflowTaskStore)f.Iam, (ITenantTaskStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, (ITenantSpaceStore)f.Iam,
            catalog, f.RunConfiguration(), new SyntheticTenantModelCatalogReader(f.Iam), engine, f.Identity, f.Authorizer, f.Ids, f.Clock);
        using var input = JsonDocument.Parse("{}");
        var request = new WorkflowStart(1, input.RootElement, "start-once", await Revision());
        async Task<WorkflowExecution?> StartOnce()
        {
            try { return await service.StartHumanAsync(tenant, id, human, request); }
            catch (AgentCore.Application.Security.SecurityConflictException) { return null; }
        }
        var simultaneous = await Task.WhenAll(StartOnce(), StartOnce());
        var accepted = simultaneous.First(value => value is not null)!;
        Assert.All(simultaneous.Where(value => value is not null), value => Assert.Equal(accepted.ExecutionId, value!.ExecutionId));
        Assert.Equal(1, (await ((ITenantWorkflowExecutionStore)f.Iam).ListWorkflowExecutionsAsync(tenant, null, 0, 20)).Total);
        Assert.Equal(WorkflowExecutionStatus.PendingAdmission, accepted.Status); Assert.Null(accepted.CoordinatorInstanceId);
        Assert.Equal(accepted.ExecutionId, (await service.StartHumanAsync(tenant, id, human, request)).ExecutionId);
        Assert.Equal(accepted.ExecutionId, (await service.RecoverHumanStartAsync(tenant, human, request.RequestKey))!.ExecutionId);
        // Elsa may persist a bookmark before the Core operation reservation is acknowledged.
        var created = await engine.StartAsync((await catalog.VersionAsync(tenant, id, 1, human)), accepted.ExecutionId);
        var originalBookmark = Assert.Single(created.Bookmarks);
        Assert.Empty(await ((ITenantWorkflowExecutionStore)f.Iam).ReadWorkflowStepsAsync(tenant, accepted.ExecutionId));
        await service.ReconcileAsync(8, default);
        var store = (ITenantWorkflowExecutionStore)f.Iam;
        var waiting = (await store.FindWorkflowExecutionAsync(tenant, accepted.ExecutionId))!;
        Assert.Equal(WorkflowExecutionStatus.WaitingForHuman, waiting.Status);
        var step = Assert.Single(await store.ReadWorkflowStepsAsync(tenant, accepted.ExecutionId));
        Assert.Equal(originalBookmark.BookmarkId, step.BookmarkId);
        Assert.Equal("review", step.NodeId); Assert.Equal(WorkflowOperationStatus.Reserved, step.Status);
        Assert.Null(step.DecisionActorId); Assert.Null(step.Outcome); Assert.False(step.ProofAccepted);
        await service.ReconcileAsync(8, default);
        Assert.Equal(step, Assert.Single(await store.ReadWorkflowStepsAsync(tenant, accepted.ExecutionId)));
        Assert.Equal("review", Assert.Single((await engine.ReadAsync(tenant, accepted.ExecutionId))!.Bookmarks).NodeId);
        await Assert.ThrowsAsync<AgentCore.Application.Security.AccessDeniedException>(async () =>
            await store.WriteWorkflowStepAsync(new(step with { Revision = 2, Status = WorkflowOperationStatus.Settled,
                DecisionActorId = human.PrincipalId, Outcome = "approve", ProofAccepted = true, SettledAt = f.Clock.GetUtcNow() }, 1, f.Ids.NewId())));
        await Assert.ThrowsAsync<AgentCore.Application.Security.AccessDeniedException>(async () =>
            await store.WriteWorkflowExecutionAsync(new(waiting with { Revision = waiting.Revision + 1,
                Status = WorkflowExecutionStatus.Completed, TerminalAt = f.Clock.GetUtcNow() }, waiting.Revision, f.Ids.NewId())));
        var decision = new WorkflowDecisionCommand(tenant, accepted.ExecutionId, "review", human, approved, "Reviewed the exact pinned process",
            "decision-once", waiting.Revision, step.Revision, await Revision(), f.Ids.NewId());
        var receipt = await service.DecideHumanAsync(decision);
        Assert.Equal(receipt, await service.DecideHumanAsync(decision));
        await Assert.ThrowsAsync<AgentCore.Application.Security.SecurityConflictException>(async () => await service.DecideHumanAsync(decision with { Approved = !approved }));
        Assert.Equal(approved, receipt.Approved);
        var view = await service.ReadHumanAsync(tenant, accepted.ExecutionId, human);
        Assert.Equal(1, view.RequiredCount); Assert.Equal(approved ? 1 : 0, view.ProvenCount);
        Assert.False(view.Steps.Single(s => s.NodeId == "review").CanDecide);
        Assert.Equal(1, (await service.ListHumanAsync(tenant, human, 0, 20)).Total);

        // Simulate an interruption after Elsa burns the bookmark and before the Core consumed marker.
        await engine.ResumeAsync(tenant, accepted.ExecutionId, step.BookmarkId, step.NodeId, approved ? "approve" : "reject");
        for (var i = 0; i < 8; i++) await service.ReconcileAsync(8, default);
        var terminal = (await store.FindWorkflowExecutionAsync(tenant, accepted.ExecutionId))!;
        Assert.Equal(approved ? WorkflowExecutionStatus.Completed : WorkflowExecutionStatus.Failed, terminal.Status);
        Assert.All(await store.ReadWorkflowStepsAsync(tenant, accepted.ExecutionId), saved => Assert.Equal(WorkflowOperationStatus.SignalConsumed, saved.Status));
        Assert.True((await engine.ReadAsync(tenant, accepted.ExecutionId))!.Finished);
        var proof = WorkflowProofRules.Evaluate((await ((ITenantWorkflowCatalogStore)f.Iam).FindWorkflowVersionAsync(tenant, id, 1))!, await store.ReadWorkflowStepsAsync(tenant, accepted.ExecutionId));
        Assert.Equal(approved, proof.CanSucceed);
        var second = await service.StartHumanAsync(tenant, id, human, request with { RequestKey = "second-start", ExpectedSecurityRevision = await Revision() });
        if (approved)
        {
            var cancel = new WorkflowCancelCommand(tenant, second.ExecutionId, human, second.Revision, await Revision(), "cancel-once", f.Ids.NewId());
            var cancelled = await service.CancelHumanAsync(cancel); Assert.True(cancelled.CancellationRequested);
            Assert.Equal(cancelled, await service.CancelHumanAsync(cancel));
            await service.ReconcileAsync(8, default);
            Assert.Equal(WorkflowExecutionStatus.Cancelled, (await store.FindWorkflowExecutionAsync(tenant, second.ExecutionId))!.Status);
            Assert.Null(await engine.ReadAsync(tenant, second.ExecutionId));
            Assert.Empty(await store.ReadWorkflowStepsAsync(tenant, second.ExecutionId));
        }
        else
        {
            await service.ReconcileAsync(8, default);
            clock.Advance(TimeSpan.FromHours(73));
            var renewed = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
            var freshHuman = human with { AuthenticationSessionId = renewed.SessionId };
            var pending = (await store.FindWorkflowExecutionAsync(tenant, second.ExecutionId))!;
            var pendingStep = Assert.Single(await store.ReadWorkflowStepsAsync(tenant, second.ExecutionId));
            await Assert.ThrowsAsync<AgentCore.Application.Security.AccessDeniedException>(async () => await service.DecideHumanAsync(
                new(tenant, second.ExecutionId, "review", freshHuman, true, "Late approval", "too-late", pending.Revision, pendingStep.Revision, await Revision(), f.Ids.NewId())));
            await service.ReconcileAsync(8, default);
            Assert.Equal(WorkflowExecutionStatus.Expired, (await store.FindWorkflowExecutionAsync(tenant, second.ExecutionId))!.Status);
            Assert.True((await engine.ReadAsync(tenant, second.ExecutionId))!.Cancelled);
            Assert.Null(await store.FindWorkflowDecisionAsync(tenant, second.ExecutionId, "review"));
        }
        if (sqlite)
        {
            var reopened = (ITenantWorkflowExecutionStore)new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts!, clock: f.Clock);
            Assert.Equal(receipt, await reopened.FindWorkflowDecisionAsync(tenant, accepted.ExecutionId, "review"));
            Assert.Equal(terminal, await reopened.FindWorkflowExecutionAsync(tenant, accepted.ExecutionId));
        }

    }
}
