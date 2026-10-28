using AgentCore.Application.Ports;
using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Sessions;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Workflows;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)]
    public async Task Workflows_real_continuation_uses_one_Synthetic_worker_without_forged_source_and_recovers(bool sqlite, bool reject, bool repeated)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Formal process", "formal-process");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new HumanSessionReference(owner.AccountId, login.SessionId, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        using var http = new ProviderHttpFixture(); var probe = new GlobalModelProbe(); await using var services = RuntimeServices(f, http, probe, "Synthetic");
        var definitions = services.GetRequiredService<TenantDefinitionService>();
        async Task<TenantAgent> Agent(string name, string[] capabilities)
        {
            var draft = await definitions.CreateAsync(human.PrincipalId, tenant, name, await Revision(), initiator: human);
            draft = await definitions.UpdateAsync(human.PrincipalId, tenant, draft.DraftId, draft.Candidate with { ExecutionPolicy = new(Capabilities: new("Selected", capabilities)) }, await Revision(), draft.Revision, initiator: human);
            await definitions.PublishAsync(human.PrincipalId, tenant, draft.DraftId, await Revision(), draft.Revision, initiator: human);
            return await f.Agents.CreateFromPublicationAsync(human.PrincipalId, tenant, Guid.Parse(draft.DefinitionId), 1, await Revision(), initiator: human);
        }
        var coordinator = await Agent("Process coordinator", ["tasks.delegate"]);
        var worker = await Agent("Process worker", []);
        worker = await f.AgentConfiguration().UpdateCooperationAsync(human.PrincipalId, tenant, worker.Instance.InstanceId, new(AcceptTasks: true), await Revision(), worker.Instance.Revision, initiator: human);
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, worker.PrincipalId);
        var workerRole = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Workflow worker", ["sessions.create", "agent-runs.create", "sessions.read", "agent-runs.read", "agents.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, worker.PrincipalId, workerRole.RoleId, AccessScope.ForTenant(tenant), await Revision());
        var evals = services.GetRequiredService<AgentCore.Application.Evaluations.TenantEvaluationService>();
        var suite = await evals.SaveSuiteAsync(human.PrincipalId, tenant, Guid.NewGuid(), new("Exact worker completed", new EvaluationCheck[] { new("done", "Worker completed", EvaluatorKind.Script, true, EvaluationPredicate.RunCompleted) }.Concat(reject ? new EvaluationCheck[] { new("assessment", "Independent assessment", EvaluatorKind.Manual, true) } : []).ToArray(), 0, await Revision()), human);
        var space = (await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(tenant, worker.Instance.InstanceId))!;
        var graph = new WorkflowGraph(1, [
            new("start", "Start", WorkflowNodeKind.Start),
            new("prepare", "Prepare reviewed material", WorkflowNodeKind.AgentTask, WorkflowRequirement.Global,
                Task: new(worker.Instance.InstanceId, "synthetic-task-worker Prepare the approved material", space.SpaceId)),
            new("verify", "Independent verification", WorkflowNodeKind.Verify, WorkflowRequirement.Global,
                Verification: new(WorkflowVerificationKind.Evaluation, "prepare", suite.SuiteId, suite.Version, suite.ContentHash)),
            new("done", "Workflow completed", WorkflowNodeKind.EndSuccess), new("failed", "Workflow failed", WorkflowNodeKind.EndFailure)
        ], [new("a", "start", "prepare"), new("b", "prepare", "verify", "succeeded"), new("c", "prepare", "failed", "failed"),
            new("d", "verify", "done", "passed"), new("e", "verify", "failed", "failed")], [], [WorkflowInvocationKind.HumanManual], [coordinator.Instance.InstanceId]);
        if (repeated)
            graph = graph with { Nodes = graph.Nodes.Concat([
                graph.Nodes.Single(n => n.Id == "prepare") with { Id = "prepare2", Name = "Second required Task" },
                graph.Nodes.Single(n => n.Id == "verify") with { Id = "verify2", Verification = new(WorkflowVerificationKind.Evaluation, "prepare2", suite.SuiteId, suite.Version, suite.ContentHash) }]).ToArray(),
                Edges = graph.Edges.Select(e => e.Id == "d" ? e with { To = "prepare2" } : e).Concat([
                    new WorkflowEdge("f", "prepare2", "verify2", "succeeded"), new WorkflowEdge("g", "prepare2", "failed", "failed"),
                    new WorkflowEdge("h", "verify2", "done", "passed"), new WorkflowEdge("i", "verify2", "failed", "failed")]).ToArray() };
        var catalog = Workflows(f); var workflowId = Guid.NewGuid();
        await catalog.SaveAsync(tenant, workflowId, human, new("Actual worker process", "", graph, false, 0, await Revision(), "worker-process"));
        var version = (await catalog.PublishAsync(tenant, workflowId, human, new(1, await Revision(), "worker-process-publish"))).Version!;
        var coordinatorRole = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Actual process delegation", ["tasks.delegate", "agents.read", "sessions.read", "agent-runs.read", "evaluations.read", "evaluations.run", "spaces.read", "scripts.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, coordinator.PrincipalId, coordinatorRole.RoleId, AccessScope.ForTenant(tenant), await Revision());
        var executionId = Guid.NewGuid(); var now = DateTimeOffset.FromUnixTimeMilliseconds(f.Clock.GetUtcNow().ToUnixTimeMilliseconds());
        var binding = new WorkflowExecution(tenant, executionId, workflowId, 1, version.Graph.Hash,
            new(WorkflowInvocationKind.HumanManual, executionId, human.PrincipalId, human.PrincipalId, human.AccountId), coordinator.Instance.InstanceId,
            "{}", WorkflowExecutionRules.Hash("{}"), "one-start", WorkflowExecutionRules.Hash("one-logical-worker"), now, now.AddDays(1), 1);
        var processStore = (ITenantWorkflowExecutionStore)f.Iam;
        binding = await processStore.StartHumanWorkflowAsync(new(human, binding, await Revision(), f.Ids.NewId()));
        Assert.Equal(binding.ExecutionId, (await processStore.StartHumanWorkflowAsync(new(human, binding, 0, f.Ids.NewId()))).ExecutionId);
        var vendor = WorkflowRuntimeProvider(f, sqlite);
        try {
            if (sqlite) await vendor.MigrateFormalWorkflowRuntimeAsync(); await vendor.PopulateFormalWorkflowActivitiesAsync();
            var engine = vendor.GetRequiredService<IFormalWorkflowRuntime>();
            var bookmark = Assert.Single((await engine.StartAsync(version, executionId)).Bookmarks); Assert.Equal("prepare", bookmark.NodeId);
            var operation = await processStore.ReserveWorkflowStepAsync(new(tenant, executionId,
                new(tenant, executionId, "prepare", 1, f.Ids.NewId(), bookmark.BookmarkId, version.Graph.Hash, WorkflowNodeKind.AgentTask, 1, WorkflowOperationStatus.Reserved, now, f.Ids.NewId(), f.Ids.NewId()), binding.Revision, f.Ids.NewId()));
            var at = DateTimeOffset.FromUnixTimeMilliseconds(f.Clock.GetUtcNow().ToUnixTimeMilliseconds());
            var resolved = await f.RunConfiguration().ResolveInSpaceAsync(tenant, worker.Instance.InstanceId, new(tenant, space.SpaceId, null, 0, 1, at), default);
            var modelCatalog = await new SyntheticTenantModelCatalogReader(f.Iam).ReadAsync((await f.Iam.ReadTenantAsync(tenant))!, default);
            var selected = SessionModelBinder.BindPreference(modelCatalog, worker.Instance.UnattendedModel ?? ModelPreference.Inherit, resolved.Configuration.Configuration.Definition.ModelDefaults);
            var model = new AgentRunModelPin(selected.OfferingId, selected.ConnectionId, selected.ExternalModelId, selected.ReasoningEffort, selected.Binding, selected.Decision);
            var activation = f.Ids.NewId();
            var source = new TaskSource(tenant, null, coordinator.Instance.InstanceId, coordinator.PrincipalId, TaskActorKind.WorkflowContinuation,
                null, null, null, human.PrincipalId, activation, null, 1, new(workflowId, executionId, version.Version, version.Graph.Hash, operation.NodeId, 1, operation.OperationId));
            var request = new TaskRequest(graph.Nodes.Single(n => n.Id == "prepare").Task!.Objective, "Prepare reviewed material", [worker.Instance.InstanceId], false, space.SpaceId, null, [], false, binding.DeadlineAt, "workflow:" + operation.OperationId.ToString("D"));
            var assignment = new TaskAssignment(worker.Instance.InstanceId, worker.PrincipalId, space.SpaceId, null, worker.Instance.Revision,
                TaskLimits.Hash(resolved.Configuration.Configuration), "explicit", model, 0, worker.Instance.Persona.Name,
                WorkflowTaskRules.Allocate(binding, version, operation.NodeId, resolved.Configuration.Configuration.Definition, worker.Instance.ExecutionBudgets, []));
            var task = new DelegatedTask(operation.TaskId!.Value, source, request, assignment, DelegatedTaskState.Queued, 1, at, at, binding.DeadlineAt, f.Ids.NewSessionId(), operation.WorkerRunId);
            var workerGraph = TaskAdmissionFactory.Worker(task, worker, resolved.Configuration, model, task.WorkerSessionId!.Value, f.Ids.NewId(), activation, task.InitialWorkerRunId!.Value, f.Ids.NewId(), at);
            var admission = new WorkflowTaskAdmission(tenant, executionId, operation.OperationId, operation.Revision, await Revision(), task, workerGraph.Session, workerGraph.Run, f.Ids.NewId());
            var delegation = (await f.Iam.ReadTenantAsync(tenant))!.Assignments.Single(a => a.PrincipalId == coordinator.PrincipalId && a.RoleId == coordinatorRole.RoleId);
            await f.Access.RevokeAssignmentAsync(human.PrincipalId, tenant, delegation.AssignmentId, await Revision());
            admission = admission with { ExpectedSecurityRevision = await Revision() };
            await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantWorkflowTaskStore)f.Iam).AdmitWorkflowTaskAsync(admission).AsTask());
            Assert.Null(await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, task.TaskId));
            await f.Access.AssignRoleAsync(human.PrincipalId, tenant, coordinator.PrincipalId, coordinatorRole.RoleId, AccessScope.ForTenant(tenant), await Revision());
            admission = admission with { ExpectedSecurityRevision = await Revision() };
            TaskMutationResult accepted;
            try { accepted = await ((ITenantWorkflowTaskStore)f.Iam).AdmitWorkflowTaskAsync(admission); }
            catch (SecurityConflictException error) { throw new InvalidOperationException(string.Join("\n", f.DatabaseErrors), error); }
            var replay = await ((ITenantWorkflowTaskStore)f.Iam).AdmitWorkflowTaskAsync(admission);
            Assert.True(replay.Replayed); Assert.Equal(accepted.Task.TaskId, replay.Task.TaskId);
            Assert.Null(accepted.Task.Source.SessionId); Assert.Null(accepted.Task.Source.RunId); Assert.Null(workerGraph.Session.Origin!.OriginatingSessionId);
            Assert.Equal(workerGraph.Run.AgentRunId, (await TenantExecutionLineage.RootAsync(workerGraph.Run, (ITenantRunStore)f.Iam, default)).AgentRunId);
            await ((ITenantTaskStore)f.Iam).RequireTaskExecutionSourcesAsync(workerGraph.Run);
            if (sqlite) { await vendor.DisposeAsync(); vendor = null!; }
            var actual = await ExecuteCooperation(f, services, tenant, workerGraph.Run.AgentRunId, probe, beforeDispatch: run => {
                Assert.True(run.Claim is not null, $"Claim stopped: {run.Status}; cancellation={run.CancellationRequested}; source={task.Source.ActorKind}; task={(f.Iam as ITenantTaskStore)!.FindTaskAsync(tenant, task.TaskId).Result?.State}");
                return Task.CompletedTask;
            });
            Assert.Contains("Synthetic worker completed", actual.Result!.Text); Assert.Empty(http.Requests);
            Assert.True(await ((ITenantTaskStore)f.Iam).ReconcileTaskAsync(tenant, task.TaskId, task.Revision, f.Ids.NewId()));
            var settledTask = (await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, task.TaskId))!; Assert.Equal(DelegatedTaskState.Succeeded, settledTask.State);
            operation = Assert.Single(await processStore.ReadWorkflowStepsAsync(tenant, executionId)); Assert.Equal(WorkflowOperationStatus.Admitted, operation.Status);
            var settled = operation with { Revision = operation.Revision + 1, Status = WorkflowOperationStatus.Settled, WorkerRunId = actual.AgentRunId,
                Outcome = "succeeded", SettledAt = f.Clock.GetUtcNow() };
            await Assert.ThrowsAsync<AccessDeniedException>(() => processStore.WriteWorkflowStepAsync(new(settled with { ProofAccepted = true }, operation.Revision, f.Ids.NewId())).AsTask());
            Assert.True(await processStore.WriteWorkflowStepAsync(new(settled, operation.Revision, f.Ids.NewId())));
            if (sqlite) {
                processStore = (ITenantWorkflowExecutionStore)new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
                Assert.Equal(settled, Assert.Single(await processStore.ReadWorkflowStepsAsync(tenant, executionId)));
                vendor = WorkflowRuntimeProvider(f, true); await vendor.PopulateFormalWorkflowActivitiesAsync(); engine = vendor.GetRequiredService<IFormalWorkflowRuntime>();
            }
            var resumed = await engine.ResumeAsync(tenant, executionId, bookmark.BookmarkId, operation.NodeId, settled.Outcome!);
            Assert.False(resumed.Finished); Assert.Equal("verify", Assert.Single(resumed.Bookmarks).NodeId);
            Assert.Equal("verify", Assert.Single((await engine.ResumeAsync(tenant, executionId, bookmark.BookmarkId, operation.NodeId, settled.Outcome!)).Bookmarks).NodeId);
            Assert.True(await processStore.WriteWorkflowStepAsync(new(settled with { Revision = settled.Revision + 1, Status = WorkflowOperationStatus.SignalConsumed }, settled.Revision, f.Ids.NewId())));
            var bridge = new TenantWorkflowExecutionService(f.Iam, (ITenantWorkflowCatalogStore)f.Iam, (ITenantWorkflowExecutionStore)f.Iam,
                (ITenantWorkflowTaskStore)f.Iam, (ITenantTaskStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, (ITenantSpaceStore)f.Iam,
                catalog, f.RunConfiguration(), new SyntheticTenantModelCatalogReader(f.Iam), engine, f.Identity, f.Authorizer, f.Ids, f.Clock, evaluations: evals);
            for (var pass = 0; pass < 20; pass++)
            {
                await bridge.ReconcileAsync(8, default);
                foreach (var nextTask in (await ((ITenantWorkflowExecutionStore)f.Iam).ReadWorkflowTasksAsync(tenant, executionId)).Where(t => t.State == DelegatedTaskState.Queued))
                {
                    await ExecuteCooperation(f, services, tenant, nextTask.InitialWorkerRunId!.Value, probe);
                    await ((ITenantTaskStore)f.Iam).ReconcileTaskAsync(tenant, nextTask.TaskId, nextTask.Revision, f.Ids.NewId());
                }
            }
            var completed = (await ((ITenantWorkflowExecutionStore)f.Iam).FindWorkflowExecutionAsync(tenant, executionId))!;
            if (reject)
            {
                Assert.Equal(WorkflowExecutionStatus.WaitingForVerification, completed.Status);
                Assert.Equal("evaluation_review_pending", completed.ReasonCode);
                var pending = (await ((ITenantWorkflowExecutionStore)f.Iam).ReadWorkflowStepsAsync(tenant, executionId)).Single(s => s.NodeId == "verify");
                Assert.False(pending.ProofAccepted);
                var review = (await ((ITenantEvaluationStore)f.Iam).FindAttemptAsync(tenant, pending.EvaluationAttemptId!.Value))!;
                Assert.Equal(EvaluationStatus.Pending, review.Status);
                await evals.ReviewAsync(human.PrincipalId, tenant, review.AttemptId, new("assessment", EvaluationOutcome.Failed, "Independent assessment rejected the worker output", await Revision(), review.Revision), human);
                for (var pass = 0; pass < 8; pass++) await bridge.ReconcileAsync(8, default);
                completed = (await ((ITenantWorkflowExecutionStore)f.Iam).FindWorkflowExecutionAsync(tenant, executionId))!;
            }
            Assert.True(completed.Status == (reject ? WorkflowExecutionStatus.Failed : WorkflowExecutionStatus.Completed), $"Workflow stopped at {completed.Status}: {completed.ReasonCode}");
            var receipts = await ((ITenantWorkflowExecutionStore)f.Iam).ReadWorkflowStepsAsync(tenant, executionId);
            var verification = receipts.Single(s => s.NodeId == "verify"); Assert.Equal(!reject, verification.ProofAccepted);
            var attempt = (await ((ITenantEvaluationStore)f.Iam).FindAttemptAsync(tenant, verification.EvaluationAttemptId!.Value))!;
            Assert.Equal(coordinator.PrincipalId, attempt.ActorId); Assert.Equal(reject ? EvaluationOutcome.Failed : EvaluationOutcome.Passed, attempt.Outcome);
            Assert.Equal(actual.AgentRunId, attempt.SubjectId); Assert.Equal(WorkflowEvidenceAuthority.EvaluationKey(verification), attempt.DedupKey);
            var acceptedTasks = await ((ITenantWorkflowExecutionStore)f.Iam).ReadWorkflowTasksAsync(tenant, executionId);
            Assert.Equal(repeated ? 2 : 1, acceptedTasks.Count);
            Assert.True(acceptedTasks.Sum(t => t.Assignment.Budget!.MaxSteps) <= graph.MaxTotalTaskSteps);
            Assert.True(acceptedTasks.Sum(t => t.Assignment.Budget!.DurationSeconds) <= graph.MaxTotalTaskSeconds);
            var view = await TaskProjection.ForAsync((await f.Iam.ReadTenantAsync(tenant))!, new(Human: human), settledTask, f.Authorizer,
                (ITenantSessionStore)f.Iam, f.Clock.GetUtcNow(), default, services.GetRequiredService<TenantRunSourceAuthority>());
            Assert.Equal(executionId, view.WorkflowExecutionId); Assert.Null(view.SourceSessionId); Assert.False(view.CanRetry);
            if (!reject && !repeated)
            {
                var initiator = new TenantSessionInitiator(human.AccountId, human.AuthenticationSessionId, human.PrincipalId);
                var workerOwner = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, workerGraph.Session.SessionId))!.Owner;
                var workerSession = await f.Sessions.ReadAsync(tenant, workerGraph.Session.SessionId, initiator);
                await f.Sessions.ChangeAsync(tenant, initiator, new(workerGraph.Session.SessionId, workerSession.Revision, TenantSessionAction.Delete), await Revision());
                Assert.True(await ((ITenantWorkingCleanupStore)f.Iam).RetireTenantRunContentAsync(workerOwner));
                var minimizedTask = (await ((ITenantTaskStore)f.Iam).FindTaskAsync(tenant, task.TaskId))!;
                Assert.NotNull(minimizedTask.ContentRetiredAt); Assert.Equal("[retired]", minimizedTask.Request.Objective);
                Assert.Equal("[retired]", minimizedTask.Result); Assert.Equal(settledTask.Request.Fingerprint(), minimizedTask.Request.Fingerprint());
                Assert.Equal(settledTask.Source, minimizedTask.Source); Assert.Equal(DelegatedTaskState.Succeeded, minimizedTask.State);
            }
            if (sqlite) {
                await using var db = f.Contexts!.CreateDbContext(); var command = db.Database.GetDbConnection().CreateCommand();
                await db.Database.OpenConnectionAsync(); command.CommandText = "PRAGMA foreign_key_check"; await using var reader = await command.ExecuteReaderAsync(); Assert.False(await reader.ReadAsync());
            }
        }
        finally { if (vendor is not null) await vendor.DisposeAsync(); }
    }
}
