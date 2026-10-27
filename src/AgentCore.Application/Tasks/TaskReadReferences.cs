using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tasks;

/// <summary>Recorded tool evidence remains subject to current source rights at protected boundaries.</summary>
public static class TaskReadReferences
{
    private static bool Read(string name) => name is "tasks.list" or "tasks.inspect" or "tasks.messages.list" or "agent-messages.list" or "execution.wait";
    public static async ValueTask RequireAsync(AgentRun run, TenantSecurityState state, IamAuthorizer iam, ITenantRunStore runs, CancellationToken ct)
    {
        if (run.Status != AgentRunStatus.Running || run.Claim is null || !AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return;
        var savedMessages = messages!;
        var groups = savedMessages.SelectMany(m => m.ToolCalls ?? []).GroupBy(c => c.Id, StringComparer.Ordinal).ToArray();
        if (groups.Any(group => group.Select(c => (c.Name, c.ArgumentsJson)).Distinct().Count() > 1)) throw new AccessDeniedException("task_source_ambiguous");
        var calls = groups.ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var message in savedMessages.Where(m => m.Role == ModelRole.Tool && m.Name is { } name && Read(name)))
        {
            if (!calls.TryGetValue(message.ToolCallId ?? "", out var call) || call.Name != message.Name) continue;
            using var reply = JsonDocument.Parse(message.Text); using var arguments = JsonDocument.Parse(call.ArgumentsJson);
            var data = reply.RootElement; if (data.ValueKind != JsonValueKind.Object || data.TryGetProperty("error", out _) || data.TryGetProperty("code", out _)) continue;
            var actor = new TaskActor(Agent: new(run.Owner, run.SessionId, run.AgentRunId, run.Claim.Generation), Tool: call.Name);
            var taskIds = new HashSet<Guid>();
            var projections = new Dictionary<Guid, JsonElement>();
            if (call.Name == "tasks.inspect" && data.TryGetProperty("taskId", out var exact)) { taskIds.Add(exact.GetGuid()); projections.Add(exact.GetGuid(), data); }
            if (call.Name == "tasks.list" && data.TryGetProperty("items", out var items)) foreach (var item in items.EnumerateArray())
            { var id = item.GetProperty("taskId").GetGuid(); taskIds.Add(id); projections.Add(id, item); }
            if (call.Name == "execution.wait" && data.TryGetProperty("statuses", out var statuses))
                foreach (var item in statuses.EnumerateArray()) if (item.TryGetProperty("taskId", out var task) && item.GetProperty("state").GetString() != "Unavailable") taskIds.Add(task.GetGuid());
            foreach (var id in taskIds)
            {
                var saved = runs is ITenantTaskStore tasks ? await tasks.FindTaskAsync(state.Tenant.TenantId, id, ct) : null;
                if (saved is null || !await TaskAuthority.CanReadAsync(state, actor, saved, iam, ct, run)) throw new AccessDeniedException("task_source_unavailable");
                if (runs is not ITenantSessionStore sessions || await sessions.FindTenantSessionAsync(state.Tenant.TenantId, saved.Source.SessionId, ct) is not { } source
                    || SessionRetention.IsDue(source.Snapshot, run.UpdatedAtUtc)) throw new AccessDeniedException("task_source_unavailable");
                _ = await ((ITenantTaskStore)runs).ReadTaskNotificationsAsync(state.Tenant.TenantId, actor, id, ct);
                var projection = projections.GetValueOrDefault(id);
                if (projection.ValueKind == JsonValueKind.Object)
                {
                    foreach (var (field, expected, permission, type, review) in new (string, Guid?, string, string, bool)[] {
                        ("selectedTargetInstanceId", saved.Assignment.InstanceId, "agents.read", "agent", false),
                        ("sourceInstanceId", saved.Source.InstanceId, "agents.read", "agent", false),
                        ("spaceId", saved.Assignment.SpaceId, "spaces.read", "space", false),
                        ("sourceSessionId", saved.Source.SessionId, "sessions.read", "session", false),
                        ("sourceRunId", saved.Source.RunId, "agent-runs.read", "agent-run", true),
                        ("workerSessionId", saved.WorkerSessionId, "sessions.read", "session", true),
                        ("initialRunId", saved.InitialWorkerRunId, "agent-runs.read", "agent-run", true),
                        ("terminalRunId", saved.TerminalRunId, "agent-runs.read", "agent-run", true) })
                    {
                        if (!projection.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) continue;
                        var scope = AccessScope.ForResource(state.Tenant.TenantId, type, value.GetGuid().ToString("D"));
                        if (value.GetGuid() != expected || !(await iam.CheckOwnedTenantAsync(state, actor.PrincipalId, PermissionCatalog.Get(permission), scope, ct)).Allowed
                            || review && await TenantSandboxReadOperationRules.NeedsReviewAsync(state, run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId, permission, scope, iam, ct))
                            throw new AccessDeniedException("task_source_unavailable");
                    }
                    if (projection.TryGetProperty("retryOfTaskId", out var retry) && retry.ValueKind == JsonValueKind.String)
                    {
                        var previous = await ((ITenantTaskStore)runs).FindTaskAsync(state.Tenant.TenantId, retry.GetGuid(), ct);
                        if (previous is null || !await TaskAuthority.CanReadAsync(state, actor, previous, iam, ct, run)) throw new AccessDeniedException("task_source_unavailable");
                    }
                }
                if (projection.ValueKind == JsonValueKind.Object && projection.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
                {
                    if (result.GetString() != saved.Result) throw new AccessDeniedException("task_result_source_unavailable");
                    if (saved.WorkerSessionId is not { } worker || saved.TerminalRunId is not { } terminal
                        || await sessions.FindTenantSessionAsync(state.Tenant.TenantId, worker, ct) is null
                        || !(await iam.CheckTenantAsync(state, actor.PrincipalId, PermissionCatalog.Get("sessions.read"), TenantSessionRules.Scope(state.Tenant.TenantId, worker), ct)).Allowed
                        || !(await iam.CheckTenantAsync(state, actor.PrincipalId, PermissionCatalog.Get("agent-runs.read"), TenantRunRules.Scope(state.Tenant.TenantId, terminal), ct)).Allowed)
                        throw new AccessDeniedException("task_result_source_unavailable");
                    var final = await runs.FindTenantRunAsync(state.Tenant.TenantId, terminal, ct) ?? throw new AccessDeniedException("task_result_source_unavailable");
                    if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId,
                        "sessions.read", TenantSessionRules.Scope(state.Tenant.TenantId, worker), iam, ct)
                        || await TenantSandboxReadOperationRules.NeedsReviewAsync(state, run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId,
                            "agent-runs.read", TenantRunRules.Scope(state.Tenant.TenantId, terminal), iam, ct)) throw new AccessDeniedException("task_result_source_unavailable");
                    await ((ITenantTaskStore)runs).RequireTaskOutcomeSourcesAsync(final, ct);
                }
            }
            if (call.Name == "tasks.messages.list" && arguments.RootElement.TryGetProperty("taskId", out var messageTask)
                && data.TryGetProperty("items", out var taskMessages) && taskMessages.GetArrayLength() > 0)
            {
                if (runs is not ITenantAgentMessageStore store) throw new AccessDeniedException("task_message_source_unavailable");
                _ = await store.ReadMessagesPageAsync(state.Tenant.TenantId, actor, messageTask.GetGuid(), null, null, 1, ct);
                await RequireMessages(taskMessages, store);
            }
            if (call.Name == "agent-messages.list" && data.TryGetProperty("items", out var direct))
            {
                if (runs is not ITenantAgentMessageStore store) throw new AccessDeniedException("agent_message_source_unavailable");
                foreach (var thread in direct.EnumerateArray().Select(i => i.GetProperty("threadId").GetGuid()).Distinct())
                    _ = await store.ReadMessagesPageAsync(state.Tenant.TenantId, actor, null, thread, null, 1, ct);
                await RequireMessages(direct, store);
            }
            async ValueTask RequireMessages(JsonElement items, ITenantAgentMessageStore store)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("messageId", out var id)) continue;
                    var current = await store.FindAgentMessageSourceAsync(state.Tenant.TenantId, id.GetGuid(), ct);
                    if (current is null || current.Message.ThreadId != item.GetProperty("threadId").GetGuid()
                        || current.Message.Text != item.GetProperty("text").GetString()) throw new AccessDeniedException("message_source_unavailable");
                }
            }
        }
    }
}
