using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    // The caller holds the IAM and backing admission gates, as for normal deletion.
    private void RetireRunContent(Guid tenant, Guid session, DateTimeOffset at)
    {
        foreach (var run in sessionBacking.AgentRuns.Runs.Values.Where(r => r.Owner.TenantId == tenant && r.SessionId == session).ToArray())
        {
            var reportPending = sessionBacking.AgentRuns.CompletionReceipts.TryGetValue(run.AgentRunId, out var row)
                && CompletionInboxMapping.Read(row).Status is not (CompletionInboxStatus.Handled or CompletionInboxStatus.Delivered or CompletionInboxStatus.Skipped);
            var minimized = run.RetireConversationContent(at, reportPending);
            if (!ReferenceEquals(run, minimized)) sessionBacking.SaveRetainedTenantOperationRun(minimized, run.Revision);
        }
    }
}
