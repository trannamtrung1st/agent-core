using System.Text.Json;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Domain.Tests;

public sealed class TaskDelegationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
    private static TaskRequest Request(Guid target, Guid? space = null, Guid[]? dependencies = null) =>
        new("Compare migration risks", "Review migration", [target], false, space, null, dependencies ?? [], false, null, "operation-1");
    private static TaskSource Source() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), TaskActorKind.Agent,
        Guid.NewGuid(), Guid.NewGuid(), null, null, Guid.NewGuid(), null, 1);
    private static DelegatedTask Queued()
    {
        var target = Guid.NewGuid(); var space = Guid.NewGuid();
        return new(Guid.NewGuid(), Source(), Request(target, space), new(target, Guid.NewGuid(), space, null, 1, new('a', 64), "explicit"),
            DelegatedTaskState.Queued, 1, Now, Now, Now.AddHours(1), Guid.NewGuid(), Guid.NewGuid());
    }
    [Fact] public void Exact_consent_reserves_real_source_and_worker_identities_without_admitting_work()
    {
        var old = Queued(); var assignment = old.Assignment with { Model = new AgentRunModelPin("synthetic", "synthetic", "synthetic", null), Budget = new(8, 60, 24, 180) };
        var task = new DelegatedTask(old.TaskId, old.Source, old.Request, assignment, old.State, old.Revision, Now, Now, old.DeadlineAt, old.WorkerSessionId, old.InitialWorkerRunId);
        var consent = new PreparedTaskDelegation(task, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()); consent.Validate();
        var reloaded = JsonSerializer.Deserialize<PreparedTaskDelegation>(JsonSerializer.Serialize(consent))!;
        Assert.Equal(consent, reloaded); Assert.Equal(consent.GetHashCode(), reloaded.GetHashCode());
        Assert.NotEqual(consent, reloaded with { WorkerResponseId = Guid.NewGuid() });
        Assert.Throws<ArgumentException>(() => (consent with { SourceGeneration = null }).Validate());
        Assert.Throws<ArgumentException>(() => (consent with { WorkerActivationId = task.Source.RootActivationId }).Validate());
        Assert.Throws<ArgumentException>(() => (consent with { WorkerEntryId = task.WorkerSessionId }).Validate());
        var human = task.Source with { ActorKind = TaskActorKind.Human, RunId = null, ActivationId = null, HumanCommandId = Guid.NewGuid(), OriginalInitiatorPrincipalId = task.Source.PrincipalId };
        var pending = new DelegatedTask(Guid.NewGuid(), human, Request(assignment.InstanceId, assignment.SpaceId, [Guid.NewGuid()]), assignment,
            DelegatedTaskState.PendingDependencies, 1, Now, Now, Now.AddHours(1));
        new PreparedTaskDelegation(pending, null, null, null, null, Guid.NewGuid()).Validate();
        Assert.Throws<ArgumentException>(() => new PreparedTaskDelegation(pending, null, null, null, null).Validate());
        Assert.Throws<ArgumentException>(() => new PreparedTaskDelegation(pending, Guid.NewGuid(), null, null, null).Validate());
        Assert.Throws<ArgumentException>(() => new PreparedTaskDelegation(pending, null, Guid.NewGuid(), null, null).Validate());
    }
    [Fact] public void Intent_freezes_candidates_dependencies_and_normalizes_hash()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var candidates = new[] { b, a }; var dependency = Guid.NewGuid(); var dependencies = new[] { dependency };
        var request = new TaskRequest("  café  ", null, candidates, true, null, null, dependencies, false, null, "request");
        candidates[0] = Guid.NewGuid(); dependencies[0] = Guid.NewGuid();
        var equal = new TaskRequest("café", null, [a, b], true, null, null, [dependency], false, null, "other-transport-key");
        Assert.Equal(equal.Objective, request.Objective); Assert.Equal(equal.Title, request.Title);
        Assert.Equal(equal.CandidateInstanceIds, request.CandidateInstanceIds); Assert.Equal(equal.DependsOnTaskIds, request.DependsOnTaskIds);
        Assert.Equal(request.Fingerprint(), equal.Fingerprint());
        Assert.Equal(dependency, Assert.Single(request.DependsOnTaskIds));
        Assert.Equal(request.Fingerprint(), JsonSerializer.Deserialize<TaskRequest>(JsonSerializer.Serialize(request))!.Fingerprint());
    }
    [Fact] public void Human_source_cannot_invent_a_model_Run()
    {
        var source = Source();
        Assert.Throws<ArgumentException>(() => (source with { ActorKind = TaskActorKind.Human, HumanCommandId = Guid.NewGuid() }).Validate());
        var human = source with { ActorKind = TaskActorKind.Human, RunId = null, ActivationId = null, HumanCommandId = Guid.NewGuid(), OriginalInitiatorPrincipalId = source.PrincipalId };
        human.Validate();
        Assert.Throws<ArgumentException>(() => (human with { ParentTaskId = Guid.NewGuid(), Depth = 2 }).Validate());
        Assert.Throws<ArgumentException>(() => (human with { Depth = 4 }).Validate());
    }
    [Fact] public void Pending_dependencies_have_no_speculative_worker_and_terminal_is_immutable()
    {
        var queued = Queued(); var result = queued.Advance(DelegatedTaskState.Succeeded, Now.AddMinutes(1), terminalRunId: Guid.NewGuid(), result: "Observed migration risks");
        Assert.Throws<InvalidOperationException>(() => result.Advance(DelegatedTaskState.Queued, Now.AddMinutes(2)));
        Assert.Throws<ArgumentException>(() => new DelegatedTask(Guid.NewGuid(), queued.Source, queued.Request, queued.Assignment,
            DelegatedTaskState.PendingDependencies, 1, Now, Now, Now.AddHours(1)));
        var pending = new DelegatedTask(Guid.NewGuid(), queued.Source, Request(queued.Assignment.InstanceId, queued.Assignment.SpaceId, [Guid.NewGuid()]), queued.Assignment,
            DelegatedTaskState.PendingDependencies, 1, Now, Now, Now.AddHours(1));
        Assert.Null(pending.WorkerSessionId);
        Assert.Throws<InvalidOperationException>(() => pending.Advance(DelegatedTaskState.Running, Now));
        Assert.Equal(DelegatedTaskState.Blocked, pending.Advance(DelegatedTaskState.Blocked, Now, reason: "dependency_failed").State);
    }
    [Fact] public void Task_assignment_deadline_and_cancellation_cannot_drift()
    {
        var task = Queued();
        Assert.Throws<InvalidOperationException>(() => task.Advance(DelegatedTaskState.Queued, Now, workerSessionId: Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new DelegatedTask(task.TaskId, task.Source, task.Request, task.Assignment, task.State, 1,
            Now, Now, Now.AddHours(25), task.WorkerSessionId, task.InitialWorkerRunId));
        var cancelled = task.Advance(DelegatedTaskState.Running, Now, cancellationRequested: true);
        Assert.Throws<InvalidOperationException>(() => cancelled.Advance(DelegatedTaskState.Running, Now, cancellationRequested: false));
    }
    [Fact] public void Direct_pairs_are_canonical_and_third_Agent_cannot_send()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var tenant = Guid.NewGuid();
        var first = new AgentThread(tenant, Guid.NewGuid(), a, b, null, null, null, Now);
        var reverse = new AgentThread(tenant, Guid.NewGuid(), b, a, null, null, null, Now);
        Assert.Equal(first.FirstInstanceId, reverse.FirstInstanceId); Assert.Equal(first.SecondInstanceId, reverse.SecondInstanceId);
        Assert.Equal(b, first.Recipient(a, Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => first.Recipient(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new AgentThread(tenant, Guid.NewGuid(), a, a, null, null, null, Now));
    }
    [Fact] public void Same_Agent_task_message_role_requires_distinct_real_Session()
    {
        var agent = Guid.NewGuid(); var source = Guid.NewGuid(); var worker = Guid.NewGuid();
        var thread = new AgentThread(Guid.NewGuid(), Guid.NewGuid(), agent, agent, Guid.NewGuid(), source, worker, Now);
        Assert.Equal(agent, thread.Recipient(agent, source)); Assert.Equal(agent, thread.Recipient(agent, worker));
        Assert.Throws<InvalidOperationException>(() => thread.Recipient(agent, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => new AgentThread(thread.TenantId, Guid.NewGuid(), agent, agent, thread.TaskId, source, source, Now));
    }
    [Fact] public void Human_guidance_is_never_an_Agent_post()
    {
        Assert.Throws<ArgumentException>(() => new AgentMessage(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, TaskActorKind.Human,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), AgentMessageKind.Note, "hello", "key", new('a', 64), Now));
        Assert.Throws<ArgumentException>(() => TaskLimits.Text("\ud800", 20, "Invalid unicode"));
        Assert.Throws<ArgumentException>(() => Request(Guid.Empty));
    }
    [Fact] public void Content_tombstones_preserve_hashes_and_lineage_without_becoming_new_work()
    {
        var task = Queued().Advance(DelegatedTaskState.Succeeded, Now.AddMinutes(1), terminalRunId: Guid.NewGuid(), result: "Private result");
        var retired = task.RetireContent(Now.AddMinutes(2));
        var restored = JsonSerializer.Deserialize<DelegatedTask>(JsonSerializer.Serialize(retired))!;
        Assert.Equal(task.Request.Fingerprint(), restored.Request.Fingerprint());
        Assert.Equal(task.Source, restored.Source); Assert.Equal(task.Assignment, restored.Assignment);
        Assert.Equal(task.TerminalRunId, restored.TerminalRunId); Assert.Equal(task.State, restored.State);
        Assert.Equal("[retired]", restored.Request.Objective); Assert.Equal("[retired]", restored.Result);
        Assert.Same(retired, retired.RetireContent(Now.AddMinutes(3)));
        Assert.Throws<InvalidOperationException>(() => restored.Advance(DelegatedTaskState.Queued, Now.AddMinutes(3)));
        Assert.Throws<ArgumentException>(() => new PreparedTaskDelegation(restored, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()).Validate());
        var message = new AgentMessage(task.Source.TenantId, Guid.NewGuid(), Guid.NewGuid(), 1, TaskActorKind.Agent,
            task.Source.PrincipalId, task.Source.InstanceId, task.Source.SessionId, task.Source.RunId, task.Assignment.InstanceId,
            AgentMessageKind.Note, "Private correspondence", "once", new('a', 64), Now);
        var minimized = JsonSerializer.Deserialize<AgentMessage>(JsonSerializer.Serialize(message.RetireContent(Now.AddMinutes(2))))!;
        Assert.Equal("[retired]", minimized.Text); Assert.Equal(message.RequestHash, minimized.RequestHash);
        Assert.Equal(message.SourceRunId, minimized.SourceRunId); Assert.Equal(message.Sequence, minimized.Sequence);
    }
    [Fact] public void Cooperation_is_off_by_default_and_wake_requires_acceptance()
    {
        Assert.False(AgentCooperationPolicy.Disabled.AcceptTasks); Assert.False(AgentCooperationPolicy.Disabled.AcceptDirectMessages);
        Assert.False(AgentCooperationPolicy.Disabled.AllowDirectMessageWake);
        Assert.Throws<ArgumentException>(() => new AgentCooperationPolicy(AllowDirectMessageWake: true).Validate());
        Assert.Throws<ArgumentException>(() => new AgentCooperationPolicy(MaxConcurrentTasks: 9).Validate());
    }
}
