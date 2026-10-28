using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private IReadOnlyList<TaskWaitFact> TenantTaskWaitFacts(AgentRun run, AgentRunWait wait)
    {
        var tenant = run.Owner.TenantId!.Value; var state = _tenants[tenant]; var actor = new TaskActor(Agent: new(run.Owner, run.SessionId, run.AgentRunId,
            run.Claim?.Generation ?? wait.SuspendedGeneration ?? Guid.Empty), Tool: "execution.wait");
        var result = new List<TaskWaitFact>();
        foreach (var witness in wait.Tasks ?? [])
        {
            var task = delegatedTasks.GetValueOrDefault((tenant, witness.TaskId));
            var available = task is not null && (task.Source.SessionId == run.SessionId || task.WorkerSessionId == run.SessionId)
                && sessionBacking.ReadTenantSession(tenant, task.Source.RequireSessionId()) is { } source && !SessionRetention.IsDue(source.Snapshot, TaskNow)
                && CanReadTask(state, actor, task, default);
            long sequence = 0;
            if (available && TaskWaitRules.CanReadMessagesAsync(state, run.Owner.ExecutorPrincipalId!.Value, task!, new(this), CancellationToken.None, run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId).Result)
            {
                var thread = agentThreads.Values.SingleOrDefault(t => t.TenantId == tenant && t.TaskId == task!.TaskId);
                if (thread is not null) sequence = agentMessages.Values.Where(m => m.Message.TenantId == tenant && m.Message.ThreadId == thread.ThreadId
                    && m.Message.SourceSessionId != run.SessionId).Select(m => m.Message.Sequence).DefaultIfEmpty().Max();
            }
            result.Add(new(witness.TaskId, available, available ? task!.State : null, available ? task!.Revision : 0, sequence));
        }
        return result;
    }
}
