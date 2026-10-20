using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    internal static async Task<Guid> AdmitIndependentTask(WebApplicationFactory<Program> factory, HttpClient owner, Guid tenant, Guid instance, string instruction, FakeTimeProvider clock, string origin, HttpClient? invoker = null)
    {
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(tenant, instance))!;
        var route = $"/api/v2/tenants/{tenant}/agents/{instance}/automations";
        var created = await owner.PostAsJsonAsync(route, new { enabled = true, name = "Independent protected task", instructions = instruction,
            schedule = new { kind = "oneShot", timeZone = "UTC", atUtc = clock.GetUtcNow().AddMinutes(30).ToString("O") }, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision, requiresTools = true });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await Json(created)).GetProperty("automationId").GetGuid();
        if (origin == "manual")
        {
            var response = await (invoker ?? owner).PostAsJsonAsync(route + $"/{id}/run", new { expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = agent.Instance.Revision, expectedAutomationRevision = 1 });
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync()); return (await Json(response)).GetProperty("runId").GetGuid();
        }
        clock.Advance(TimeSpan.FromMinutes(31)); await factory.Services.GetRequiredService<TenantAutomationService>().AdmitDueAsync();
        return Assert.Single((await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantSessionRunsAsync(tenant, null, null, 0, 100, instanceId: instance)).Items).AgentRunId;
    }
    [Theory(Timeout = 120000)]
    [InlineData(false, "manual", false)] [InlineData(true, "manual", false)] [InlineData(false, "scheduled", false)] [InlineData(true, "scheduled", false)]
    [InlineData(false, "manual", true)] [InlineData(true, "manual", true)] [InlineData(false, "scheduled", true)] [InlineData(true, "scheduled", true)]
    public Task Independent_Automation_accounts_preserve_exact_read_draft_send_and_Drive_consent_without_a_fake_requester(bool sqlite, string origin, bool drive)
        => AutonomousAccountFlow(sqlite, origin, drive);

    [Theory(Timeout = 120000)] [InlineData(false)] [InlineData(true)]
    public Task Independent_scheduled_account_consent_reopens_in_a_fresh_SQLite_host_with_exact_native_sources(bool drive)
        => AutonomousAccountFlow(true, "scheduled", drive, restart: true);

    private async Task AutonomousAccountFlow(bool sqlite, string origin, bool drive, bool restart = false)
    {
        var model = new CredentialModel(); var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var diagnostics = new AccountReadDiagnostics();
        var root = restart ? Path.Combine(Path.GetTempPath(), "independent-account-reopen-" + Guid.NewGuid().ToString("N")) : null;
        void Configure(IServiceCollection services) { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(clock); services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(diagnostics); }
        await using var first = new OAuthFactory(root, sqlite: sqlite, preserve: restart, execute: true, model: model, servicesOverride: Configure);
        var factory = first; OAuthFactory? recreated = null; HttpClient? reopenedOwner = null;
        using var firstOwner = ConnectedClient(factory); var owner = firstOwner;
        try
        {
        var setup = await ConnectedReadAccount(factory, owner, drive ? "GoogleDriveAccount" : "EmailAccount", tools: true);
        factory.TenantExecutionEnabled = false;
        if (drive) await factory.Services.GetRequiredService<ISyntheticDriveStore>().SaveAsync(setup.Credential,
            new("file", "Original.bin", "application/octet-stream", 3, "1", null, ["root"], false), new byte[] { 0, 255, 42 }, null, default);
        var api = $"/api/v2/tenants/{setup.Tenant}"; var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(setup.Tenant, setup.Instance))!;
        var permissions = new List<string> { "automations.run", "sessions.create", "agent-runs.create", "sessions.read", "credentials.read", "credentials.use" };
        permissions.AddRange(drive ? ["drive.read", "drive.write"] : new[] { "email.draft", "email.send" });
        var access = factory.Services.GetRequiredService<TenantAccessService>(); var role = await access.SaveRoleAsync(human, setup.Tenant, null, "Independent exact account executor", permissions, await Revision(owner, setup.Tenant));
        await access.AssignRoleAsync(human, setup.Tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision(owner, setup.Tenant));
        var steps = drive ? new object[] {
            new { tool = ToolCatalog.DriveGet, arguments = (object)new { accountRef = setup.Alias, fileId = "file" } },
            new { tool = ToolCatalog.DriveRename, arguments = (object)new { accountRef = setup.Alias, authEpoch = 1, fileId = "file", expectedVersion = "1", name = "Independent renamed.bin" } }
        } : new object[] {
            new { tool = ToolCatalog.EmailCreateDraft, arguments = (object)new { accountRef = setup.Alias, to = new[] { "friend@example.test" }, subject = "Independent exact message", body = "Independent immutable message body." } },
            new { tool = ToolCatalog.EmailSend, arguments = (object)new { accountRef = setup.Alias, draftId = "$draft" } }
        };
        var initialId = await AdmitIndependentTask(factory, owner, setup.Tenant, setup.Instance, "synthetic account actions " + JsonSerializer.Serialize(new { steps }), clock, origin);
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
        factory.TenantExecutionEnabled = true;
        var initial = (await runs.FindTenantRunAsync(setup.Tenant, initialId))!; var reviewed = new List<TenantApprovalRequest>(); var seen = new HashSet<Guid>(); AgentRun final;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            await factory.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync(deadline.Token);
            await factory.Services.GetRequiredService<ITenantRunExecution>().ExecuteRunnableAsync(8, deadline.Token);
            var values = new List<AgentRun>(); foreach (var id in await runs.ListTenantRunIdsAsync(setup.Tenant, initial.SessionId, deadline.Token)) values.Add((await runs.FindTenantRunAsync(setup.Tenant, id, deadline.Token))!);
            final = values.SingleOrDefault(r => !r.IsTerminal) ?? values.Single(r => r.Result?.OutcomeKind != AgentRunOutcomeKind.Continued);
            if (final.Status == AgentRunStatus.WaitingForApproval && seen.Add(final.ApprovalRequestId!.Value))
            {
                var request = (await approvals.FindApprovalAsync(setup.Tenant, final.ApprovalRequestId.Value))!;
                if (restart && recreated is null && reviewed.Count == 1)
                {
                    await first.DisposeAsync(); model = new CredentialModel();
                    recreated = new OAuthFactory(root, sqlite: true, preserve: true, execute: true, model: model, servicesOverride: Configure); factory = recreated;
                    reopenedOwner = ConnectedClient(factory); owner = reopenedOwner; await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                    runs = factory.Services.GetRequiredService<ITenantRunStore>(); approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
                    Assert.Equal(JsonSerializer.Serialize(request), JsonSerializer.Serialize((await approvals.FindApprovalAsync(setup.Tenant, request.RequestId))!));
                    Assert.Equal(AgentRunStatus.WaitingForApproval, (await runs.FindTenantRunAsync(setup.Tenant, request.RunId))!.Status); seen.Remove(request.RequestId);
                    if (!drive) Assert.Null(await factory.Services.GetRequiredService<IEmailStore>().GetSendAsync(request.Operation.AccountAction!.Draft!.DraftId));
                    else Assert.Equal("Original.bin", (await factory.Services.GetRequiredService<ISyntheticDriveStore>().GetAsync(setup.Credential, "file", default))!.Name);
                    continue;
                }
                reviewed.Add(request);
                Assert.Equal(origin == "scheduled" ? (Guid?)null : human, request.OriginalInitiatorPrincipalId); Assert.Equal(initial.ActivationId, request.RootActivationId);
                Assert.Equal(initial.SessionId, request.SessionId); Assert.Equal(ApprovalKind.Review, request.Kind);
                var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                Assert.Equal(origin == "scheduled", detail.GetProperty("review").GetProperty("requesterName").ValueKind == JsonValueKind.Null);
                var decision = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                    expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision });
                Assert.True(decision.IsSuccessStatusCode, await decision.Content.ReadAsStringAsync());
            }
            if (final.IsTerminal && final.Result?.OutcomeKind != AgentRunOutcomeKind.Continued) break;
            await Task.Delay(25, deadline.Token);
        }
        Assert.True(final.Status == AgentRunStatus.Completed, $"{final.Status}: {final.Failure?.Code}; {string.Join("\n", diagnostics.Failures)}");
        Assert.True(reviewed.Count == 2, $"reviews={reviewed.Count}: {final.Result?.Text}; " + string.Join("; ", (await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(setup.Tenant, 200)).Where(a => a.ActorPrincipalId == agent.PrincipalId && a.Decision != "allowed").Select(a => a.ReasonCode))); Assert.Equal(initial.ActivationId, final.Admission.ExecutionContext!.RootActivationId);
        Assert.Equal(origin == "scheduled" ? (Guid?)null : human, final.Admission.ExecutionContext.OriginalInitiatorPrincipalId); Assert.Null(final.Owner.ProfileIdOrNull);
        if (!drive)
        {
            var drafts = reviewed.Select(r => r.Operation.AccountAction!.Draft!.DraftId).Distinct().ToArray(); var id = Assert.Single(drafts);
            var native = factory.Services.GetRequiredService<IEmailStore>(); Assert.Equal("Independent immutable message body.", (await native.GetDraftAsync(id))!.Draft.Body);
            Assert.Equal(EmailSendOutcome.Sent, (await native.GetSendAsync(id))!.Result!.Outcome);
        }
        else Assert.Equal("Independent renamed.bin", (await factory.Services.GetRequiredService<ISyntheticDriveStore>().GetAsync(setup.Credential, "file", default))!.Name);
        if (sqlite)
        {
            await using var db = factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>().CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
            if (origin == "scheduled")
            {
                await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261020180000_TenantAutonomousAutomation"));
                Assert.Contains("20261020190000_TenantIndependentAccountConsent", await db.Database.GetAppliedMigrationsAsync());
            }
            else
            {
                foreach (var request in reviewed)
                {
                    var rejected = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET OriginalInitiatorPrincipalId = NULL, PayloadJson = json_set(PayloadJson, '$.OriginalInitiatorPrincipalId', NULL) WHERE RequestId = {request.RequestId}"));
                    Assert.Contains("independent task consent requires its exact owned scheduled lineage", rejected.Message);
                }
            }
        }
        var effects = factory.Services.GetRequiredService<ITenantApprovedOperationStore>();
        foreach (var request in reviewed)
        {
            Assert.Equal(TenantOperationOutcome.Completed, (await effects.FindOperationReceiptAsync(setup.Tenant, request.Operation.OperationId))!.Outcome);
            Assert.Null(await approvals.FindOperationGrantAsync(setup.Tenant, request.RequestId));
        }
        Assert.Equal(0, factory.Outbound);
        }
        finally
        {
            reopenedOwner?.Dispose(); if (recreated is not null) await recreated.DisposeAsync();
            if (restart) { await first.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
