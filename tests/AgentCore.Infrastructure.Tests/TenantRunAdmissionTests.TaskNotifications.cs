using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Tasks;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    private static async Task<(BackgroundSetup Parent, DelegatedTask Task)> FinishedTaskNotice(Fixture f)
    {
        var parent = await BackgroundParent(f); var child = await BackgroundChild(f, parent);
        await AdmitTaskFixture(f, parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var active = await ClaimBackground(f, child.Run); var outcome = BackgroundOutcome(f, active.Run, active.Snapshot);
        await ((ITenantRuntimeStore)f.Iam).CommitTenantOutcomeAsync(active.Frame, outcome.Snapshot, active.Snapshot.Revision, outcome.Complete, null, f.Ids.NewId());
        var task = child.Admission.Task; var store = (ITenantTaskStore)f.Iam;
        Assert.True(await store.ReconcileTaskAsync(parent.Setup.Tenant, task.TaskId, task.Revision, f.Ids.NewId()));
        return (parent, (await store.FindTaskAsync(parent.Setup.Tenant, task.TaskId))!);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Task_notice_acknowledgment_is_exact_durable_idempotent_and_does_not_change_result(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await FinishedTaskNotice(f);
        var store = (ITenantTaskStore)f.Iam; var tenant = v.Parent.Setup.Tenant; var actor = new TaskActor(Human: v.Parent.Setup.Owner);
        var notice = Assert.Single((await store.ReadTaskNotificationsAsync(tenant, actor, v.Task.TaskId)).Items);
        Assert.Null(notice.AcknowledgedAt); var state = (await f.Iam.ReadTenantAsync(tenant))!;
        var command = new TaskAcknowledge(tenant, v.Task.TaskId, notice.NotificationId, actor, notice.Revision,
            state.Tenant.Revision, "handle-exact-notice", f.Ids.NewId());
        var handled = await store.AcknowledgeTaskAsync(command); Assert.NotNull(handled.AcknowledgedAt);
        Assert.Equal(handled, await store.AcknowledgeTaskAsync(command));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(v.Task), System.Text.Json.JsonSerializer.Serialize(await store.FindTaskAsync(tenant, v.Task.TaskId)));
        Assert.False(await store.ReconcileTaskAsync(tenant, v.Task.TaskId, v.Task.Revision, f.Ids.NewId()));
        Assert.Single((await store.ReadTaskNotificationsAsync(tenant, actor, v.Task.TaskId)).Items);
        if (sqlite)
        {
            var reopened = new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock);
            Assert.Equal(handled, Assert.Single((await reopened.ReadTaskNotificationsAsync(tenant, actor, v.Task.TaskId)).Items));
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Task_notice_and_pages_retire_when_current_source_task_read_is_revoked(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await FinishedTaskNotice(f);
        var tenant = v.Parent.Setup.Tenant; var store = (ITenantTaskStore)f.Iam;
        var actor = new TaskActor(Agent: v.Parent.Frame, Tool: "tasks.list");
        Assert.Single((await store.ReadTasksAsync(new(tenant, actor, SessionId: v.Parent.Frame.SessionId))).Items);
        Assert.Single((await store.ReadTaskNotificationsAsync(tenant, actor, v.Task.TaskId)).Items);
        await f.Access.RevokeAssignmentAsync(v.Parent.Setup.Owner.PrincipalId, tenant, v.Parent.Assignment!.Value, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.ReadTasksAsync(new(tenant, actor, SessionId: v.Parent.Frame.SessionId, Limit: 1)).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => store.ReadTaskNotificationsAsync(tenant, actor, v.Task.TaskId).AsTask());
    }
    [Theory] [InlineData(false, "agent-messages.send")] [InlineData(true, "agent-messages.send")]
    [InlineData(false, "tasks.cancel")] [InlineData(true, "tasks.cancel")]
    public async Task Task_coordination_never_bypasses_current_model_Review_policy(bool sqlite, string permission)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var parent = await BackgroundParent(f);
        var child = await BackgroundChild(f, parent); await AdmitTaskFixture(f, parent.Frame, child.Session, child.Run, f.Ids.NewId());
        var tenant = parent.Setup.Tenant; var task = child.Admission.Task; var tasks = (ITenantTaskStore)f.Iam;
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock)
            .SaveAsync(tenant, parent.Setup.Owner, permission, new(AgentCore.Domain.Security.OperationApprovalMode.Always, false), (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, 0);
        var revision = (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var actor = new TaskActor(Agent: parent.Frame, Tool: permission == "tasks.cancel" ? "tasks.cancel" : "tasks.messages.send");
        if (permission == "tasks.cancel")
        {
            var command = new TaskCancel(tenant, task.TaskId, actor, task.Revision, revision, "review-fence", "Exact cancellation", f.Ids.NewId());
            Assert.Equal("task_review_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => tasks.CancelTaskAsync(command).AsTask())).ReasonCode);
            Assert.Equal(revision, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
            Assert.False((await tasks.FindTaskAsync(tenant, task.TaskId))!.CancellationRequested);
            await tasks.CancelTaskAsync(command with { Actor = new(Human: parent.Setup.Owner) });
        }
        else
        {
            var messages = (ITenantAgentMessageStore)f.Iam;
            var command = new AgentMessageCommand(tenant, actor, task.TaskId, null, null, AgentMessageKind.Note, "Exact guidance", "review-fence", revision,
                f.Ids.NewId(), f.Ids.NewId(), f.Ids.NewId());
            Assert.Equal("task_review_required", (await Assert.ThrowsAsync<AccessDeniedException>(() => messages.AppendAgentMessageAsync(command).AsTask())).ReasonCode);
            Assert.Equal(revision, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision);
            var sent = await messages.AppendAgentMessageAsync(command with { Actor = new(Human: parent.Setup.Owner), Kind = AgentMessageKind.HumanGuidance });
            Assert.Equal(TaskActorKind.Human, sent.Message.SenderKind); Assert.Null(sent.Message.SourceRunId);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Task_model_inspection_discovers_the_exact_notice_for_acknowledgment_without_new_work(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var v = await FinishedTaskNotice(f);
        var tools = new AgentCore.Application.Tasks.TenantTaskToolService(f.Iam, (ITenantTaskStore)f.Iam, (ITenantAgentMessageStore)f.Iam,
            (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, f.RunConfiguration(), FixtureTasks(f), f.Authorizer, f.Ids, f.Clock);
        using var inspected = System.Text.Json.JsonDocument.Parse(await tools.ExecuteAsync(v.Parent.Frame,
            new("inspect-notice", "tasks.inspect", System.Text.Json.JsonSerializer.Serialize(new { taskId = v.Task.TaskId })), 12000, default));
        var notice = Assert.Single(inspected.RootElement.GetProperty("notifications").EnumerateArray());
        Assert.Equal(v.Task.TaskId, inspected.RootElement.GetProperty("taskId").GetGuid());
        using var handled = System.Text.Json.JsonDocument.Parse(await tools.ExecuteAsync(v.Parent.Frame,
            new("ack-notice", "tasks.acknowledge", System.Text.Json.JsonSerializer.Serialize(new { taskId = v.Task.TaskId,
                notificationId = notice.GetProperty("notificationId").GetGuid(), expectedRevision = notice.GetProperty("revision").GetInt64(), requestKey = "model-handle-once" })), 3000, default));
        Assert.Equal(notice.GetProperty("notificationId").GetGuid(), handled.RootElement.GetProperty("notificationId").GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.String, handled.RootElement.GetProperty("acknowledgedAt").ValueKind);
        Assert.Equal(2, (await ((ITenantSessionStore)f.Iam).ListTenantSessionsAsync(v.Parent.Setup.Tenant, null, 0, 100)).Items.Count);
        Assert.Equal(v.Parent.Parent.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(v.Parent.Setup.Tenant, v.Parent.Parent.AgentRunId))!.Revision);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Task_model_reads_and_messages_use_exact_Task_grants_and_current_Review_policy(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var parent = await BackgroundParent(f);
        var first = await BackgroundChild(f, parent, "scoped-first"); await AdmitTaskFixture(f, parent.Frame, first.Session, first.Run, f.Ids.NewId());
        var second = await BackgroundChild(f, parent, "scoped-second"); await AdmitTaskFixture(f, parent.Frame, second.Session, second.Run, f.Ids.NewId());
        var tenant = parent.Setup.Tenant; var human = parent.Setup.Owner; var task = first.Admission.Task;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        await f.Access.RevokeAssignmentAsync(human.PrincipalId, tenant, parent.Assignment!.Value, await Revision());
        var baseline = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Actual source context", ["sessions.read", "agents.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, parent.Setup.Agent.PrincipalId, baseline.RoleId,
            AgentCore.Domain.Security.AccessScope.ForTenant(tenant), await Revision());
        var exact = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "One Task coordination", ["tasks.read", "agent-messages.read", "agent-messages.send"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, parent.Setup.Agent.PrincipalId, exact.RoleId,
            AgentCore.Application.Tasks.TaskAuthority.Scope(tenant, task.TaskId), await Revision());
        var tools = new AgentCore.Application.Tasks.TenantTaskToolService(f.Iam, (ITenantTaskStore)f.Iam, (ITenantAgentMessageStore)f.Iam,
            (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, f.RunConfiguration(), FixtureTasks(f), f.Authorizer, f.Ids, f.Clock);
        async Task<System.Text.Json.JsonDocument> Call(string name, object arguments) => System.Text.Json.JsonDocument.Parse(await tools.ExecuteAsync(parent.Frame,
            new(f.Ids.NewId().ToString("D"), name, System.Text.Json.JsonSerializer.Serialize(arguments)), 12000, default));
        Assert.Contains("tasks.inspect", await tools.OfferedAsync(parent.Parent, default));
        using (var page = await Call("tasks.list", new { scope = "created", limit = 10 }))
            Assert.Equal(task.TaskId, Assert.Single(page.RootElement.GetProperty("items").EnumerateArray()).GetProperty("taskId").GetGuid());
        using (var inspected = await Call("tasks.inspect", new { taskId = task.TaskId })) Assert.Equal(task.TaskId, inspected.RootElement.GetProperty("taskId").GetGuid());
        await Assert.ThrowsAsync<AccessDeniedException>(() => Call("tasks.inspect", new { taskId = second.Admission.Task.TaskId }));
        using (var sent = await Call("tasks.messages.send", new { taskId = task.TaskId, kind = "note", text = "Exact scoped guidance", requestKey = "scoped-message" }))
            Assert.True(sent.RootElement.GetProperty("message").GetProperty("messageId").GetGuid() != Guid.Empty);
        using (var messages = await Call("tasks.messages.list", new { taskId = task.TaskId, limit = 10 }))
            Assert.Equal("Exact scoped guidance", Assert.Single(messages.RootElement.GetProperty("items").EnumerateArray()).GetProperty("text").GetString());
        var policy = new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock);
        await policy.SaveAsync(tenant, human, "agents.read", new(AgentCore.Domain.Security.OperationApprovalMode.Always, false), await Revision(), 0);
        using (var metadata = await Call("tasks.inspect", new { taskId = task.TaskId }))
        {
            foreach (var name in new[] { "sourceInstanceId", "sourceAgentName", "selectedTargetInstanceId", "selectedTargetName" })
                Assert.Equal(System.Text.Json.JsonValueKind.Null, metadata.RootElement.GetProperty(name).ValueKind);
            Assert.Equal(task.TaskId, metadata.RootElement.GetProperty("taskId").GetGuid());
        }
        await policy.SaveAsync(tenant, human, "sessions.read", new(AgentCore.Domain.Security.OperationApprovalMode.Always, false), await Revision(), 0);
        // Notification discovery requires the actual source Session read too, so
        // this policy closes the entire tool response rather than returning metadata.
        await Assert.ThrowsAsync<AccessDeniedException>(() => Call("tasks.inspect", new { taskId = task.TaskId }));
        await policy.SaveAsync(tenant, human, "sessions.read", new(AgentCore.Domain.Security.OperationApprovalMode.None, false), await Revision(), 1);
        await policy.SaveAsync(tenant, human, "agent-messages.read", new(AgentCore.Domain.Security.OperationApprovalMode.Always, false), await Revision(), 0);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Call("tasks.messages.list", new { taskId = task.TaskId, limit = 10 }));
        await policy.SaveAsync(tenant, human, "tasks.read", new(AgentCore.Domain.Security.OperationApprovalMode.Always, false), await Revision(), 0);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Call("tasks.inspect", new { taskId = task.TaskId }));
        Assert.DoesNotContain("tasks.inspect", await tools.OfferedAsync(parent.Parent, default));
        Assert.Equal(2, (await ((ITenantTaskStore)f.Iam).ReadTasksAsync(new(tenant, new(Human: human), SessionId: parent.Frame.SessionId))).Items.Count);
    }

}
