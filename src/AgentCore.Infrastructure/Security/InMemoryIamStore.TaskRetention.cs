using AgentCore.Domain.Tasks;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private sealed record RetiredTaskApproval(Guid TenantId, Guid RequestId, Guid OperationId, Guid TaskId, Guid SourceSessionId,
        Guid WorkerInstanceId, Guid WorkerPrincipalId, string ActionHash, TenantApprovalStatus Status, DateTimeOffset ContentRetiredAt);
    private readonly Dictionary<Guid, RetiredTaskApproval> retiredTaskApprovals = [];
    private void RetireTaskContent(Guid tenant, DateTimeOffset at)
    {
        var tasks = delegatedTasks.Values.Where(t => t.Source.TenantId == tenant).ToArray();
        var runs = sessionBacking.AgentRuns.Runs.Values.Where(r => r.Owner.TenantId == tenant).ToArray();
        bool Deleted(Guid id) => sessionBacking.ReadRetainedTenantBackgroundSession(tenant, id)?.Snapshot.DurablyDeletedAt is not null;
        bool Quiescent(Guid source, Guid? root = null)
        {
            var roots = root is { } exact ? new HashSet<Guid> { exact } : tasks.Where(t => t.Source.SessionId == source).Select(t => t.Source.RootActivationId).ToHashSet();
            return !runs.Any(r => (r.SessionId == source || roots.Contains(r.Admission.Activation.RootActivationId)) && (!r.IsTerminal || r.RequiresEffectReconciliation));
        }
        foreach (var task in tasks.Where(t => t.ContentRetiredAt is null && TaskLimits.Terminal(t.State) && Deleted(t.Source.SessionId) && Quiescent(t.Source.SessionId, t.Source.RootActivationId)).OrderBy(t => t.TaskId).Take(100))
        {
            if (task.ContentRetiredAt is not null || !TaskLimits.Terminal(task.State) || !Deleted(task.Source.SessionId) || !Quiescent(task.Source.SessionId, task.Source.RootActivationId)) continue;
            delegatedTasks[(tenant, task.TaskId)] = task.RetireContent(at);
            foreach (var entry in taskCommands.Where(c => c.Key.Tenant == tenant && c.Key.Task == task.TaskId).ToArray())
                taskCommands[entry.Key] = (entry.Value.Hash, entry.Value.Task.RetireContent(at));
        }
        foreach (var approval in _approvals.Values.Where(a => a.TenantId == tenant && a.Operation.TaskDelegation is not null && Deleted(a.Operation.TaskDelegation.Task.Source.SessionId) && Quiescent(a.Operation.TaskDelegation.Task.Source.SessionId, a.RootActivationId) && !_approvalSignals.Values.Any(signal => signal.RequestId == a.RequestId && signal.ProcessedAt is null)).OrderBy(a => a.RequestId).Take(100).ToArray())
        {
            var task = approval.Operation.TaskDelegation!.Task;
            if (!Deleted(task.Source.SessionId) || !Quiescent(task.Source.SessionId, task.Source.RootActivationId)
                || _approvalSignals.Values.Any(s => s.RequestId == approval.RequestId && s.ProcessedAt is null)) continue;
            foreach (var grant in _operationGrants.Values.Where(g => g.RequestId == approval.RequestId && g.Status == OperationGrantStatus.Available).ToArray())
                _operationGrants[grant.RequestId] = grant with { Status = OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) };
            retiredTaskApprovals[approval.RequestId] = new(tenant, approval.RequestId, approval.Operation.OperationId, task.TaskId,
                task.Source.SessionId, task.Assignment.InstanceId, task.Assignment.PrincipalId, approval.Operation.ActionHash,
                approval.Status == TenantApprovalStatus.Pending ? at >= approval.ExpiresAt ? TenantApprovalStatus.Expired : TenantApprovalStatus.Cancelled : approval.Status, at);
            _approvals.Remove(approval.RequestId);
        }
        bool MessageQuiescent(AgentMessage message, AgentMessageReceipt receipt)
        {
            if (message.SourceSessionId is not { } source || !Deleted(source)) return false;
            var root = agentThreads[(tenant, message.ThreadId)].TaskId is { } taskId ? tasks.Single(t => t.TaskId == taskId).Source.RootActivationId
                : runs.SingleOrDefault(r => r.AgentRunId == message.SourceRunId)?.Admission.Activation.RootActivationId;
            return root is not null && Quiescent(source, root) && (receipt.WakeSessionId is not { } wake || Quiescent(wake, receipt.WakeRootActivationId));
        }
        foreach (var entry in agentMessages.Where(m => m.Key.Tenant == tenant && m.Value.Message.ContentRetiredAt is null
            && MessageQuiescent(m.Value.Message, m.Value.Receipt)).OrderBy(m => m.Key.Message).Take(100).ToArray())
        {
            var (message, receipt, scope) = entry.Value;
            agentMessages[entry.Key] = (message.RetireContent(at), receipt.State == AgentMessageDelivery.NotificationPending
                ? receipt with { State = AgentMessageDelivery.Undeliverable, Reason = "message_source_unavailable", Revision = checked(receipt.Revision + 1), UpdatedAt = at } : receipt, scope);
        }
    }
}
