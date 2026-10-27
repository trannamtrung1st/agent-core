using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.SignalR;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionLiveDelivery
{
    // Transport cursors only. Every hint is rebuilt from currently authorized saved facts.
    internal async ValueTask PublishTaskHintsAsync(ITenantTaskStore tasks, ITenantAgentMessageStore messages, CancellationToken ct)
    {
        await Task.WhenAll(_connections.ToArray().Select(async pair =>
        {
            var state = pair.Value;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            var entered = false;
            try
            {
                await state.Gate.WaitAsync(bounded.Token).ConfigureAwait(false); entered = true;
                if (state.Closed || state.Current is not { } sub) return;
                var recipient = await access.AuthorizeRecipientAsync(sub.Tenant, sub.Session, sub.Human, bounded.Token).ConfigureAwait(false);
                var actor = new TaskActor(Human: sub.Human);
                var hints = new List<(string Type, string Key, long Revision, Dictionary<string, object?> Payload)>();
                var page = await tasks.ReadTasksAsync(new(sub.Tenant, actor, SessionId: sub.Session, Cursor: sub.TaskCursor, Limit: 16), bounded.Token).ConfigureAwait(false);
                if (page.SecurityRevision != recipient.SecurityRevision) throw new SecurityConflictException("stale_revision");
                foreach (var task in page.Items)
                {
                    hints.Add(("task.changed", $"task:{task.TaskId:D}", task.Revision, new() { ["taskId"] = task.TaskId.ToString("D"), ["revision"] = task.Revision }));
                    var notices = await tasks.ReadTaskNotificationsAsync(sub.Tenant, actor, task.TaskId, bounded.Token).ConfigureAwait(false);
                    if (notices.SecurityRevision != recipient.SecurityRevision) throw new SecurityConflictException("stale_revision");
                    foreach (var notice in notices.Items)
                        hints.Add(("task.changed", $"notice:{notice.NotificationId:D}", notice.Revision, new() { ["taskId"] = task.TaskId.ToString("D"),
                            ["revision"] = task.Revision, ["notificationId"] = notice.NotificationId.ToString("D"), ["notificationRevision"] = notice.Revision,
                            ["attention"] = notice.AcknowledgedAt is null }));
                }
                var threads = await messages.ReadAgentThreadsAsync(new(sub.Tenant, actor, InstanceId: sub.Owner.AgentInstanceId, Scope: "all", Cursor: sub.ThreadCursor, Limit: 16), bounded.Token).ConfigureAwait(false);
                if (threads.SecurityRevision != recipient.SecurityRevision) throw new SecurityConflictException("stale_revision");
                foreach (var thread in threads.Items)
                {
                    var key = $"thread:{thread.ThreadId:D}";
                    var visible = await messages.ReadAgentMessagesAsync(sub.Tenant, actor, thread.ThreadId, sub.CoordinationHints.GetValueOrDefault(key), 16, bounded.Token).ConfigureAwait(false);
                    if (visible.SecurityRevision != recipient.SecurityRevision) throw new SecurityConflictException("stale_revision");
                    if (visible.Items.LastOrDefault() is { } last)
                        hints.Add(("agent-message.changed", key, last.Sequence, new() { ["threadId"] = thread.ThreadId.ToString("D"),
                            ["messageId"] = last.MessageId.ToString("D"), ["messageSequence"] = last.Sequence }));
                }
                var after = await access.AuthorizeRecipientAsync(sub.Tenant, sub.Session, sub.Human, bounded.Token).ConfigureAwait(false);
                if (after.Owner != recipient.Owner || after.SecurityRevision != recipient.SecurityRevision || after.SessionRevision != recipient.SessionRevision)
                    throw new SecurityConflictException("stale_revision");
                if (state.Closed || state.Current != sub) return;
                foreach (var hint in hints)
                {
                    if (sub.CoordinationHints.GetValueOrDefault(hint.Key) >= hint.Revision) continue;
                    hint.Payload["tenantId"] = sub.Tenant.ToString("D"); hint.Payload["securityRevision"] = after.SecurityRevision;
                    var context = Context(sub.Session);
                    await hubs.Clients.Client(pair.Key).SendAsync("SessionEvent", new ServerEvent { ProtocolVersion = 1,
                        SessionId = sub.Session.ToString("D"), AttachmentId = sub.Attachment.ToString("D"), EventId = context.EventId.ToString("D"),
                        Sequence = ++sub.Sequence, Timestamp = context.Timestamp.ToString("O"), CorrelationId = context.CorrelationId.ToString("D"),
                        Type = hint.Type, Payload = hint.Payload }, bounded.Token).ConfigureAwait(false);
                    sub.CoordinationHints[hint.Key] = hint.Revision;
                    while (sub.CoordinationHints.Count > 256) sub.CoordinationHints.Remove(sub.CoordinationHints.Keys.First());
                }
                sub.TaskCursor = page.NextCursor; sub.ThreadCursor = threads.NextCursor;
            }
            catch (AccessDeniedException)
            {
                // A denied coordination read must not disrupt an otherwise authorized voice/chat attachment.
                if (entered && state.Current is { } current) { current.CoordinationHints.Clear(); current.TaskCursor = null; current.ThreadCursor = null; }
            }
            catch (Exception error) when (error is SecurityConflictException or OperationCanceledException) { }
            finally { if (entered) state.Gate.Release(); }
        })).ConfigureAwait(false);
    }
}
