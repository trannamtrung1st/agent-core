using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class ForegroundGateModel : TenantBackgroundModel
    {
        public TaskCompletionSource ChildEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ChildRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Origin(request) == ExecutionOrigin.ImmediateBackground)
            {
                ChildEntered.TrySetResult();
                await ChildRelease.Task.WaitAsync(cancellationToken);
            }
            await foreach (var item in base.GenerateAsync(request, cancellationToken)) yield return item;
        }
    }
    [Theory(Timeout = 90000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_active_background_continues_through_its_mailbox_without_cloning_or_losing_later_saves(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-foreground-host-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ForegroundGateModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantBackgroundSqliteFactory(root, model) : new TenantBackgroundMemoryFactory(root, model);
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            var started = await StartTenantBackground(factory, owner);
            await model.ChildEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            var child = Assert.Single((await sessions.ListTenantSessionsAsync(started.Tenant, null, 0, 100)).Items, s => s.Owner.SessionId != started.Session);
            var initial = Assert.Single(await runs.ListTenantRunIdsAsync(started.Tenant, child.Owner.SessionId));
            var api = $"/api/v2/tenants/{started.Tenant}/sessions/{child.Owner.SessionId}";
            Assert.Equal(1, (await Json(await owner.GetAsync($"/api/v2/tenants/{started.Tenant}/sessions"))).GetProperty("total").GetInt32());
            var activityApi = $"/api/v2/tenants/{started.Tenant}/agents/{child.Owner.AgentInstanceId}/activity/sessions";
            var activity = await Json(await owner.GetAsync(activityApi + "?pageSize=1&backgroundOnly=true"));
            Assert.Equal(1, activity.GetProperty("total").GetInt32());
            Assert.Equal(child.Owner.SessionId, Assert.Single(activity.GetProperty("items").EnumerateArray()).GetProperty("sessionId").GetGuid());
            var activeDetail = await Json(await owner.GetAsync(activityApi + $"/{child.Owner.SessionId}/background"));
            Assert.Equal(initial, activeDetail.GetProperty("initialRun").GetProperty("runId").GetGuid());
            Assert.Equal("running", activeDetail.GetProperty("initialRun").GetProperty("status").GetString());
            Assert.True(activeDetail.GetProperty("session").GetProperty("canContinue").GetBoolean());
            owner.DefaultRequestHeaders.Remove("X-AgentCore-CSRF");
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(api + "/continue-in-chat", new { expectedRevision = await Revision(owner, started.Tenant), expectedSessionRevision = child.Snapshot.Revision })).StatusCode);
            await Csrf(owner);
            var continued = await owner.PostAsJsonAsync(api + "/continue-in-chat", new { expectedRevision = await Revision(owner, started.Tenant), expectedSessionRevision = child.Snapshot.Revision });
            Assert.Equal(HttpStatusCode.OK, continued.StatusCode);
            Assert.Equal(child.Owner.SessionId, (await Json(continued)).GetProperty("sessionId").GetGuid());
            var visible = (await sessions.FindTenantSessionAsync(started.Tenant, child.Owner.SessionId))!;
            Assert.Equal(child.Snapshot.Origin, visible.Snapshot.Origin);
            Assert.Equal(SessionSurface.ChatList | SessionSurface.BackgroundWork, visible.Snapshot.Surfaces);
            Assert.Equal(AgentRunStatus.Running, (await runs.FindTenantRunAsync(started.Tenant, initial))!.Status);
            Assert.Equal(2, (await Json(await owner.GetAsync($"/api/v2/tenants/{started.Tenant}/sessions"))).GetProperty("total").GetInt32());
            model.ChildRelease.TrySetResult();
            var result = await WaitTenantBackground(factory, started.Tenant, started.Session, started.Source, true);
            Assert.Equal(initial, result.Child.AgentRunId);
            var final = (await sessions.FindTenantSessionAsync(started.Tenant, child.Owner.SessionId))!;
            Assert.Equal(visible.Snapshot.Surfaces, final.Snapshot.Surfaces); Assert.Equal(child.Snapshot.Origin, final.Snapshot.Origin);
            Assert.Single(await runs.ListTenantRunIdsAsync(started.Tenant, child.Owner.SessionId));
            var detail = await Json(await owner.GetAsync(activityApi + $"/{child.Owner.SessionId}/background"));
            Assert.Equal("Owned background result.", detail.GetProperty("initialRun").GetProperty("outcomeText").GetString());
            Assert.Equal("delivered", detail.GetProperty("completionDelivery").GetProperty("status").GetString());
            var history = await Json(await owner.GetAsync(activityApi + $"/{child.Owner.SessionId}/runs"));
            Assert.Equal(initial, Assert.Single(history.GetProperty("items").EnumerateArray()).GetProperty("runId").GetGuid());
            Assert.DoesNotContain("checkpoint", history.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("claim", history.ToString(), StringComparison.OrdinalIgnoreCase);

            Assert.Single(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(started.Tenant, 200), a => a.Action == "sessions.continue-in-chat");
            var repeated = await owner.PostAsJsonAsync(api + "/continue-in-chat", new { expectedRevision = await Revision(owner, started.Tenant), expectedSessionRevision = final.Snapshot.Revision });
            Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
            final = (await sessions.FindTenantSessionAsync(started.Tenant, child.Owner.SessionId))!;
            var followup = await Json(await owner.PostAsJsonAsync(api + "/messages", new { messageId = Guid.NewGuid(), text = "Continue the task here", expectedRevision = await Revision(owner, started.Tenant), expectedSessionRevision = final.Snapshot.Revision }));
            var next = await WaitTenantBackground(factory, started.Tenant, child.Owner.SessionId, followup.GetProperty("runId").GetGuid(), true);
            Assert.NotEqual(result.Child.Admission.ExecutionContext!.RootActivationId, next.Child.Admission.ExecutionContext!.RootActivationId);
            var retained = await Json(await owner.GetAsync(activityApi + $"/{child.Owner.SessionId}/background"));
            Assert.Equal(initial, retained.GetProperty("initialRun").GetProperty("runId").GetGuid());
            Assert.Equal("Owned background result.", retained.GetProperty("initialRun").GetProperty("outcomeText").GetString());

            Assert.Single(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(started.Tenant, 200), a => a.Action == "sessions.continue-in-chat");
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory(Timeout = 90000)] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_rejected_foreground_persistence_does_not_publish_visibility_or_fail_the_independent_child(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-foreground-refused-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ForegroundGateModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new TenantBackgroundSqliteFactory(root, model) : new TenantBackgroundMemoryFactory(root, model);
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            var started = await StartTenantBackground(factory, owner); await model.ChildEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
            var child = Assert.Single((await sessions.ListTenantSessionsAsync(started.Tenant, null, 0, 100)).Items, s => s.Owner.SessionId != started.Session);
            var actor = (await Json(await owner.GetAsync($"/api/v2/tenants/{started.Tenant}/access/me"))).GetProperty("principalId").GetGuid();
            var identity = factory.Services.GetRequiredService<IdentityService>();
            var login = (await identity.SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!;
            var human = new TenantSessionInitiator(login.Account.AccountId, login.Session.SessionId, actor);
            var request = await factory.Services.GetRequiredService<TenantSessionForegroundService>().PrepareAsync(started.Tenant, child.Owner.SessionId, human,
                await Revision(owner, started.Tenant), child.Snapshot.Revision);
            await identity.SignOutAsync(human.AuthenticationSessionId);
            await Assert.ThrowsAsync<AccessDeniedException>(() => factory.Services.GetRequiredService<ITenantSessionForegroundControl>().ContinueAsync(request).AsTask());
            Assert.Equal(SessionSurface.BackgroundWork, (await sessions.FindTenantSessionAsync(started.Tenant, child.Owner.SessionId))!.Snapshot.Surfaces);
            model.ChildRelease.TrySetResult();
            await WaitTenantBackground(factory, started.Tenant, started.Session, started.Source, true);
            Assert.Equal(SessionSurface.BackgroundWork, (await sessions.FindTenantSessionAsync(started.Tenant, child.Owner.SessionId))!.Snapshot.Surfaces);
            Assert.DoesNotContain(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(started.Tenant, 200), a => a.Action == "sessions.continue-in-chat");
            Assert.Equal(1, (await Json(await owner.GetAsync($"/api/v2/tenants/{started.Tenant}/sessions"))).GetProperty("total").GetInt32());
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact(Timeout = 90000)]
    public async Task Tenant_foreground_visibility_and_original_Activity_result_survive_actual_SQLite_host_recreation_without_replay()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-foreground-reopen-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Guid tenant; Guid session; Guid instance; Guid initial;
            await using (var first = new TenantBackgroundSqliteFactory(root, new TenantBackgroundModel()))
            {
                using var owner = first.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
                var started = await StartTenantBackground(first, owner); var result = await WaitTenantBackground(first, started.Tenant, started.Session, started.Source, true);
                tenant = started.Tenant; session = result.Child.SessionId; instance = result.Child.AgentInstanceId; initial = result.Child.AgentRunId;
                var saved = (await first.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!;
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/v2/tenants/{tenant}/sessions/{session}/continue-in-chat",
                    new { expectedRevision = await Revision(owner, tenant), expectedSessionRevision = saved.Snapshot.Revision })).StatusCode);
            }
            var model = new TenantBackgroundModel(); await using var second = new TenantBackgroundSqliteFactory(root, model);
            using var reopened = second.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            await Login(reopened, "owner@example.test", "explicit-synthetic-test-password");
            var detail = await Json(await reopened.GetAsync($"/api/v2/tenants/{tenant}/agents/{instance}/activity/sessions/{session}/background"));
            Assert.Equal(initial, detail.GetProperty("initialRun").GetProperty("runId").GetGuid());
            Assert.Equal("Owned background result.", detail.GetProperty("initialRun").GetProperty("outcomeText").GetString());
            Assert.Equal("immediateBackground", detail.GetProperty("session").GetProperty("origin").GetString());
            Assert.Equal(new[] { "ChatList", "BackgroundWork" }, detail.GetProperty("session").GetProperty("surfaces").EnumerateArray().Select(x => x.GetString()).ToArray());
            Assert.Equal(2, (await Json(await reopened.GetAsync($"/api/v2/tenants/{tenant}/sessions"))).GetProperty("total").GetInt32());
            Assert.Single(await second.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 200), a => a.Action == "sessions.continue-in-chat");
            Assert.Single(await second.Services.GetRequiredService<ITenantRunStore>().ListTenantRunIdsAsync(tenant, session));
            Assert.Empty(model.Requests);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

}
