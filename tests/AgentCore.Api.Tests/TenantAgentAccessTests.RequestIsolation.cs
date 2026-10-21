using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Security;
using System.Text.Json;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using AgentCore.Api.Realtime;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_request_isolation_noop_scan_cannot_strand_an_unrelated_claim_loaded_before_registration(bool sqlite)
    {
        await using var factory = new RequestIsolationFactory(sqlite); using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, _) = await IsolationAgent(client); var api = $"/api/v2/tenants/{tenant}";
        var agent = await Json(await client.GetAsync(api + $"/agents/{instance}"));
        var role = await Json(await client.PostAsJsonAsync(api + "/roles", new { name = "Unrelated dispatch executor", permissions = new[] { "agents.chat" }, expectedRevision = await Revision(client, tenant) }));
        await Json(await client.PostAsJsonAsync(api + "/role-assignments", new { principalId = agent.GetProperty("principalId").GetGuid(), roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(client, tenant) }));
        async Task<Guid> Create() => (await Json(await client.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedInstanceRevision = 1, expectedRevision = await Revision(client, tenant) }))).GetProperty("sessionId").GetGuid();
        var scanned = await Create(); var target = await Create(); var host = factory.Services.GetRequiredService<TenantSessionRuntimeHost>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeforeDispatchRegistration = async id => { if (id == target) { entered.TrySetResult(); await release.Task; } };
        try
        {
            var submitted = await Json(await client.PostAsJsonAsync(api + $"/sessions/{target}/messages", new { messageId = Guid.NewGuid(), text = "Dispatch despite an unrelated cleanup scan", expectedRevision = await Revision(client, tenant), expectedSessionRevision = 1 }));
            var id = submitted.GetProperty("runId").GetGuid(); var executing = factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var owner = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, scanned))!.Owner;
            Assert.False(await host.SettleRequestAsync(owner)); release.TrySetResult(); await executing.WaitAsync(TimeSpan.FromSeconds(15));
            var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            for (var i = 0; i < 200 && (await runs.FindTenantRunAsync(tenant, id))?.IsTerminal != true; i++) await Task.Delay(25);
            Assert.Equal(AgentRunStatus.Completed, (await runs.FindTenantRunAsync(tenant, id))!.Status);
            Assert.Single(await runs.ListTenantRunIdsAsync(tenant, target));
        }
        finally { release.TrySetResult(); host.BeforeDispatchRegistration = null; }
    }

    [Theory]
    [InlineData(false, "plain")] [InlineData(true, "plain")]
    [InlineData(false, "relaxed")] [InlineData(true, "relaxed")]
    [InlineData(false, "lease")] [InlineData(true, "lease")]
    [InlineData(true, "restart")]
    public async Task Tenant_request_isolation_rejects_another_root_preserves_exact_replay_and_completes_after_actual_Run(bool sqlite, string variant)
    {
        var clock = variant == "restart" ? new FakeTimeProvider(DateTimeOffset.UtcNow) : null;
        await using var factory = new RequestIsolationFactory(sqlite, clock: clock);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
        var (tenant, instance, isolation) = await IsolationAgent(client); var api = $"/api/v2/tenants/{tenant}";
        var early = variant == "relaxed" ? (await Json(await client.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedInstanceRevision = 1, expectedRevision = await Revision(client, tenant) }))).GetProperty("sessionId").GetGuid() : (Guid?)null;
        var preview = await Json(await client.PostAsync(isolation + "/presets/Stateless", null));
        Assert.Equal(HttpStatusCode.OK, (await ApplyIsolation(client, isolation, preview)).StatusCode);
        var agent = await Json(await client.GetAsync(api + $"/agents/{instance}"));
        var role = await Json(await client.PostAsJsonAsync(api + "/roles", new { name = "Request text executor", permissions = new[] { "agents.chat" }, expectedRevision = await Revision(client, tenant) }));
        await Json(await client.PostAsJsonAsync(api + "/role-assignments", new { principalId = agent.GetProperty("principalId").GetGuid(), roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(client, tenant) }));
        var session = early ?? (await Json(await client.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedInstanceRevision = 2, expectedRevision = await Revision(client, tenant) }))).GetProperty("sessionId").GetGuid();
        var message = Guid.NewGuid(); var route = api + $"/sessions/{session}/messages";
        var first = await Json(await client.PostAsJsonAsync(route, new { messageId = message, text = "First isolated request", expectedRevision = await Revision(client, tenant), expectedSessionRevision = 1 }));
        var run = first.GetProperty("runId").GetGuid();
        if (variant is "lease" or "restart")
        {
            var profile = new SandboxProfile("p-" + Guid.NewGuid().ToString("N"), 1, "Request lease", "Retained request generation", true, "docker.io/library/python@sha256:" + new string('a', 64), TenantId: tenant);
            var lease = new SandboxLease(session, instance, Guid.NewGuid(), 1, profile, SandboxLeaseState.Ready, "unconfirmed-request-provider", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), TenantId: tenant, ExecutorPrincipalId: agent.GetProperty("principalId").GetGuid(), AdmissionRunId: run, AdmissionToolCallId: "retained-request-lease");
            if (!sqlite)
            {
                var backing = (InMemorySandboxStore)factory.Services.GetRequiredService<ISandboxStore>();
                lock (backing.TenantGate) { Assert.True(backing.SaveTenantProfile(new(tenant, profile.Id, DateTimeOffset.UtcNow), profile, 0)); backing.SaveTenantLease(lease); }
            }
            else
            {
                await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContextAsync();
                db.Add(new TenantSandboxProfileRecord(profile.Id, tenant, DateTimeOffset.UtcNow)); db.Add(new SandboxProfileRow { Id = profile.Id, TenantId = tenant, Revision = 1, Json = JsonSerializer.Serialize(profile, SandboxRuntimePersistence.Json) });
                db.Add(SandboxRuntimePersistence.Row(lease)); await db.SaveChangesAsync();
            }
        }
        if (variant == "relaxed") { preview = await Json(await client.PostAsync(isolation + "/presets/DefinitionDefaults", null)); Assert.Equal(HttpStatusCode.OK, (await ApplyIsolation(client, isolation, preview)).StatusCode); }
        var second = await client.PostAsJsonAsync(route, new { messageId = Guid.NewGuid(), text = "Another independent request", expectedRevision = await Revision(client, tenant), expectedSessionRevision = 2 });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode); Assert.Contains("fresh_session_required", await second.Content.ReadAsStringAsync());
        var replay = await Json(await client.PostAsJsonAsync(route, new { messageId = message, text = "First isolated request", expectedRevision = await Revision(client, tenant), expectedSessionRevision = 1 }));
        Assert.Equal(run, replay.GetProperty("runId").GetGuid());
        await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8);
        var store = factory.Services.GetRequiredService<ITenantRunStore>();
        for (var i = 0; i < 200 && (await store.FindTenantRunAsync(tenant, run))?.IsTerminal != true; i++) await Task.Delay(25);
        Assert.Equal(AgentRunStatus.Completed, (await store.FindTenantRunAsync(tenant, run))!.Status);
        var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
        if (variant == "restart")
        {
            await factory.DisposeAsync();
            await using var reopened = new RequestIsolationFactory(true, factory.Root, clock);
            using var again = reopened.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            await Login(again, "owner@example.test", "explicit-synthetic-test-password");
            var inventory = await reopened.Services.GetRequiredService<ITenantRequestIsolationStore>().ListTenantRequestIsolationAsync(null, 25);
            Assert.Contains(inventory.Items, item => item.TenantId == tenant && item.SessionId == session);
            await CompleteRetainedRequestLease(reopened, tenant, session, clock);
            await AssertCompletedIsolatedRequest(reopened, again, tenant, session, run, message);
            return;
        }
        if (variant == "lease") await CompleteRetainedRequestLease(factory, tenant, session);
        await AssertCompletedIsolatedRequest(factory, client, tenant, session, run, message);
    }

    private static async Task CompleteRetainedRequestLease(RequestIsolationFactory factory, Guid tenant, Guid session, FakeTimeProvider? clock = null)
    {
        var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
        var parent = (await sessions.FindTenantSessionAsync(tenant, session))!;
        Assert.Equal(SessionLifecycleStatus.Active, parent.Snapshot.LifecycleStatus);
        var isolationStore = factory.Services.GetRequiredService<ITenantRequestIsolationStore>();
        Assert.False(await isolationStore.CompleteTenantIsolatedRequestAsync(parent.Owner, parent.Snapshot.Revision, DateTimeOffset.UtcNow));
        Assert.False(await factory.Services.GetRequiredService<ITenantRequestIsolationControl>().SettleRequestAsync(parent.Owner));
        var sandbox = factory.Services.GetRequiredService<ITenantSandboxRuntimeStore>();
        var lease = (await sandbox.ReadTenantSandboxRuntimeAsync(tenant, session))!.Lease!;
        for (var i = 0; i < 200 && lease.State == SandboxLeaseState.Ready; i++)
        {
            await Task.Delay(25); await factory.Services.GetRequiredService<ITenantRequestIsolationControl>().SettleRequestAsync(parent.Owner);
            lease = (await sandbox.ReadTenantSandboxRuntimeAsync(tenant, session))!.Lease!;
        }
        Assert.Contains(lease.State, new[] { SandboxLeaseState.Terminating, SandboxLeaseState.NeedsReconciliation });
        Assert.Contains(lease.CleanupReason, new[] { "requestCompleted", "authorityOrLifetime" });
        Assert.True(await sandbox.TrySaveTenantSandboxLeaseFactsAsync(tenant, lease with { Revision = lease.Revision + 1, State = SandboxLeaseState.Terminated }, lease.Revision));
        Assert.False(await isolationStore.CompleteTenantIsolatedRequestAsync(parent.Owner, parent.Snapshot.Revision + 1, DateTimeOffset.UtcNow));
        Assert.Null(await isolationStore.ReadTenantRequestIsolationAsync(parent.Owner with { ExecutorPrincipalId = Guid.NewGuid() }));
        Assert.False(await isolationStore.CompleteTenantIsolatedRequestAsync(parent.Owner with { TenantId = Guid.NewGuid() }, parent.Snapshot.Revision, DateTimeOffset.UtcNow));
        var control = factory.Services.GetRequiredService<ITenantRequestIsolationControl>();
        if (clock is null)
        {
            var outcomes = await Task.WhenAll(control.SettleRequestAsync(parent.Owner).AsTask(), control.SettleRequestAsync(parent.Owner).AsTask());
            Assert.InRange(outcomes.Count(result => result), 0, 1); // The supervised release may also win this same reservation.
        }
        else clock.Advance(TimeSpan.FromSeconds(16));
    }

    private static async Task AssertCompletedIsolatedRequest(RequestIsolationFactory factory, HttpClient client, Guid tenant, Guid session, Guid run, Guid message)
    {
        var sessions = factory.Services.GetRequiredService<ITenantSessionStore>(); var store = factory.Services.GetRequiredService<ITenantRunStore>();
        var route = $"/api/v2/tenants/{tenant}/sessions/{session}/messages";
        for (var i = 0; i < 200 && (await sessions.FindTenantSessionAsync(tenant, session))?.Snapshot.LifecycleStatus != SessionLifecycleStatus.Completed; i++) await Task.Delay(25);
        Assert.Equal(SessionLifecycleStatus.Completed, (await sessions.FindTenantSessionAsync(tenant, session))!.Snapshot.LifecycleStatus);
        var revision = await Revision(client, tenant);
        var replay = await Json(await client.PostAsJsonAsync(route, new { messageId = message, text = "First isolated request", expectedRevision = 1, expectedSessionRevision = 1 }));
        Assert.Equal(run, replay.GetProperty("runId").GetGuid()); Assert.False(replay.GetProperty("created").GetBoolean()); Assert.Equal(revision, await Revision(client, tenant));
        var changed = await client.PostAsJsonAsync(route, new { messageId = message, text = "Changed retry body", expectedRevision = revision, expectedSessionRevision = 1 });
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode); Assert.Contains("message_id_conflict", await changed.Content.ReadAsStringAsync());
        Assert.Single(await store.ListTenantRunIdsAsync(tenant, session));
        var original = (await store.FindTenantRunAsync(tenant, run))!; var root = (await sessions.FindTenantSessionAsync(tenant, session))!.Owner;
        await Assert.ThrowsAsync<SecurityConflictException>(() => store.FindTenantAcceptedHumanInputAsync(root, original.Admission.ExecutionContext!.OriginalInitiatorPrincipalId!.Value,
            message, "First isolated request", [], null, SessionMode.Voice).AsTask());
    }

    private sealed class RequestIsolationFactory(bool sqlite, string? root = null, TimeProvider? clock = null) : AgentCoreApiFactory
    {
        protected override bool EnableTenantRunExecution => true;
        public string Root { get; } = root ?? Path.Combine(Path.GetTempPath(), "tenant-request-isolation-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (clock is not null) builder.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
            if (sqlite) builder.ConfigureTestServices(services => {
                Directory.CreateDirectory(Root); var connection = $"Data Source={Path.Combine(Root, "state.db")};Foreign Keys=True;Default Timeout=10";
                services.AddDbContextFactory<AgentCoreDbContext>(options => options.UseSqlite(connection)); TestSqliteIdentity.Configure(services, connection, Path.Combine(Root, "keys"));
            });
        }
    }
}
