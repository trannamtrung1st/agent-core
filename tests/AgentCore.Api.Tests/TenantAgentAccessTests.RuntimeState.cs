using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantAgentAccessTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_runtime_state_counts_only_owned_readable_sessions_and_runs_without_private_bodies(bool sqlite)
    {
        await using var factory = new RuntimeStateFactory(sqlite); using var owner = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var (tenant, id, isolation) = await IsolationAgent(owner); var api = $"/api/v2/tenants/{tenant}"; var root = isolation.Replace("state-isolation", "runtime-state");
        async Task<Guid> Session(Guid agent) => (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = agent,
            expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
        var a = await Session(id); var b = await Session(id);
        foreach (var session in new[] { a, b })
            Assert.Equal(HttpStatusCode.Accepted, (await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Private runtime count sentinel",
                expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 })).StatusCode);
        var other = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
        _ = await Session(other.GetProperty("instance").GetProperty("instanceId").GetGuid());
        var observed = await Json(await owner.GetAsync(root)); var state = observed.GetProperty("observation");
        Assert.Equal(2, state.GetProperty("sessions").GetProperty("open").GetInt32()); Assert.Equal(2, state.GetProperty("runs").GetProperty("queued").GetInt32());
        Assert.Equal("readableInstance", state.GetProperty("scope").GetString()); Assert.DoesNotContain("Private runtime count sentinel", observed.GetRawText());
        Assert.Equal(0, state.GetProperty("hostedSessions").GetProperty("runtimes").GetInt32());
        Assert.Equal(15, (state.GetProperty("freshThroughUtc").GetDateTimeOffset() - state.GetProperty("observedAtUtc").GetDateTimeOffset()).TotalSeconds);
        var selected = (await Json(await owner.GetAsync(root + $"?sessionId={a}"))).GetProperty("observation");
        Assert.Equal(1, selected.GetProperty("sessions").GetProperty("open").GetInt32()); Assert.Equal(1, selected.GetProperty("runs").GetProperty("queued").GetInt32());
        var beta = (await Json(await owner.PostAsJsonAsync("/api/v2/system/tenants", new { name = "Runtime foreign", slug = "runtime-foreign" }))).GetProperty("tenantId").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(root.Replace(tenant.ToString(), beta.ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync(root + $"?sessionId={Guid.NewGuid()}")).StatusCode);
        using var anonymous = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(root)).StatusCode);
        var before = await Revision(owner, tenant); _ = await Json(await owner.GetAsync(root)); Assert.Equal(before, await Revision(owner, tenant));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_runtime_state_exact_reader_gets_no_hidden_counts_or_source_calls(bool sqlite)
    {
        await using var factory = new RuntimeStateFactory(sqlite); using var owner = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var (tenant, id, isolation) = await IsolationAgent(owner); var api = $"/api/v2/tenants/{tenant}"; var root = isolation.Replace("state-isolation", "runtime-state");
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = id, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
        var role = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Runtime membership only", permissions = new[] { "agents.chat" }, expectedRevision = await Revision(owner, tenant) }));
        var invite = await Json(await owner.PostAsJsonAsync(api + "/members/invite", new { login = "runtime-reader@example.test", roleId = role.GetProperty("roleId").GetGuid(), expectedRevision = await Revision(owner, tenant) }));
        using var member = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); await Csrf(member);
        Assert.Equal(HttpStatusCode.Created, (await member.PostAsJsonAsync("/api/v2/auth/register", new { login = "runtime-reader@example.test", displayName = "Runtime reader", password = "runtime-reader-password",
            invitation = new { tenantId = tenant, invitationId = invite.GetProperty("invitationId").GetGuid(), token = invite.GetProperty("token").GetString() } })).StatusCode);
        await Login(member, "runtime-reader@example.test", "runtime-reader-password");
        var principal = (await Json(await owner.GetAsync(api + "/members"))).GetProperty("items").EnumerateArray().Single(x => x.GetProperty("login").GetString() == "RUNTIME-READER@EXAMPLE.TEST").GetProperty("principalId").GetGuid();
        var readRole = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Exact runtime agent", permissions = new[] { "agents.read", "browser.read" }, expectedRevision = await Revision(owner, tenant) }));
        var access = await Json(await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = principal, roleId = readRole.GetProperty("roleId").GetGuid(), resourceType = "agent", resourceId = id, expectedRevision = await Revision(owner, tenant) }));
        var before = factory.Browser.Calls;
        var observation = (await Json(await member.GetAsync(root))).GetProperty("observation");
        Assert.Equal(JsonValueKind.Null, observation.GetProperty("sessions").ValueKind); Assert.Equal(JsonValueKind.Null, observation.GetProperty("runs").ValueKind);
        Assert.Equal(JsonValueKind.Null, observation.GetProperty("hostedSessions").ValueKind); Assert.Equal(before, factory.Browser.Calls);
        Assert.All(observation.GetProperty("resources").EnumerateArray(), x => Assert.Equal("restricted", x.GetProperty("availability").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync(root + $"?sessionId={session}")).StatusCode);
        var sessionRole = await Json(await owner.PostAsJsonAsync(api + "/roles", new { name = "Exact runtime Session", permissions = new[] { "sessions.read" }, expectedRevision = await Revision(owner, tenant) }));
        await Json(await owner.PostAsJsonAsync(api + "/role-assignments", new { principalId = principal, roleId = sessionRole.GetProperty("roleId").GetGuid(), resourceType = "session", resourceId = session, expectedRevision = await Revision(owner, tenant) }));
        var allowed = (await Json(await member.GetAsync(root))).GetProperty("observation");
        Assert.Equal(1, allowed.GetProperty("sessions").GetProperty("open").GetInt32()); Assert.Equal(JsonValueKind.Null, allowed.GetProperty("runs").ValueKind);
        Assert.Equal(new[] { session }, factory.Browser.ReadableSessions); Assert.Equal(id, factory.Browser.Owner!.AgentInstanceId);
        factory.Browser.AfterRead = async () => Assert.Equal(System.Net.HttpStatusCode.NoContent,
            (await owner.DeleteAsync(api + $"/role-assignments/{access.GetProperty("assignmentId").GetGuid()}?expectedRevision={await Revision(owner, tenant)}")).StatusCode);
        var revoked = await member.GetAsync(root); Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.DoesNotContain("observation", await revoked.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_runtime_state_does_not_release_observed_facts_after_original_login_is_revoked(bool relogin)
    {
        await using var factory = new RuntimeStateFactory(false); using var owner = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false }); var (_, _, isolation) = await IsolationAgent(owner);
        factory.Browser.AfterRead = async () => { Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
            if (relogin) await Login(owner, "owner@example.test", "explicit-synthetic-test-password"); };
        var response = await owner.GetAsync(isolation.Replace("state-isolation", "runtime-state")); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("liveContexts", body); Assert.DoesNotContain("sessions", body);
    }

    [Fact]
    public async Task Tenant_runtime_state_bounds_uncooperative_source_and_preserves_other_observations()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); await using var factory = new RuntimeStateFactory(false, clock); using var owner = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var (_, _, isolation) = await IsolationAgent(owner); var release = new TaskCompletionSource<ResourceStateObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Browser.Pending = release.Task;
        var request = owner.GetAsync(isolation.Replace("state-isolation", "runtime-state")); await factory.Browser.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(3)); var response = await request.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = (await Json(response)).GetProperty("observation"); Assert.Equal("partial", state.GetProperty("availability").GetString());
        Assert.Equal(0, state.GetProperty("sessions").GetProperty("open").GetInt32());
        Assert.Contains(state.GetProperty("issues").EnumerateArray(), x => x.GetProperty("kind").GetString() == "browser" && x.GetProperty("code").GetString() == "observation_timeout");
        release.SetResult(new("browser", "available", "isolated", clock.GetUtcNow(), LiveContexts: 100));
        Assert.DoesNotContain("100", state.GetRawText());
    }

    private sealed class RuntimeBrowserProbe : ITenantBrowserStateReader
    {
        public int Calls; public TenantBrowserAgentOwner? Owner; public IReadOnlyList<Guid>? ReadableSessions;
        public Func<Task>? AfterRead; public Task<ResourceStateObservation>? Pending;
        public Func<int, Task>? BeforeRead; public bool CountPages;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<ResourceStateObservation> ObserveTenantSessionAsync(TenantBrowserOwner owner, CancellationToken ct = default) => throw new NotSupportedException();
        public async ValueTask<ResourceStateObservation> ObserveTenantAgentAsync(TenantBrowserAgentOwner owner, IReadOnlyList<Guid>? readableSessions, CancellationToken ct = default)
        {
            Calls++; Owner = owner; ReadableSessions = readableSessions; Started.TrySetResult();
            if (BeforeRead is { } before) await before(Calls);
            if (AfterRead is { } callback) { AfterRead = null; await callback(); }
            return Pending is { } pending ? await pending : new("browser", "available", "isolated", DateTimeOffset.UtcNow, LiveContexts: 0, OpenPages: CountPages ? Calls : 0);
        }
    }
    private sealed class RuntimeStateFactory(bool sqlite, TimeProvider? clock = null, ILanguageModel? model = null, string? root = null, IModelCatalog? catalog = null, AgentCore.Application.Triggers.ITriggerCommandAuthorizer? semantic = null, Action<IServiceCollection>? configure = null, bool nativeDefinitions = false) : AgentCoreApiFactory
    {
        protected override bool EnableTenantRunExecution => model is not null;
        public RuntimeBrowserProbe Browser { get; } = new(); public string Root { get; } = root ?? Path.Combine(Path.GetTempPath(), "tenant-runtime-state-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                if (sqlite) { Directory.CreateDirectory(Root); var connection = $"Data Source={Path.Combine(Root, "state.db")};Foreign Keys=True;Default Timeout=10";
                    services.AddDbContextFactory<AgentCoreDbContext>(options => options.UseSqlite(connection)); TestSqliteIdentity.Configure(services, connection, Path.Combine(Root, "keys")); }
                if (model is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton<ILanguageModel>(model));
                    if (!nativeDefinitions)
                    {
                        var descriptor = services.Single(d => d.ServiceType == typeof(IBuiltInAgentDefinitionStore));
                        services.Replace(ServiceDescriptor.Singleton<IBuiltInAgentDefinitionStore>(provider => new RuntimeDefinitions((IBuiltInAgentDefinitionStore)
                            (descriptor.ImplementationInstance ?? descriptor.ImplementationFactory?.Invoke(provider) ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!)))));
                    }
                }
                services.Replace(ServiceDescriptor.Singleton<ITenantBrowserStateReader>(Browser));
                if (semantic is not null) services.Replace(ServiceDescriptor.Singleton(semantic));
                if (catalog is not null) services.Replace(ServiceDescriptor.Singleton<IModelCatalog>(catalog));
                if (clock is not null) services.Replace(ServiceDescriptor.Singleton(clock));
                configure?.Invoke(services);
            });
        }
    }
}
