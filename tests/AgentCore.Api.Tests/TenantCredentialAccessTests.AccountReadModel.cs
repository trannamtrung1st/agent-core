using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Api.Http;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private sealed class AccountReadDiagnostics : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Failures { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Dispose() { }
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        { if (error is not null) Failures.Enqueue(error.ToString()); }
    }
    private sealed class AccountReadProbe(ITenantConnectedAccountReader inner) : ITenantConnectedAccountReader
    {
        public int Calls;
        public ConnectedAccountRead? LastRead;
        public ConnectedAccountReadResult? LastResult;
        public Func<Task>? Before, After;
        public ConnectedAccountAvailability Availability(Credential c) => inner.Availability(c);
        public async ValueTask<ConnectedAccountReadResult> ReadAsync(TenantConnectedAccount c, ConnectedAccountRead read,
            Func<CancellationToken, ValueTask<Credential>> current, Func<CredentialOAuthCheck, CancellationToken, ValueTask> maintain, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls); if (Interlocked.Exchange(ref Before, null) is { } before) await before();
            var result = await inner.ReadAsync(c, read, current, maintain, ct);
            LastRead = read; LastResult = result;
            if (Interlocked.Exchange(ref After, null) is { } after) await after(); return result;
        }
    }

    [Theory] [InlineData(false, "EmailAccount", "approve")] [InlineData(true, "EmailAccount", "approve")]
    [InlineData(false, "GoogleDriveAccount", "approve")] [InlineData(true, "GoogleDriveAccount", "approve")]
    [InlineData(false, "EmailAccount", "exception")] [InlineData(true, "EmailAccount", "exception")]
    [InlineData(false, "GoogleDriveAccount", "exception")] [InlineData(true, "GoogleDriveAccount", "exception")]
    [InlineData(false, "EmailAccount", "reject")] [InlineData(true, "EmailAccount", "reject")]
    [InlineData(false, "GoogleDriveAccount", "cancel")] [InlineData(true, "GoogleDriveAccount", "cancel")]
    [InlineData(false, "EmailAccount", "family-revoked")] [InlineData(true, "EmailAccount", "family-revoked")]
    [InlineData(false, "GoogleDriveAccount", "family-revoked")] [InlineData(true, "GoogleDriveAccount", "family-revoked")]
    [InlineData(false, "EmailAccount", "binding-changed")] [InlineData(true, "EmailAccount", "binding-changed")]
    [InlineData(false, "GoogleDriveAccount", "metadata-revoked")] [InlineData(true, "GoogleDriveAccount", "metadata-revoked")]
    [InlineData(false, "EmailAccount", "message")] [InlineData(true, "EmailAccount", "message")]
    [InlineData(false, "GoogleDriveAccount", "file")] [InlineData(true, "GoogleDriveAccount", "file")]
    [InlineData(false, "GoogleDriveAccount", "search")] [InlineData(true, "GoogleDriveAccount", "search")]
    [InlineData(false, "EmailAccount", "after-family-revoked")] [InlineData(true, "EmailAccount", "after-family-revoked")]
    [InlineData(false, "EmailAccount", "final-family-revoked")] [InlineData(true, "EmailAccount", "final-family-revoked")]
    [InlineData(false, "EmailAccount", "visibility")] [InlineData(true, "EmailAccount", "visibility")]
    [InlineData(false, "EmailAccount", "recent-omitted")] [InlineData(true, "EmailAccount", "recent-omitted")]
    [InlineData(false, "EmailAccount", "recent-empty")] [InlineData(true, "EmailAccount", "recent-empty")]
    [InlineData(false, "EmailAccount", "recent-blank")] [InlineData(true, "EmailAccount", "recent-blank")]
    public async Task Model_connected_account_read_waits_for_exact_consent_and_returns_only_current_owned_data(bool sqlite, string purpose, string choice)
    {
        var model = new CredentialModel(); var diagnostics = new AccountReadDiagnostics(); AccountReadProbe? probe = null;
        await using var source = new OAuthFactory(sqlite: sqlite, execute: true, model: model);
        await using var factory = source.WithWebHostBuilder(b => {
            b.ConfigureLogging(l => l.AddProvider(diagnostics));
            b.ConfigureTestServices(services => services.Replace(ServiceDescriptor.Singleton<ITenantConnectedAccountReader>(p => probe = new(p.GetRequiredService<TenantConnectedAccountReader>()))));
        });
        using var owner = ConnectedClient(factory); var s = await ConnectedReadAccount(factory, owner, purpose, tools: true);
        _ = factory.Services.GetRequiredService<ITenantConnectedAccountReader>();
        var actor = (await Json(await owner.GetAsync($"/api/v2/tenants/{s.Tenant}/access/me"))).GetProperty("principalId").GetGuid();
        var api = $"/api/v2/tenants/{s.Tenant}"; var access = factory.Services.GetRequiredService<TenantAccessService>();
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(s.Tenant, s.Instance))!;
        async Task<RoleAssignment> Grant(string name, string[] permissions)
        {
            var role = await access.SaveRoleAsync(actor, s.Tenant, null, name, permissions, await Revision(owner, s.Tenant));
            return await access.AssignRoleAsync(actor, s.Tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), await Revision(owner, s.Tenant));
        }
        var metadata = await Grant("Read exact metadata", ["credentials.read"]);
        var family = await Grant("Read exact family", [purpose == "EmailAccount" ? "email.read" : "drive.read"]);
        await Grant("Read current Session", ["sessions.read"]);
        if (choice != "exception") await Grant("Use exact account", ["credentials.use"]);
        if (purpose == "GoogleDriveAccount")
            await factory.Services.GetRequiredService<ISyntheticDriveStore>().SaveAsync(s.Credential,
                new("exact-file", "Exact consented report.txt", "text/plain", 3, "1", null, ["root"], false), [1, 2, 3], null, default);
        var tool = choice switch { "message" => ToolCatalog.EmailRead, "file" => ToolCatalog.DriveGet, "search" => ToolCatalog.DriveSearch,
            _ => purpose == "EmailAccount" ? ToolCatalog.EmailSearch : ToolCatalog.DriveList };
        object arguments = choice switch { "message" => new { accountRef = s.Alias, messageId = "syn-" + s.Credential.ToString("N") + ":syn-msg-harness" },
            "recent-omitted" => new { accountRef = s.Alias, limit = 10 },
            "recent-empty" or "recent-blank" => new { accountRef = s.Alias, query = choice == "recent-empty" ? "" : "   ", limit = 10 },
            "file" => new { accountRef = s.Alias, fileId = "exact-file" }, _ => new { accountRef = s.Alias, query = purpose == "EmailAccount" ? "harness" : "Exact", limit = 1 } };
        var command = ScriptedLanguageModel.AccountReadMarker + " " + JsonSerializer.Serialize(new { tool, arguments });
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = s.Instance, expectedRevision = await Revision(owner, s.Tenant), expectedInstanceRevision = 2 }))).GetProperty("sessionId").GetGuid();
        var initial = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = command,
            expectedRevision = await Revision(owner, s.Tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var waiting = await AwaitCredentialRun(runs, s.Tenant, initial, true);
        Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}: {waiting.Failure?.Summary}");
        Assert.Null(waiting.Claim); Assert.Equal(0, probe!.Calls);
        var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(s.Tenant, waiting.ApprovalRequestId!.Value))!;
        Assert.Equal(TenantOperationKind.AccountRead, request.Operation.Kind); Assert.Equal(choice == "exception" ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
        Assert.Equal(tool, request.Operation.AccountRead!.ToolName); Assert.Equal(s.Credential, request.Operation.AccountRead.Credential.CredentialId);
        Assert.Equal("credentials.use", request.Permission); Assert.DoesNotContain("ProtectedPayload", request.Operation.CanonicalActionJson);
        Assert.Contains(TenantAccountReadContract.Operation, Assert.Single(model.Requests.First().Tools!, t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson);
        var route = api + $"/approvals/{request.RequestId}";
        if (choice == "after-family-revoked") probe.After = async () => await access.RevokeAssignmentAsync(actor, s.Tenant, family.AssignmentId, await Revision(owner, s.Tenant));
        if (choice == "final-family-revoked") model.AfterAccountRead = async () => await access.RevokeAssignmentAsync(actor, s.Tenant, family.AssignmentId, await Revision(owner, s.Tenant));
        if (choice == "visibility")
        {
            var account = await factory.Services.GetRequiredService<IdentityService>().RegisterAsync("account-visibility@example.test", "Account reviewer", "strong-account-visibility-password");
            var readerRole = await access.SaveRoleAsync(actor, s.Tenant, null, "Read protected consent", ["approvals.read", "credentials.read", "credentials.use", "sessions.read"], await Revision(owner, s.Tenant));
            var invite = await access.InviteAsync(actor, s.Tenant, account.LoginKey, readerRole.RoleId, await Revision(owner, s.Tenant));
            var member = await access.AcceptInvitationAsync(account.AccountId, s.Tenant, invite.Invitation.InvitationId, invite.Token);
            using var reader = ConnectedClient(factory); await Login(reader, account.LoginKey, "strong-account-visibility-password");
            Assert.Equal(0, (await Json(await reader.GetAsync(api + "/approvals?pageSize=1"))).GetProperty("total").GetInt32());
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(route)).StatusCode);
            var exact = await access.SaveRoleAsync(actor, s.Tenant, null, "Read protected mailbox", ["email.read"], await Revision(owner, s.Tenant));
            var assignment = await access.AssignRoleAsync(actor, s.Tenant, member.PrincipalId, exact.RoleId,
                AccessScope.ForResource(s.Tenant, "credential", s.Credential.ToString("D")), await Revision(owner, s.Tenant));
            Assert.Equal(1, (await Json(await reader.GetAsync(api + "/approvals?pageSize=1"))).GetProperty("total").GetInt32());
            Assert.Empty((await Json(await reader.GetAsync(api + "/approvals?page=1&pageSize=1"))).GetProperty("items").EnumerateArray());
            Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(route)).StatusCode);
            await access.RevokeAssignmentAsync(actor, s.Tenant, assignment.AssignmentId, await Revision(owner, s.Tenant));
            var hidden = await Json(await reader.GetAsync(api + "/approvals?pageSize=1")); Assert.Equal(0, hidden.GetProperty("total").GetInt32());
            Assert.DoesNotContain("harness", hidden.GetRawText()); Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(route)).StatusCode);
        }
        if (choice is "family-revoked" or "metadata-revoked")
            await access.RevokeAssignmentAsync(actor, s.Tenant, choice == "family-revoked" ? family.AssignmentId : metadata.AssignmentId, await Revision(owner, s.Tenant));
        if (choice == "binding-changed")
            Assert.Equal(HttpStatusCode.OK, (await owner.DeleteAsync(api + $"/agents/{s.Instance}/credential-bindings/{s.Binding}?expectedRevision={await Revision(owner, s.Tenant)}&expectedInstanceRevision=2&expectedBindingRevision=1")).StatusCode);
        var detail = await Json(await owner.GetAsync(route)); Safe(detail);
        Assert.Equal("read_connected_account", detail.GetProperty("preparedAction").GetProperty("operation").GetString());
        Assert.Contains(detail.GetProperty("review").GetProperty("changes").EnumerateArray(), c => c.GetProperty("field").GetString() == "accountReadArguments");
        var stale = choice is "family-revoked" or "metadata-revoked" or "binding-changed";
        Assert.Equal(!stale, detail.GetProperty("review").GetProperty("canDecide").GetBoolean()); Assert.True(detail.GetProperty("review").GetProperty("canCancel").GetBoolean());
        using var decided = stale || choice is "cancel" or "visibility" ? await owner.PostAsJsonAsync(route + "/cancel", new { actionHash = request.Operation.ActionHash, expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision })
            : await owner.PostAsJsonAsync(route + "/decision", new { decision = choice == "reject" ? "reject" : "approve", actionHash = request.Operation.ActionHash,
                expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision });
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        AgentRun finished;
        try { finished = await AwaitCredentialRun(runs, s.Tenant, initial, false); }
        catch (OperationCanceledException) { throw new Xunit.Sdk.XunitException(string.Join("\n", diagnostics.Failures.TakeLast(8))); }
        if (choice == "final-family-revoked")
        {
            Assert.Equal(AgentRunStatus.Failed, finished.Status); Assert.Equal("protected-output-unavailable", finished.Failure!.Code); Assert.Null(finished.Result); Assert.Equal(1, probe.Calls);
            Assert.Contains(model.Requests.Last().Messages, m => m.Role == ModelRole.Tool && m.Text.Contains("tenantAccountRead", StringComparison.Ordinal));
            Assert.Contains(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(s.Tenant, 200), a => a.Execution?.Context.RunId == finished.AgentRunId && a.Decision == "denied" && a.ReasonCode == "account_read_required");
        }
        if (choice != "final-family-revoked") Assert.True(finished.Status == AgentRunStatus.Completed, $"{finished.Status}: {finished.Failure?.Code}: {finished.Failure?.Summary}");
        var recent = choice.StartsWith("recent-", StringComparison.Ordinal);
        var allowed = recent || choice is "approve" or "exception" or "message" or "file" or "search"; Assert.Equal(allowed || choice is "after-family-revoked" or "final-family-revoked" ? 1 : 0, probe.Calls);
        if (recent)
        {
            Assert.Equal(10, probe.LastRead!.Limit); Assert.True(string.IsNullOrWhiteSpace(probe.LastRead.Query));
            var messages = probe.LastResult!.Mailbox!.Results; Assert.NotEmpty(messages); Assert.InRange(messages.Count, 1, 10);
            Assert.Equal(messages.Select(m => m.Date).OrderByDescending(d => d), messages.Select(m => m.Date));
            using var schema = JsonDocument.Parse(TenantAccountReadContract.Schema([ToolCatalog.EmailSearch]).ToJsonString());
            var required = schema.RootElement.GetProperty("oneOf")[0].GetProperty("properties").GetProperty("read").GetProperty("properties").GetProperty("arguments").GetProperty("required");
            Assert.DoesNotContain(required.EnumerateArray(), p => p.GetString() == "query");
        }
        var result = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(s.Tenant, session))!;
        // Private runtime output has no browser delivery acknowledgement. Read retained history through a fresh trusted frame.
        Volatile.Write(ref source.TenantExecutionEnabled, false);
        var inspectId = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect recorded account outcome",
            expectedRevision = await Revision(owner, s.Tenant), expectedSessionRevision = result.Snapshot.Revision }))).GetProperty("runId").GetGuid();
        var inspect = (await runs.FindTenantRunAsync(s.Tenant, inspectId))!;
        var runtime = factory.Services.GetRequiredService<ITenantRuntimeStore>(); var clock = factory.Services.GetRequiredService<TimeProvider>(); var ids = factory.Services.GetRequiredService<IIdGenerator>();
        inspect = await runtime.ApplyTenantRuntimeCommandAsync(inspect.Owner, inspectId,
            new AgentRunCommand.Claim(inspect.Revision, clock.GetUtcNow(), ids.NewId(), clock.GetUtcNow().AddMinutes(5)), ids.NewId());
        var retained = (await runtime.LoadTenantRuntimeAsync(new(inspect.Owner, session, inspectId, inspect.Claim!.Generation)))!;
        var answer = choice == "final-family-revoked" ? "" : Assert.Single(retained.Entries, e => e.Role == ConversationRole.Assistant && e.ResponseId == finished.ResponseId).Text;
        inspect = await runtime.ApplyTenantRuntimeCommandAsync(inspect.Owner, inspectId,
            new AgentRunCommand.RequestCancellation(inspect.Revision, clock.GetUtcNow(), null), ids.NewId());
        await runtime.ApplyTenantRuntimeCommandAsync(inspect.Owner, inspectId,
            new AgentRunCommand.CommitCancellation(inspect.Revision, clock.GetUtcNow(), inspect.Claim!.Generation, null), ids.NewId());
        if (choice == "final-family-revoked")
        {
            Assert.DoesNotContain(retained.Entries, e => e.ResponseId == finished.ResponseId && e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Completed);
            return;
        }
        if (allowed)
        {
            Assert.Contains(purpose == "EmailAccount" ? "Synthetic harness inbox" : "Exact consented report.txt", answer);
            var receipt = (await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId))!;
            Assert.Equal(TenantAccountReadRules.Reason, receipt.ReasonCode); Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome);
            Assert.Contains(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(s.Tenant, 200), a => a.Action == "operations.accounts.read" && a.Execution?.Context.RunId == finished.AgentRunId);
            var grant = await factory.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(s.Tenant, request.RequestId);
            if (choice == "exception") { Assert.NotNull(grant); Assert.Equal(OperationGrantStatus.Consumed, grant.Status); }
            else Assert.Null(grant);
            Assert.Contains(model.Requests.Last().Messages, m => m.Role == ModelRole.Tool && m.Text.Contains("tenantAccountRead", StringComparison.Ordinal));
        }
        else { Assert.Contains("not delivered", answer); Assert.DoesNotContain("Synthetic harness inbox", answer); Assert.DoesNotContain("Exact consented report.txt", answer); }
        Assert.Equal(0, source.Outbound); Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }
}
