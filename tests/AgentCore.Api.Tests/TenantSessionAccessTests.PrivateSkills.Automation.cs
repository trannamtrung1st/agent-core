using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;
public sealed partial class TenantSessionAccessTests
{
    public static IEnumerable<object[]> IndependentPrivateSkillCases => from sqlite in new[] { false, true }
        from origin in new[] { "scheduled", "manual" }
        from mode in new[] { "direct", "approve", "reject" }
        select new object[] { sqlite, origin, mode };
    [Theory(Timeout = 120000)] [MemberData(nameof(IndependentPrivateSkillCases))]
    public async Task Independent_Automation_loads_private_Skills_with_actual_origin_exact_read_Review_and_no_Chat_or_management_authority(bool sqlite, string origin, string mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "independent-private-skill-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var model = new PrivateSkillModel(false, true); var logs = new ManagementMutationLogs();
        void Configure(IServiceCollection services) { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); services.AddSingleton<ILoggerProvider>(logs); }
        try
        {
            await using WebApplicationFactory<Program> host = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var owner = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") });
            var setup = await StartTenantBackground(host, owner, submit: false, fileCapabilities: [], independentTask: true);
            var tenant = setup.Tenant; var api = $"/api/v2/tenants/{tenant}";
            var parent = (await host.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, setup.Session))!;
            var instance = parent.Owner.AgentInstanceId; var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agents/{instance}/skills/commands", new { operation = "create", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1,
                input = new { id = "private-procedure", name = "Private procedure", description = "Private description sentinel", procedure = PrivateSkillProcedure,
                    projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "chat.respond" } } })).StatusCode);
            var access = host.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Independent private procedure executor", ["automations.run", "agents.read", "skills.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, parent.Owner.ExecutorPrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            if (mode != "direct") Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/skills.read", new { mode = "always", independentReview = false,
                expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            while (logs.Errors.TryDequeue(out _)) { }
            var id = await TenantCredentialAccessTests.AdmitIndependentTask(host, owner, tenant, instance, "Use the exact instance:private-procedure Skill for this admitted task.", clock, origin);
            var runs = host.Services.GetRequiredService<ITenantRunStore>();
            string Diagnostic(AgentRun value) => $"{value.Status}: {value.Failure?.Code}: {value.Failure?.Summary}; modelCalls={model.Requests.Count}; tools={string.Join(" | ", model.Requests.SelectMany(r => r.Messages).Where(m => m.Role == ModelRole.Tool).Select(m => m.Name + ":" + m.Text))}; logs={string.Join(" | ", logs.Errors)}";
            async Task<AgentRun> Pump(bool successor = false)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                while (true)
                {
                    await host.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync(deadline.Token);
                    await host.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
                    var current = (await CurrentApprovalExecution(runs, tenant, id))!;
                    if ((!successor || current.AgentRunId != id) && (current.IsTerminal || !successor && current.Status == AgentRunStatus.WaitingForApproval)) return current;
                    if (current.Status == AgentRunStatus.WaitingToRetry && current.NextRetryAtUtc is { } retry && retry > clock.GetUtcNow()) clock.Advance(retry - clock.GetUtcNow());
                    try { await Task.Delay(20, deadline.Token); }
                    catch (TaskCanceledException) { Assert.Fail(Diagnostic(current)); throw; }
                }
            }
            var run = await Pump();
            var initiator = origin == "scheduled" ? (Guid?)null : human;
            Assert.Equal(initiator, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId);
            Assert.Equal(origin == "scheduled" ? ExecutionOrigin.ScheduledOccurrence : ExecutionOrigin.ManualInvocation, run.Admission.ExecutionContext.Origin);
            if (mode != "direct")
            {
                Assert.True(run.Status == AgentRunStatus.WaitingForApproval, Diagnostic(run)); Assert.Equal(0, run.SkillLoadCount);
                var approvals = host.Services.GetRequiredService<ITenantApprovalStore>(); var request = (await approvals.FindApprovalAsync(tenant, run.ApprovalRequestId!.Value))!;
                Assert.Equal(initiator, request.OriginalInitiatorPrincipalId); Assert.Equal(ToolCatalog.SkillsLoad, request.Operation.ManagementRead!.ToolName);
                Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(api + $"/approvals/{request.RequestId}")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = mode, actionHash = request.Operation.ActionHash,
                    expectedRevision = await Revision(owner, tenant), expectedRequestRevision = request.Revision })).StatusCode);
                run = await Pump(successor: true);
                Assert.Null(await approvals.FindOperationGrantAsync(tenant, request.RequestId));
            }
            Assert.True(run.Status == AgentRunStatus.Completed, Diagnostic(run));
            Assert.Equal(mode != "reject", run.ActiveSkillKeys.Contains(PrivateSkillKey)); Assert.Equal(mode == "reject" ? 0 : 1, run.SkillLoadCount);
            Assert.Equal(initiator, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId); Assert.NotEqual(setup.Session, run.SessionId);
            Assert.Equal(2, run.Admission.Configuration!.InstanceRevision);
            Assert.DoesNotContain(model.Requests.SelectMany(r => r.Tools ?? []), t => t.Name.StartsWith("management.") || t.Name.StartsWith("harness.") || t.Name == ToolCatalog.BackgroundStart);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
