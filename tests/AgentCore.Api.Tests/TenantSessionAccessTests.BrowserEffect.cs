using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class PausedBrowserDispatch(IBrowser inner, bool pause) : IBrowser
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public BrowserProviderDescriptor Provider => inner.Provider;
        public bool IsAvailable => inner.IsAvailable;
        public BrowserHostPolicy HostPolicy => inner.HostPolicy;
        public async ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default)
        {
            if (pause) { Entered.TrySetResult(); await Proceed.Task.WaitAsync(ct); }
            return await inner.ExecuteAsync(request, ct);
        }
    }
    [TenantBrowserChromiumTheory] [InlineData(false, false, false)] [InlineData(true, false, false)] [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)]
    public async Task Tenant_Browser_effect_HTTP_consent_drives_one_cold_native_navigation_or_current_gate_denial(bool sqlite, bool revoke, bool budget)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-effect-http-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> baseFactory = sqlite ? new TenantApprovalSqliteFactory(root, new ApprovalRecordingModel()) : new AgentCoreApiFactory();
            await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
                var options = (BrowserOptions)services.Single(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = true; options.FixturePort = 0;
                options.ProfileRoot = Path.Combine(root, "browser"); options.PolicyMode = "Restricted"; options.ProfileMode = "EphemeralSession"; options.NavigationOrigins = ["http://127.0.0.1:5091"];
                options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
            }));
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var instanceRevision = 1L;
            if (budget)
            {
                var changed = await Json(await owner.PutAsJsonAsync(api + $"/agents/{instance}/execution-budgets", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                    budgets = new ExecutionBudgetPolicy(new(24, 180, 1), new(48, 300, 1), new(32, 240, 1)) }));
                instanceRevision = changed.GetProperty("instance").GetProperty("revision").GetInt64();
            }
            var session = await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision }));
            var sessionId = session.GetProperty("sessionId").GetGuid(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Execute reviewed Browser", ["browser.read", "browser.use"], await Revision(owner, tenant));
            var use = await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
            role = await access.SaveRoleAsync(human, tenant, null, "Read effect Session", ["sessions.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, TenantSessionRules.Scope(tenant, sessionId), await Revision(owner, tenant));
            var submitted = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{sessionId}/messages", new { messageId = Guid.NewGuid(), text = "Review Browser navigation", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var runtime = factory.Services.GetRequiredService<ITenantRuntimeStore>();
            var run = (await runs.FindTenantRunAsync(tenant, submitted.GetProperty("runId").GetGuid()))!; var at = DateTimeOffset.UtcNow;
            run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, at, Guid.NewGuid(), at.AddMinutes(5)), Guid.NewGuid());
            var native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>()); Assert.True(native.IsAvailable);
            var scope = new TenantBrowserOwner(tenant, instance, executor, sessionId); Assert.Null(await native.BindTenantIsolationAsync(scope, BrowserProfileMode.EphemeralSession));
            var url = native.Fixture.Origin + "/browser-native.html"; Assert.Equal(native.Fixture.Origin, native.HostPolicy.NavigationOrigins.Single());
            var approvals = factory.Services.GetRequiredService<TenantApprovalService>();
            var request = (await approvals.PrepareBrowserAsync(new(tenant, run.AgentRunId), ToolCatalog.BrowserNavigate,
                System.Text.Json.JsonSerializer.Serialize(new { url }), run.Revision, run.Claim!.Generation)).Request!;
            Assert.Equal(0, (await native.ObserveTenantSessionAsync(scope)).LiveContexts); // Review never provisions.
            var route = api + $"/approvals/{request.RequestId}"; var view = await Json(await owner.GetAsync(route));
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(route + "/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                expectedRevision = view.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })).StatusCode);
            await approvals.DispatchPendingAsync(); run = (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!; at = DateTimeOffset.UtcNow;
            run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, at, Guid.NewGuid(), at.AddMinutes(5)), Guid.NewGuid());
            if (budget) Assert.Equal(1, run.Admission.ExecutionBudget!.Profile.PerToolSeconds);
            var paused = new PausedBrowserDispatch(native, revoke || budget); var store = factory.Services.GetRequiredService<IIamStore>();
            var service = new TenantBrowserEffectService(store, runs, factory.Services.GetRequiredService<ITenantApprovalStore>(),
                factory.Services.GetRequiredService<ITenantApprovedOperationStore>(), factory.Services.GetRequiredService<TenantBrowserOperationTarget>(),
                factory.Services.GetRequiredService<IamAuthorizer>(), paused, factory.Services.GetRequiredService<TenantOperationAuthorizer>(), factory.Services.GetRequiredService<IIdGenerator>(), TimeProvider.System);
            var pending = service.ExecuteAsync(new(tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim!.Generation, request.Operation.ActionHash).AsTask();
            if (revoke)
            {
                await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(AgentRunSideEffectDisposition.InFlight, (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.SideEffect.Disposition);
                await access.RevokeAssignmentAsync(human, tenant, use.AssignmentId, await Revision(owner, tenant)); paused.Proceed.TrySetResult();
            }
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            if (budget) Assert.Contains(result.Receipt.Outcome, new[] { TenantOperationOutcome.Indeterminate, TenantOperationOutcome.Denied });
            else
            {
                Assert.Equal(revoke ? TenantOperationOutcome.Denied : TenantOperationOutcome.Completed, result.Receipt.Outcome);
                Assert.Equal(revoke ? "permission_denied" : TenantBrowserEffectRules.Completed, result.Receipt.ReasonCode);
            }
            Assert.Equal(revoke || budget ? 0 : 1, (await native.ObserveTenantSessionAsync(scope)).OpenPages);
            var currentTarget = (await native.PrepareTenantCommandAsync(scope, new BrowserGetConfig())).Witness!;
            Assert.Equal(revoke || budget ? 0 : 1, currentTarget.CommandRevision);
            var duplicate = await service.ExecuteAsync(new(tenant, run.AgentRunId), request.RequestId, request.Operation.OperationId, run.Claim.Generation, request.Operation.ActionHash);
            Assert.Equal(result.Receipt, duplicate.Receipt); Assert.Null(duplicate.Result);
            Assert.Equal(currentTarget.CommandRevision, (await native.PrepareTenantCommandAsync(scope, new BrowserGetConfig())).Witness!.CommandRevision);
            var recorded = await Json(await owner.GetAsync(route)); Assert.Equal(result.Receipt.Outcome.ToString().ToLowerInvariant(), recorded.GetProperty("operationResult").GetProperty("outcome").GetString());
            Assert.Null(await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(tenant, request.RequestId));
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
