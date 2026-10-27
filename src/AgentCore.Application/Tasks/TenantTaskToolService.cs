using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Tasks;

public sealed class TenantTaskToolService(IIamStore security, ITenantTaskStore tasks, ITenantAgentMessageStore messages,
    ITenantSessionStore sessions, ITenantRunStore runs, TenantRunConfigurationResolver configurations,
    TenantTaskService service, IamAuthorizer iam, IIdGenerator ids, TimeProvider time, TenantRunSourceAuthority? sources = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async ValueTask<IReadOnlyList<string>> OfferedAsync(AgentRun run, CancellationToken ct)
    {
        if (run.Claim is null || run.Owner.TenantId is null) return [];
        var result = new List<string>();
        foreach (var name in TenantTaskToolContract.Names)
            try { await Require(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim.Generation), name, ct, discover: true); result.Add(name); }
            catch (AccessDeniedException) { }
        return result;
    }
    private async ValueTask<(AgentRun Run, TaskActor Actor, AgentCore.Domain.Security.TenantSecurityState State)> Require(TenantRuntimeFrame frame, string name, CancellationToken ct, AgentCore.Domain.Security.AccessScope? permissionScope = null, bool discover = false)
    {
        if (!TenantTaskToolContract.IsTool(name) || frame.Owner.TenantId is not { } tenant) throw new AccessDeniedException("task_tool_unavailable");
        var state = await security.ReadTenantAsync(tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var session = await sessions.FindTenantSessionAsync(tenant, frame.SessionId, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var run = await runs.FindTenantRunAsync(tenant, frame.RunId, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var (agent, current) = await configurations.CurrentPolicyAsync(tenant, frame.Owner.AgentInstanceId, ct);
        var actor = new TaskActor(Agent: frame, Tool: name);
        await TaskAuthority.SourceAsync(state, actor, session, agent, run, current, iam, time.GetUtcNow(), TenantTaskToolContract.Permission(name), false, ct, permissionScope, discover);
        if (name.StartsWith("agent-messages.", StringComparison.Ordinal) && agent.Instance.Cooperation?.AcceptDirectMessages != true)
            throw new AccessDeniedException("agent_message_unavailable");
        return (run, actor, state);
    }
    public async ValueTask<string> ExecuteAsync(TenantRuntimeFrame frame, ModelToolCall call, int budget, CancellationToken ct)
    {
        try { return await ExecuteCoreAsync(frame, call, budget, ct); }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw new ArgumentException("Use the closed task/message schema with bounded, correctly typed values.", error); }
    }
    private async ValueTask<string> ExecuteCoreAsync(TenantRuntimeFrame frame, ModelToolCall call, int budget, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(call.ArgumentsJson) > 64_000) throw new ArgumentException("Task arguments exceed the bound.");
        if (call.Name is "tasks.delegate" or "tasks.cancel" or "tasks.acknowledge" or "tasks.messages.send" or "agent-messages.send"
            && budget < 1024) throw new ArgumentException("Reserve at least 1024 output bytes for the saved mutation receipt before changing Task or message facts.");
        var tenant = frame.Owner.TenantId!.Value;
        using var document = JsonDocument.Parse(call.ArgumentsJson, new JsonDocumentOptions { MaxDepth = 12 }); var args = document.RootElement;
        using var descriptor = JsonDocument.Parse(TenantTaskToolContract.Descriptors().Single(d => d.Name == call.Name).ModelDefinition.ParametersJson);
        Closed(args, descriptor.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name),
            descriptor.RootElement.GetProperty("required").EnumerateArray().Select(p => p.GetString()!));
        var taskId = OptionalId(args, "taskId"); var cursor = OptionalId(args, "cursor");
        var permissionScope = taskId is { } selectedTask ? TaskAuthority.Scope(tenant, selectedTask)
            : OptionalId(args, "threadId") is { } selectedThread ? AgentCore.Domain.Security.AccessScope.ForResource(tenant, "agent-thread", selectedThread.ToString("D"))
            : call.Name == "agent-messages.send" ? AgentCore.Domain.Security.AccessScope.ForResource(tenant, "agent", frame.Owner.AgentInstanceId.ToString("D")) : null;
        var (run, actor, state) = await Require(frame, call.Name, ct, permissionScope, discover: permissionScope is null && call.Name != "tasks.delegate");
        var limit = args.TryGetProperty("limit", out var count) ? count.GetInt32() : 10;
        object result;
        if (call.Name == "tasks.delegate")
        {
            var admitted = await service.DelegateAsync(tenant, frame.SessionId, actor, ParseDelegate(args), state.Tenant.Revision, ct);
            var current = await security.ReadTenantAsync(tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
            result = new { task = new { admitted.Task.TaskId, revision = 1 }, admitted.Replayed, securityRevision = current.Tenant.Revision };
        }
        else if (call.Name == "tasks.list")
        {
            var scope = args.GetProperty("scope").GetString()!;
            var page = await tasks.ReadTasksAsync(new(tenant, actor, SessionId: scope == "created" ? frame.SessionId : null,
                InstanceId: frame.Owner.AgentInstanceId, Scope: scope, Cursor: cursor, Limit: limit), ct);
            var items = new List<TaskView>(); foreach (var task in page.Items) items.Add(await TaskProjection.ForAsync(state, actor, task, iam, sessions, time.GetUtcNow(), ct, sources));
            result = new { items, page.NextCursor, page.HasMore, page.SecurityRevision };
        }
        else if (call.Name == "tasks.inspect")
        {
            var task = await tasks.FindTaskAsync(tenant, taskId!.Value, ct) ?? throw new AccessDeniedException("resource_unavailable");
            var view = await TaskProjection.ForAsync(state, actor, task, iam, sessions, time.GetUtcNow(), ct, sources);
            var notices = await tasks.ReadTaskNotificationsAsync(tenant, actor, task.TaskId, ct);
            result = view with { Notifications = notices.Items.Select(NotificationView).ToArray() };
        }
        else if (call.Name is "tasks.messages.list" or "agent-messages.list")
        {
            var thread = OptionalId(args, "threadId");
            if (call.Name == "agent-messages.list" && thread is null)
            {
                var page = await messages.ReadAgentThreadsAsync(new(tenant, actor, InstanceId: frame.Owner.AgentInstanceId, Scope: "direct", Cursor: cursor, Limit: limit), ct);
                result = new { items = page.Items.Select(t => new { t.ThreadId, t.FirstInstanceId, t.SecondInstanceId }), page.NextCursor, page.HasMore, page.SecurityRevision };
            }
            else
            {
                var page = await messages.ReadMessagesPageAsync(tenant, actor, taskId, thread, cursor, limit, ct);
                result = new { items = page.Items.Select(MessageView), page.NextCursor, page.HasMore, page.SecurityRevision };
            }
        }
        else if (call.Name is "tasks.messages.send" or "agent-messages.send")
        {
            var kind = call.Name == "agent-messages.send" ? AgentMessageKind.Note : args.GetProperty("kind").GetString() switch {
                "progress" => AgentMessageKind.Progress, "question" => AgentMessageKind.Question, "answer" => AgentMessageKind.Answer, "note" => AgentMessageKind.Note,
                _ => throw new ArgumentException("Invalid message kind.") };
            var saved = await messages.AppendAgentMessageAsync(new(tenant, actor, taskId, OptionalId(args, "threadId"), OptionalId(args, "recipientInstanceId"),
                kind, args.GetProperty("text").GetString()!, args.GetProperty("requestKey").GetString()!, state.Tenant.Revision, ids.NewId(), ids.NewId(), ids.NewId()), ct);
            result = new { saved.Thread.ThreadId, message = new { saved.Message.MessageId, saved.Message.ThreadId, saved.Message.Sequence },
                delivery = saved.Receipt.State.ToString(), saved.Replayed, saved.SecurityRevision };
        }
        else if (call.Name == "tasks.cancel")
        {
            var saved = await tasks.CancelTaskAsync(new(tenant, taskId!.Value, actor, args.GetProperty("expectedRevision").GetInt64(), state.Tenant.Revision,
                args.GetProperty("requestKey").GetString()!, args.TryGetProperty("reason", out var reason) ? TaskLimits.Text(reason.GetString()!, 160, "Reason") : null, ids.NewId()), ct);
            result = new { task = new { saved.Task.TaskId, saved.Task.Revision, state = saved.Task.State.ToString() }, saved.Replayed, saved.SecurityRevision };
        }
        else
        {
            var saved = await tasks.AcknowledgeTaskAsync(new(tenant, taskId!.Value, args.GetProperty("notificationId").GetGuid(), actor,
                args.GetProperty("expectedRevision").GetInt64(), state.Tenant.Revision, args.GetProperty("requestKey").GetString()!, ids.NewId()), ct);
            result = NotificationView(saved);
        }
        if (call.Name is "tasks.list" or "tasks.inspect" or "tasks.messages.list" or "agent-messages.list")
        {
            var after = await Require(frame, call.Name, ct, permissionScope, discover: permissionScope is null);
            if (after.State.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("task_source_changed");
        }
        var output = JsonSerializer.Serialize(result, Json);
        if (Encoding.UTF8.GetByteCount(output) > budget) throw new ArgumentException("Task output exceeds the remaining bound; request a smaller page.");
        return output;
    }
    public static object MessageView(AgentMessage value) => new { value.MessageId, value.ThreadId, value.Sequence,
        senderKind = value.SenderKind.ToString(), value.SenderInstanceId, kind = value.Kind.ToString(), value.Text, value.CreatedAt };
    public static TaskNoticeView NotificationView(TaskNotification value) => new(value.NotificationId, value.TaskId, value.Revision, value.CreatedAt, value.AcknowledgedAt, value.WithheldReason, value.HandlingRunId is not null);
    public static TaskRequest ParseDelegate(JsonElement args)
    {
        try { return ParseDelegateCore(args); }
        catch (Exception error) when (error is FormatException or InvalidOperationException or KeyNotFoundException)
        { throw new ArgumentException("Use valid closed Task fields, UUID selectors and a UTC deadline.", error); }
    }
    private static TaskRequest ParseDelegateCore(JsonElement args)
    {
        var target = args.GetProperty("target");
        var eligible = target.TryGetProperty("mode", out var mode);
        Closed(target, eligible ? ["mode", "candidateInstanceIds"] : ["instanceId"], eligible ? ["mode", "candidateInstanceIds"] : ["instanceId"]);
        if (eligible && mode.GetString() != "eligible") throw new ArgumentException("Invalid task route.");
        var candidates = eligible ? target.GetProperty("candidateInstanceIds").EnumerateArray().Select(v => v.GetGuid()).ToArray() : [target.GetProperty("instanceId").GetGuid()];
        return new(args.GetProperty("objective").GetString()!, args.TryGetProperty("title", out var title) ? title.GetString() : null,
            candidates, eligible, OptionalId(args, "spaceId"), OptionalId(args, "environmentId"),
            args.TryGetProperty("dependsOnTaskIds", out var dependencies) ? dependencies.EnumerateArray().Select(v => v.GetGuid()).ToArray() : [],
            args.TryGetProperty("reportOnCompletion", out var report) && report.GetBoolean(),
            args.TryGetProperty("deadlineAt", out var deadline) ? deadline.GetDateTimeOffset().ToUniversalTime() : null, args.GetProperty("requestKey").GetString()!, OptionalId(args, "retryOfTaskId"));
    }
    public static Guid? OptionalId(JsonElement args, string key) => args.TryGetProperty(key, out var value) ? value.GetGuid() : null;
    public static void Closed(JsonElement value, IEnumerable<string> allowed, IEnumerable<string> required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Use a closed object.");
        var keys = value.EnumerateObject().Select(p => p.Name).ToArray(); var set = allowed.ToHashSet(StringComparer.Ordinal);
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length || keys.Any(k => !set.Contains(k)) || required.Any(k => !keys.Contains(k, StringComparer.Ordinal)))
            throw new ArgumentException("Use the closed task/message arguments; trusted identities are server-owned.");
    }
}
