using AgentCore.Domain.Workflows;
using AgentCore.Domain.Tasks;
namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    // Incumbent deleted-Session cleanup owns minimization, under the existing admission gates.
    private void RetireWorkflowContent(Guid tenant, DateTimeOffset at)
    {
        var runs = sessionBacking.AgentRuns.Runs.Values.Where(r => r.Owner.TenantId == tenant).ToArray();
        foreach (var value in workflowExecutions.Values.Where(e => e.TenantId == tenant && e.ContentRetiredAt is null
            && WorkflowExecutionRules.Terminal(e.Status) && e.Source.SessionId is { } source
            && sessionBacking.ReadRetainedTenantBackgroundSession(tenant, source)?.Snapshot.DurablyDeletedAt is not null).OrderBy(e => e.ExecutionId).Take(100).ToArray())
        {
            var workers = delegatedTasks.Values.Where(t => t.Source.TenantId == tenant && t.Source.Workflow?.ExecutionId == value.ExecutionId).ToArray();
            var roots = workers.Select(t => t.Source.RootActivationId).ToHashSet();
            if (workers.Any(t => !TaskLimits.Terminal(t.State)) || runs.Any(r => (r.SessionId == value.Source.SessionId || roots.Contains(r.Admission.Activation.RootActivationId))
                && (!r.IsTerminal || r.RequiresEffectReconciliation))) continue;
            workflowExecutions[(tenant, value.ExecutionId)] = value with { InputsJson = "{}", ContentRetiredAt = at, Revision = checked(value.Revision + 1) };
            foreach (var entry in workflowDecisions.Where(e => e.Key.Tenant == tenant && e.Key.Id == value.ExecutionId).ToArray())
                workflowDecisions[entry.Key] = entry.Value with { Rationale = null, ContentRetiredAt = at };
        }
    }
}
