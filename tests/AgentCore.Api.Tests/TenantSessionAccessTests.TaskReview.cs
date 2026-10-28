using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using AgentCore.Api.Http;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Hosting;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private static async Task<(Guid Tenant, Guid Source, Guid Worker, Guid Session, Guid Human)> TaskReviewSetup(WebApplicationFactory<Program> factory, HttpClient owner)
    {
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var api = $"/api/v2/tenants/{tenant}";
        var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        async Task<Guid> Agent() => (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
        var source = await Agent(); var worker = await Agent();
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = source, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
        await Json(await owner.PutAsJsonAsync(api + $"/agents/{worker}/cooperation", new { cooperation = new { acceptTasks = true }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }));
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var target = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, worker))!;
        var role = await access.SaveRoleAsync(human, tenant, null, "Explicit discovery worker", ["sessions.create", "agent-runs.create", "spaces.read", "agents.read", "agent-messages.read"], await Revision(owner, tenant));
        await access.AssignRoleAsync(human, tenant, target.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        var sourceAgent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, source))!;
        var sourceRead = await access.SaveRoleAsync(human, tenant, null, "Actual source context read", ["spaces.read"], await Revision(owner, tenant));
        await access.AssignRoleAsync(human, tenant, sourceAgent.PrincipalId, sourceRead.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
        return (tenant, source, worker, session, human);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Task_target_HTTP_finds_Shared_only_default_and_all_authorized_Space_pages(bool sqlite, bool manySpaces)
    {
        var root = Path.Combine(Path.GetTempPath(), "task-target-review-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel()) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel());
            using var owner = factory.CreateClient(); var v = await TaskReviewSetup(factory, owner); var api = $"/api/v2/tenants/{v.Tenant}";
            var spaces = factory.Services.GetRequiredService<TenantSpaceService>(); var store = factory.Services.GetRequiredService<ITenantSpaceStore>();
            var access = factory.Services.GetRequiredService<TenantAccessService>(); var identity = factory.Services.GetRequiredService<IdentityService>();
            var account = await identity.RegisterAsync("shared-delegator@example.test", "Shared delegator", "strong-shared-delegator-password");
            var role = await access.SaveRoleAsync(v.Human, v.Tenant, null, "Delegate through Shared Space", ["tasks.read", "tasks.delegate", "agents.read", "sessions.read", "sessions.manage", "sessions.create", "agent-runs.create"], await Revision(owner, v.Tenant));
            var invite = await access.InviteAsync(v.Human, v.Tenant, account.LoginKey, role.RoleId, await Revision(owner, v.Tenant));
            var member = await access.AcceptInvitationAsync(account.AccountId, v.Tenant, invite.Invitation.InvitationId, invite.Token);
            var read = await access.SaveRoleAsync(v.Human, v.Tenant, null, "Exact Shared Space read", ["spaces.read"], await Revision(owner, v.Tenant));
            var shared = new List<AgentCore.Domain.Spaces.Space>(); var readAssignments = new List<Guid>();
            for (var i = 0; i < (manySpaces ? 103 : 1); i++)
            {
                // Stable ordered IDs put the eligible Shared Space beyond the first page.
                var id = Guid.Parse($"ffffffff-ffff-4fff-8fff-{i + 1:000000000000}");
                var space = await spaces.CreateAsync(v.Human, v.Tenant, id, $"Reviewed Shared {i + 1}", "", "", await Revision(owner, v.Tenant)); shared.Add(space);
                readAssignments.Add((await access.AssignRoleAsync(v.Human, v.Tenant, member.PrincipalId, read.RoleId, TenantSpaceService.Scope(v.Tenant, id), await Revision(owner, v.Tenant))).AssignmentId);
            }
            var selected = shared.Last();
            await spaces.SetMemberAsync(v.Human, v.Tenant, selected.SpaceId, v.Worker, "Worker", "", [], [], await Revision(owner, v.Tenant), selected.Revision, 0);
            using var reader = factory.CreateClient(); await Login(reader, account.LoginKey, "strong-shared-delegator-password");
            var own = (await store.FindPrivateSpaceAsync(v.Tenant, v.Worker))!;
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(api + $"/spaces/{own.SpaceId}")).StatusCode);
            var targets = await Json(await reader.GetAsync(api + $"/sessions/{v.Session}/task-targets"));
            var target = Assert.Single(targets.GetProperty("items").EnumerateArray());
            Assert.Equal(v.Worker, target.GetProperty("instanceId").GetGuid());
            Assert.Equal(selected.SpaceId, target.GetProperty("defaultSpaceId").GetGuid());
            Assert.Equal(selected.SpaceId, Assert.Single(target.GetProperty("spaces").EnumerateArray()).GetProperty("spaceId").GetGuid());
            Assert.DoesNotContain(own.SpaceId.ToString("D"), targets.GetRawText());
            if (manySpaces) Assert.DoesNotContain(selected.SpaceId, (await store.ListSpacesAsync(v.Tenant, shared.Select(s => s.SpaceId).ToArray(), 0, 100)).Items.Select(s => s.SpaceId));
            var admitted = await Json(await reader.PostAsJsonAsync(api + $"/sessions/{v.Session}/tasks", new { objective = "Inspect the explicitly shared facts", target = new { instanceId = v.Worker }, spaceId = selected.SpaceId, requestKey = "shared-only-delegation", expectedSecurityRevision = await Revision(reader, v.Tenant) }));
            Assert.True(admitted.TryGetProperty("task", out var receipt), admitted.GetRawText());
            var task = (await factory.Services.GetRequiredService<ITenantTaskStore>().FindTaskAsync(v.Tenant, receipt.GetProperty("taskId").GetGuid()))!;
            Assert.Equal(selected.SpaceId, task.Assignment.SpaceId);
            // Revoking the human's only usable context retires discovery without
            // bypassing the incumbent active-worker membership edit guard.
            await access.RevokeAssignmentAsync(v.Human, v.Tenant, readAssignments.Last(), await Revision(owner, v.Tenant));
            Assert.Empty((await Json(await reader.GetAsync(api + $"/sessions/{v.Session}/task-targets"))).GetProperty("items").EnumerateArray());
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public class PausedTaskReads : DispatchProxy
    {
        public ITenantTaskStore Inner = null!;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ITenantTaskStore.ReadTasksAsync)) return Read((TaskQuery)args![0]!, (CancellationToken)args[1]!);
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private async ValueTask<TaskPage<DelegatedTask>> Read(TaskQuery query, CancellationToken ct)
        {
            var saved = await Inner.ReadTasksAsync(query, ct); Entered.TrySetResult(); await Release.Task.WaitAsync(ct); return saved;
        }
    }

    [Theory] [InlineData(false, "active")] [InlineData(true, "active")] [InlineData(false, "reattach")] [InlineData(true, "reattach")] [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    public async Task Task_hint_slow_reads_leave_Chat_commands_and_delivery_live_and_fence_retired_subscriptions(bool sqlite, string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "task-hint-review-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var proxy = DispatchProxy.Create<ITenantTaskStore, PausedTaskReads>(); var pause = (PausedTaskReads)proxy;
        try
        {
            await using WebApplicationFactory<Program> original = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel()) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel());
            // Freeze only the hint deadline clock; actual execution and fixture timeouts stay live.
            await using var factory = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
                services.RemoveAll<TenantSessionLiveDelivery>();
                services.AddSingleton(p => ActivatorUtilities.CreateInstance<TenantSessionLiveDelivery>(p, new FakeTimeProvider(DateTimeOffset.UtcNow)));
            }));
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            var v = await TaskReviewSetup(factory, owner); var api = $"/api/v2/tenants/{v.Tenant}";
            // Own the exact polling/dispatch pass in this fixture so an automatic
            // poll cannot win the per-connection guard before our paused read.
            await factory.Services.GetServices<IHostedService>().OfType<AgentRunHostedService>().Single().StopAsync(CancellationToken.None);
            await Json(await owner.PostAsJsonAsync(api + $"/sessions/{v.Session}/tasks", new { objective = "Inspect current Task facts", target = new { instanceId = v.Worker }, requestKey = "hint-source", expectedSecurityRevision = await Revision(owner, v.Tenant) }));
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(v.Tenant, v.Session))).Accepted);
            var first = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.ready"));
            var delivery = factory.Services.GetRequiredService<TenantSessionLiveDelivery>(); pause.Inner = factory.Services.GetRequiredService<ITenantTaskStore>();
            var hints = delivery.PublishTaskHintsAsync(proxy, factory.Services.GetRequiredService<ITenantAgentMessageStore>(), CancellationToken.None).AsTask();
            try
            {
                await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20)); var before = events.Count;
                if (boundary == "reattach") Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(v.Tenant, v.Session))).Accepted);
                // A normal HTTP input must reach terminal live publication while the saved Task query is held.
                var snapshot = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!;
                var admission = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{v.Session}/messages", new { messageId = Guid.NewGuid(), text = "Chat remains responsive during a slow Task query", expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = snapshot.Snapshot.Revision }));
                Assert.True(admission.TryGetProperty("responseId", out var responseId), admission.GetRawText());
                var response = responseId.GetString();
                await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, CancellationToken.None);
                await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == response));
                Assert.False(hints.IsCompleted); Assert.False(pause.Release.Task.IsCompleted);
                // Revoke the actual attached authentication session, not the extra fixture login.
                if (boundary == "revoke") Assert.True((await owner.PostAsJsonAsync("/api/v2/auth/logout", new { })).IsSuccessStatusCode);
                pause.Release.TrySetResult(); await hints.WaitAsync(TimeSpan.FromSeconds(20));
                if (boundary == "revoke") Assert.DoesNotContain(events.Skip(before), e => e.Type is "task.changed" or "agent-message.changed");
                if (boundary != "revoke")
                {
                    await delivery.PublishTaskHintsAsync(pause.Inner, factory.Services.GetRequiredService<ITenantAgentMessageStore>(), CancellationToken.None);
                    await AwaitTenantWire(() => events.Skip(before).FirstOrDefault(e => e.Type == "task.changed"));
                }
                if (boundary == "reattach") Assert.DoesNotContain(events.Skip(before), e => e.AttachmentId == first.AttachmentId);
                var all = events.ToArray(); foreach (var attachment in all.GroupBy(e => e.AttachmentId))
                    Assert.True(attachment.Select(e => e.Sequence).SequenceEqual(attachment.Select(e => e.Sequence).Order()));
            }
            finally { pause.Release.TrySetResult(); await hints; }
        }
        finally { pause.Release.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
