using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Browser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [TenantBrowserChromiumTheory]
    [InlineData(false, "scheduled", "approve")] [InlineData(true, "scheduled", "approve")]
    [InlineData(false, "manual", "approve")] [InlineData(true, "manual", "read-review")]
    [InlineData(false, "scheduled", "reject")] [InlineData(true, "scheduled", "read-review")]
    public async Task Independent_Automation_Browser_uses_exact_owned_consent_and_actual_native_observation(bool sqlite, string origin, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-independent-browser-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using var page = new LoopbackBrowserFixtureHost(NullLogger.Instance); await page.StartAsync(0, CancellationToken.None); Assert.True(page.IsAvailable);
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var model = new BackgroundNativeBrowserModel { Independent = true, ReadReview = mode == "read-review", Url = page.Origin + "/browser-native.html" };
            void Configure(IServiceCollection services)
            {
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock);
                var options = (BrowserOptions)services.Last(d => d.ServiceType == typeof(BrowserOptions)).ImplementationInstance!;
                options.Enabled = true; options.Headless = true; options.Channel = null; options.FixtureEnabled = false;
                options.ProfileRoot = Path.Combine(root, "browser"); options.PolicyMode = "Restricted"; options.ProfileMode = "EphemeralSession";
                options.NavigationOrigins = [page.Origin!]; options.TargetOrigins = null; options.InteractionOrigins = null; options.ResourceOrigins = [];
            }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            var setup = await StartTenantBackground(factory, owner, submit: false, fileCapabilities: [ToolCatalog.BrowserNavigate, ToolCatalog.BrowserSnapshot], reportReads: true, independentTask: true);
            var tenant = setup.Tenant; var api = $"/api/v2/tenants/{tenant}"; var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
            var parent = (await sessions.FindTenantSessionAsync(tenant, setup.Session))!; var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>(); var role = await access.SaveRoleAsync(human, tenant, null, "Independent native Browser", ["automations.run", "browser.read", "browser.use"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, parent.Owner.ExecutorPrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (mode == "read-review")
            {
                var policy = await owner.PutAsJsonAsync(api + "/operation-policies/browser.read", new { mode = "always", independentReview = origin == "scheduled", expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 });
                Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
            }
            var initialId = await TenantCredentialAccessTests.AdmitIndependentTask(factory, owner, tenant, parent.Owner.AgentInstanceId, "Observe the exact native Browser target under current authority.", clock, origin);
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
            var initial = (await runs.FindTenantRunAsync(tenant, initialId))!; Assert.NotEqual(setup.Session, initial.SessionId); Assert.Null(initial.Admission.ExecutionContext!.ParentRunId);
            var native = Assert.IsType<NativePlaywrightBrowser>(factory.Services.GetRequiredService<IBrowser>());
            var scope = new TenantBrowserOwner(tenant, parent.Owner.AgentInstanceId, parent.Owner.ExecutorPrincipalId, initial.SessionId);
            var seen = new HashSet<Guid>(); AgentRun final; using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync(deadline.Token);
                await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
                final = (await CurrentApprovalExecution(runs, tenant, initialId))!;
                if (final.Status == AgentRunStatus.WaitingForApproval && seen.Add(final.ApprovalRequestId!.Value))
                {
                    if (seen.Count == 1) Assert.Equal(0, (await native.ObserveTenantSessionAsync(scope)).LiveContexts);
                    var request = (await approvals.FindApprovalAsync(tenant, final.ApprovalRequestId.Value))!;
                    Assert.Equal(origin == "scheduled" ? (Guid?)null : human, request.OriginalInitiatorPrincipalId); Assert.Equal(initial.ActivationId, request.RootActivationId);
                    var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                    var response = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash,
                        expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision });
                    Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
                }
                if (final.IsTerminal && final.Result?.OutcomeKind != AgentRunOutcomeKind.Continued) break;
                await Task.Delay(25, deadline.Token);
            }
            Assert.Equal(AgentRunStatus.Completed, final.Status); Assert.Equal(initial.ActivationId, final.Admission.ExecutionContext!.RootActivationId);
            Assert.Equal(origin == "scheduled" ? (Guid?)null : human, final.Admission.ExecutionContext.OriginalInitiatorPrincipalId); Assert.Null(final.Owner.ProfileIdOrNull);
            Assert.True(AgentRunToolCallCheckpoint.TryRead(final.Checkpoint, out var messages));
            using var navigation = JsonDocument.Parse(Assert.Single(messages!, m => m.ToolCallId == "child-native-navigate").Text);
            Assert.Equal(mode != "reject", navigation.RootElement.GetProperty("commandCompleted").GetBoolean()); Assert.False(navigation.RootElement.GetProperty("replayAllowed").GetBoolean());
            if (mode != "reject") Assert.Contains("untrustedBrowserContent", Assert.Single(messages!, m => m.ToolCallId == "child-native-observe").Text);
            else Assert.Equal(0, (await native.ObserveTenantSessionAsync(scope)).LiveContexts);
            Assert.Equal(mode == "read-review" ? 2 : 1, seen.Count);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
