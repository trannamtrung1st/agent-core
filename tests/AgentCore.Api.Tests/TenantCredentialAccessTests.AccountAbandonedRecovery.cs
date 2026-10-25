using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Credentials;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Hosting;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private sealed class AbandonedAccountException : Exception { }
    private sealed class AccountCrashProbe(string window)
    {
        public Guid Request;
        public bool Armed;
        public int Writes;
        public string Window => window;
    }
    // The fault boundary is after the real durable reservation/native owner and
    // before its canonical settlement. No production recovery state is fabricated.
    private sealed class CrashAccountStore(ITenantAccountOperationStore inner, AccountCrashProbe probe) : ITenantAccountOperationStore
    {
        public async ValueTask<TenantAccountEffectStart?> TryStartAccountEffectAsync(TenantSecurityState next, long revision, TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
        {
            var started = await inner.TryStartAccountEffectAsync(next, revision, commit, audit, ct);
            if (probe.Armed && probe.Request == commit.RequestId && probe.Window == "reserved" && started?.Fence is not null) throw new AbandonedAccountException();
            return started;
        }
        public ValueTask<TenantOperationReceipt?> TrySettleAccountEffectAsync(TenantSecurityState next, long revision, TenantAccountDispatch fence,
            TenantOperationOutcome outcome, string reason, AuthorizationAudit audit, bool recovery = false, CancellationToken ct = default, TenantAccountActionResult? result = null)
            => probe.Armed && probe.Request == fence.RequestId ? throw new AbandonedAccountException()
                : inner.TrySettleAccountEffectAsync(next, revision, fence, outcome, reason, audit, recovery, ct, result);
        public ValueTask<IReadOnlyList<TenantAccountDispatch>> RecoverableAccountEffectsAsync(DateTimeOffset at, int limit, CancellationToken ct = default) => inner.RecoverableAccountEffectsAsync(at, limit, ct);
        public ValueTask<bool> TryMaintainAccountEffectAsync(TenantSecurityState next, long revision, TenantAccountDispatch fence, long credentialRevision,
            long secretRevision, CredentialOAuthCheck maintenance, AuthorizationAudit audit, CancellationToken ct = default) => inner.TryMaintainAccountEffectAsync(next, revision, fence, credentialRevision, secretRevision, maintenance, audit, ct);
    }
    private sealed class CrashAccountTransport(ITenantConnectedAccountTransportFactory inner, AccountCrashProbe probe) : ITenantConnectedAccountTransportFactory
    {
        public ConnectedAccountAvailability Availability(AgentCore.Domain.Credentials.Credential account, bool send = false) => inner.Availability(account, send);
        public async ValueTask<TenantConnectedAccountTransport> OpenAsync(TenantConnectedAccount account,
            Func<CancellationToken, ValueTask<AgentCore.Domain.Credentials.Credential>> recheck,
            Func<CredentialOAuthCheck, CancellationToken, ValueTask> maintenance, bool send = false, CancellationToken ct = default)
        {
            var native = await inner.OpenAsync(account, recheck, maintenance, send, ct);
            return native with { Email = native.Email is null ? null : new CrashEmail(native.Email, probe), Drive = native.Drive is null ? null : new CrashDrive(native.Drive, probe) };
        }
    }
    private sealed class CrashEmail(IEmailProvider inner, AccountCrashProbe probe) : IEmailProvider
    {
        public bool IsAvailable => inner.IsAvailable;
        public ValueTask<EmailSearchResult> SearchAsync(EmailSearchRequest r, CancellationToken ct = default) => inner.SearchAsync(r, ct);
        public ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest r, CancellationToken ct = default) => inner.ReadAsync(r, ct);
        public ValueTask<EmailDraftResult> CreateDraftAsync(EmailCreateDraftRequest r, CancellationToken ct = default) => inner.CreateDraftAsync(r, ct);
        public ValueTask<EmailDraftSnapshot?> GetDraftAsync(string id, CancellationToken ct = default) => inner.GetDraftAsync(id, ct);
        public async ValueTask<EmailSendResult> SendDraftAsync(EmailSendDraftRequest r, CancellationToken ct = default)
        {
            Interlocked.Increment(ref probe.Writes); var result = await inner.SendDraftAsync(r, ct);
            if (probe.Armed && probe.Window == "uncertain") throw new HttpRequestException("Synthetic send accepted; acknowledgement lost.");
            return result;
        }
    }
    private sealed class CrashDrive(IGoogleDriveProvider inner, AccountCrashProbe probe) : IGoogleDriveProvider
    {
        private async ValueTask Write(Func<ValueTask> effect)
        {
            Interlocked.Increment(ref probe.Writes); await effect();
            if (probe.Armed && probe.Window == "uncertain") throw new HttpRequestException("Synthetic Drive write accepted; acknowledgement lost.");
        }
        public ValueTask<string> GenerateIdAsync(CancellationToken ct) => inner.GenerateIdAsync(ct);
        public ValueTask<DriveFilePage> ListAsync(string? folder, string? query, string? cursor, int limit, CancellationToken ct) => inner.ListAsync(folder, query, cursor, limit, ct);
        public ValueTask<DriveFile> GetAsync(string id, CancellationToken ct) => inner.GetAsync(id, ct);
        public ValueTask CreateFolderAsync(string id, string name, string parent, CancellationToken ct) => Write(() => inner.CreateFolderAsync(id, name, parent, ct));
        public ValueTask UploadAsync(string id, string name, string parent, string mime, ReadOnlyMemory<byte> bytes, DriveOperation op, CancellationToken ct) => Write(() => inner.UploadAsync(id, name, parent, mime, bytes, op, ct));
        public ValueTask<byte[]> DownloadAsync(DriveFile file, CancellationToken ct) => inner.DownloadAsync(file, ct);
        public ValueTask<byte[]> DownloadAsync(DriveFile file, int max, CancellationToken ct) => inner.DownloadAsync(file, max, ct);
        public ValueTask RenameAsync(string id, string name, CancellationToken ct) => Write(() => inner.RenameAsync(id, name, ct));
        public ValueTask MoveAsync(string id, string source, string destination, CancellationToken ct) => Write(() => inner.MoveAsync(id, source, destination, ct));
        public ValueTask TrashAsync(string id, CancellationToken ct) => Write(() => inner.TrashAsync(id, ct));
    }
    private static void CrashAccountServices(IServiceCollection services, AccountCrashProbe probe)
    {
        services.RemoveAll<ITenantAccountOperationStore>();
        services.AddSingleton<ITenantAccountOperationStore>(p => new CrashAccountStore((ITenantAccountOperationStore)p.GetRequiredService<IIamStore>(), probe));
        services.RemoveAll<ITenantConnectedAccountTransportFactory>();
        services.AddSingleton<ITenantConnectedAccountTransportFactory>(p => new CrashAccountTransport(p.GetRequiredService<TenantConnectedAccountReader>(), probe));
    }
    private static Guid AccountBatchOperationId(Guid operation, int index, string id) => new(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes($"{operation:D}|{index}|{id}")).AsSpan(0, 16));
    public static TheoryData<string, string> AccountAbandonedHostCases
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var tool in new[] { ToolCatalog.EmailSend, ToolCatalog.DriveCreateFolder, ToolCatalog.DriveUpload, ToolCatalog.DriveTrashBatch })
            foreach (var window in new[] { "reserved", "acknowledged", "uncertain" }) cases.Add(tool, window);
            cases.Add(ToolCatalog.EmailSend, "revoked"); cases.Add(ToolCatalog.DriveUpload, "revoked");
            cases.Add(ToolCatalog.DriveCreateFolder, "exception"); cases.Add(ToolCatalog.DriveUpload, "exception-uncertain"); return cases;
        }
    }
    [Theory] [MemberData(nameof(AccountAbandonedHostCases))]
    public async Task Abandoned_account_dispatch_recovers_across_actual_SQLite_hosts_without_native_replay(string tool, string window)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-abandoned-account-host-" + Guid.NewGuid().ToString("N"));
        var faultWindow = window == "exception" ? "acknowledged" : window == "exception-uncertain" ? "uncertain" : window;
        var probe = new AccountCrashProbe(faultWindow); var email = tool == ToolCatalog.EmailSend;
        try
        {
            ConnectedReadSetup setup; Guid initial; Guid session; TenantApprovalRequest request; AgentRun abandoned;
            EmailSendOperation? send = null; var native = new List<DriveOperation>(); var bytes = new byte[] { 0, 255, 7, 42, 13, 10 };
            await using (var first = new OAuthFactory(root, sqlite: true, preserve: true, execute: true, model: new CredentialModel(), servicesOverride: services => CrashAccountServices(services, probe)))
            {
                using var owner = ConnectedClient(first); setup = await ConnectedReadAccount(first, owner, email ? "EmailAccount" : "GoogleDriveAccount", tools: true);
                var api = $"/api/v2/tenants/{setup.Tenant}"; var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
                var access = first.Services.GetRequiredService<TenantAccessService>(); var agent = (await first.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(setup.Tenant, setup.Instance))!;
                var permissions = new[] { "sessions.read", "agent-runs.read", "credentials.read", "credentials.use", "email.draft", "email.send", "drive.read", "drive.write", "drive.delete", "workspaces.read", "agents.read" }
                    .Where(key => !window.StartsWith("exception", StringComparison.Ordinal) || key != "drive.write").ToArray();
                var role = await access.SaveRoleAsync(human, setup.Tenant, null, "Exact recoverable account operations", permissions, await Revision(owner, setup.Tenant));
                var assignment = await access.AssignRoleAsync(human, setup.Tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision(owner, setup.Tenant));
                session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = setup.Instance, expectedRevision = await Revision(owner, setup.Tenant), expectedInstanceRevision = 2 }))).GetProperty("sessionId").GetGuid();
                var steps = new List<object>();
                if (email) steps.Add(new { tool = ToolCatalog.EmailCreateDraft, arguments = new { accountRef = setup.Alias, to = new[] { "offline@example.test" }, subject = "Exact recovery", body = "Private immutable recovery body" } });
                if (tool == ToolCatalog.DriveUpload) await first.Services.GetRequiredService<ITenantWorkspaceMutationStore>().WriteTenantAsync(new TenantWorkspaceReference.SessionWorking(setup.Tenant, session), "/working/recovery.bin", bytes, _ => ValueTask.CompletedTask);
                if (tool == ToolCatalog.DriveTrashBatch)
                    foreach (var id in new[] { "first", "second" })
                    {
                        await first.Services.GetRequiredService<ISyntheticDriveStore>().SaveAsync(setup.Credential, new(id, id + ".bin", "application/octet-stream", bytes.Length, "1", null, ["root"], false), bytes, null, default);
                        steps.Add(new { tool = ToolCatalog.DriveGet, arguments = new { accountRef = setup.Alias, fileId = id } });
                    }
                object arguments = email ? new { accountRef = setup.Alias, draftId = "$draft" }
                    : tool == ToolCatalog.DriveCreateFolder ? new { accountRef = setup.Alias, authEpoch = 1, name = "Recovered folder", parentId = "root" }
                    : tool == ToolCatalog.DriveUpload ? new { accountRef = setup.Alias, authEpoch = 1, path = "/working/recovery.bin", name = "recovery.bin", folderId = "root" }
                    : new { accountRef = setup.Alias, authEpoch = 1, files = new[] { new { fileId = "first", expectedVersion = "1" }, new { fileId = "second", expectedVersion = "1" } } };
                steps.Add(new { tool, arguments });
                initial = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "synthetic account actions " + JsonSerializer.Serialize(new { steps }),
                    expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
                var runs = first.Services.GetRequiredService<ITenantRunStore>(); var approvals = first.Services.GetRequiredService<ITenantApprovalStore>(); var seen = new HashSet<Guid>();
                while (true)
                {
                    var waiting = await AwaitAccountConsent(runs, setup.Tenant, initial, seen);
                    Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}; {waiting.Result?.Text}");
                    request = (await approvals.FindApprovalAsync(setup.Tenant, waiting.ApprovalRequestId!.Value))!; Assert.True(seen.Add(request.RequestId));
                    var target = request.Operation.AccountAction?.ToolName == tool;
                    if (target) first.TenantExecutionEnabled = false;
                    var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
                    using var decision = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                        expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision });
                    Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
                    if (target) break;
                }
                await first.Services.GetRequiredService<TenantApprovalService>().DispatchPendingAsync();
                var parent = (await runs.FindTenantRunAsync(setup.Tenant, request.RunId))!;
                var queued = (await runs.FindTenantRunAsync(setup.Tenant, parent.Result!.ContinuationRunId!.Value))!;
                var clock = first.Services.GetRequiredService<TimeProvider>(); var ids = first.Services.GetRequiredService<IIdGenerator>();
                abandoned = await first.Services.GetRequiredService<ITenantRuntimeStore>().ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
                    new AgentRunCommand.Claim(queued.Revision, clock.GetUtcNow(), ids.NewId(), clock.GetUtcNow().AddMinutes(5)), ids.NewId());
                Assert.Equal(window.StartsWith("exception", StringComparison.Ordinal) ? ApprovalKind.ExceptionalGrant : ApprovalKind.Review, request.Kind);
                probe.Request = request.RequestId; probe.Armed = true;
                await Assert.ThrowsAsync<AbandonedAccountException>(() => first.Services.GetRequiredService<TenantAccountEffectService>().ExecuteApprovedAsync(new(setup.Tenant, abandoned.AgentRunId),
                    request.RequestId, request.Operation.OperationId, abandoned.Claim!.Generation, request.Operation.ActionHash).AsTask());
                Assert.Null(await first.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(setup.Tenant, request.Operation.OperationId));
                abandoned = (await runs.FindTenantRunAsync(setup.Tenant, abandoned.AgentRunId))!;
                Assert.Equal(AgentRunSideEffectDisposition.InFlight, abandoned.SideEffect.Disposition);
                if (request.Kind == ApprovalKind.ExceptionalGrant)
                    Assert.Equal(OperationGrantStatus.Claimed, (await approvals.FindOperationGrantAsync(setup.Tenant, request.RequestId))!.Status);
                if (email)
                {
                    send = await first.Services.GetRequiredService<IEmailStore>().GetSendAsync(request.Operation.AccountAction!.Draft!.DraftId);
                    if (faultWindow == "reserved") Assert.Null(send);
                    else Assert.Equal(faultWindow == "uncertain" ? EmailSendOutcome.Indeterminate : EmailSendOutcome.Sent, send!.Result!.Outcome);
                }
                else if (faultWindow != "reserved")
                {
                    var references = tool == ToolCatalog.DriveTrashBatch
                        ? new[] { AccountBatchOperationId(request.Operation.OperationId, 0, "first"), AccountBatchOperationId(request.Operation.OperationId, 1, "second") }
                        : new[] { request.Operation.OperationId };
                    foreach (var id in references)
                        if (await first.Services.GetRequiredService<IDriveOperationStore>().GetAsync(setup.Instance, id, default) is { } operation) native.Add(operation);
                    Assert.Equal(faultWindow == "uncertain" && tool == ToolCatalog.DriveTrashBatch ? 1 : tool == ToolCatalog.DriveTrashBatch ? 2 : 1, native.Count);
                    Assert.All(native, n => Assert.Equal(faultWindow == "uncertain" ? "Indeterminate" : "Succeeded", n.State));
                }
                if (window == "revoked") await access.RevokeAssignmentAsync(human, setup.Tenant, assignment.AssignmentId, await Revision(owner, setup.Tenant));
                Assert.Equal(0, first.Outbound);
            }
            var before = probe.Writes; Assert.Equal(faultWindow == "reserved" ? 0 : tool == ToolCatalog.DriveTrashBatch && faultWindow != "uncertain" ? 2 : 1, before);
            probe.Armed = false;
            var recoveryClock = new FakeTimeProvider(abandoned.Claim!.LeaseExpiresAtUtc.AddMinutes(1));
            await using (var second = new OAuthFactory(root, sqlite: true, preserve: true, execute: true, model: new CredentialModel(), servicesOverride: services => {
                services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(recoveryClock);
                // Keep counting all attempted native writes after host recreation.
                services.RemoveAll<ITenantConnectedAccountTransportFactory>();
                services.AddSingleton<ITenantConnectedAccountTransportFactory>(p => new CrashAccountTransport(p.GetRequiredService<TenantConnectedAccountReader>(), probe));
            }))
            {
                using var owner = ConnectedClient(second); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var store = second.Services.GetRequiredService<ITenantApprovedOperationStore>(); var runs = second.Services.GetRequiredService<ITenantRunStore>();
                var host = second.Services.GetServices<IHostedService>().OfType<AgentRunHostedService>().Single();
                async Task<AgentRun> Pump(Guid firstRun, bool waiting)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var id = firstRun;
                    while (true)
                    {
                        var value = (await runs.FindTenantRunAsync(setup.Tenant, id, timeout.Token))!;
                        if (value.Result?.ContinuationRunId is { } next) { id = next; continue; }
                        if (value.IsTerminal || waiting && value.Status == AgentRunStatus.WaitingForApproval) return value;
                        // Run the actual registered host pass without advancing
                        // execution budgets merely to drive its periodic cadence.
                        await host.RunPassAsync(timeout.Token); await Task.Delay(25, timeout.Token);
                    }
                }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); TenantOperationReceipt? receipt;
                while ((receipt = await store.FindOperationReceiptAsync(setup.Tenant, request.Operation.OperationId, deadline.Token)) is null) await Task.Delay(25, deadline.Token);
                Assert.Equal(TenantOperationOutcome.Indeterminate, receipt.Outcome); Assert.Equal(TenantAccountEffectRules.Uncertain, receipt.ReasonCode);
                Assert.Equal(abandoned.AgentRunId, receipt.RunId); Assert.Equal(request.RootActivationId, receipt.RootActivationId);
                Assert.Equal(0, await second.Services.GetRequiredService<TenantAccountEffectService>().RecoverAsync());
                if (request.Kind == ApprovalKind.ExceptionalGrant)
                    Assert.Equal(OperationGrantStatus.Indeterminate, (await second.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(setup.Tenant, request.RequestId))!.Status);
                var finished = await Pump(initial, false);
                Assert.True(finished.IsTerminal, $"{finished.Status}: {finished.Failure?.Code}; {finished.Result?.Text}");
                Assert.DoesNotContain("Private immutable recovery body", finished.Result?.Text ?? "");
                Assert.Equal(receipt, await second.Services.GetRequiredService<TenantAccountEffectService>().ExecuteApprovedAsync(new(setup.Tenant, abandoned.AgentRunId), request.RequestId,
                    request.Operation.OperationId, (await runs.FindTenantRunAsync(setup.Tenant, abandoned.AgentRunId))!.Claim?.Generation ?? abandoned.Claim.Generation, request.Operation.ActionHash));
                if (email) Assert.Equal(send, await second.Services.GetRequiredService<IEmailStore>().GetSendAsync(request.Operation.AccountAction!.Draft!.DraftId));
                else foreach (var operation in native) Assert.Equal(operation, await second.Services.GetRequiredService<IDriveOperationStore>().GetAsync(setup.Instance, operation.OperationId, default));
                if (!email && window != "revoked")
                {
                    var api = $"/api/v2/tenants/{setup.Tenant}";
                    var inspectionSession = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = setup.Instance,
                        expectedRevision = await Revision(owner, setup.Tenant), expectedInstanceRevision = 2 }))).GetProperty("sessionId").GetGuid();
                    var readInitial = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{inspectionSession}/messages", new { messageId = Guid.NewGuid(),
                        text = "synthetic account read " + JsonSerializer.Serialize(new { tool = ToolCatalog.DriveGet, arguments = new { accountRef = setup.Alias, operationId = request.Operation.OperationId } }),
                        expectedRevision = await Revision(owner, setup.Tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
                    var waiting = await Pump(readInitial, true);
                    Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}; {waiting.Result?.Text}");
                    var read = (await second.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(setup.Tenant, waiting.ApprovalRequestId!.Value))!;
                    Assert.Equal(receipt.RunId, read.Operation.AccountRead!.Source!.RunId); Assert.Equal("Indeterminate", read.Operation.AccountRead.Source.RecordedOutcome);
                    var detail = await Json(await owner.GetAsync(api + $"/approvals/{read.RequestId}"));
                    using var accepted = await owner.PostAsJsonAsync(api + $"/approvals/{read.RequestId}/decision", new { decision = "approve", actionHash = read.Operation.ActionHash,
                        expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = read.Revision });
                    Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
                    var inspected = await Pump(readInitial, false);
                    Assert.True(inspected.Status == AgentRunStatus.Completed, $"{inspected.Status}: {inspected.Failure?.Code}; {inspected.Result?.Text}");
                    Assert.Contains("Recorded outcome: Indeterminate", inspected.Result!.Text);
                    Assert.True(AgentRunToolCallCheckpoint.TryRead(inspected.Checkpoint, out var messages));
                    using var output = JsonDocument.Parse(messages!.Last(m => m.Role == ModelRole.Tool).Text);
                    var operation = output.RootElement.GetProperty("accountRead").GetProperty("result").GetProperty("operation");
                    Assert.False(operation.GetProperty("replayAllowed").GetBoolean());
                    var items = operation.GetProperty("items").EnumerateArray().ToArray();
                    Assert.Equal(tool == ToolCatalog.DriveTrashBatch ? 2 : 1, items.Length);
                    if (faultWindow == "reserved") Assert.All(items, i => Assert.Equal("no_retained_evidence", i.GetProperty("evidence").GetString()));
                    else if (faultWindow == "uncertain")
                    {
                        Assert.Equal("current_remote_metadata", items[0].GetProperty("evidence").GetString()); Assert.Equal("verified", items[0].GetProperty("status").GetString());
                        if (tool == ToolCatalog.DriveTrashBatch) Assert.Equal("no_retained_evidence", items[1].GetProperty("evidence").GetString());
                    }
                    else Assert.All(items, i => Assert.Equal("retained_verification", i.GetProperty("evidence").GetString()));
                    Assert.Equal(receipt, await store.FindOperationReceiptAsync(setup.Tenant, receipt.OperationId));
                }
                if (request.Kind == ApprovalKind.ExceptionalGrant)
                    Assert.Equal(OperationGrantStatus.Indeterminate, (await second.Services.GetRequiredService<ITenantApprovalStore>().FindOperationGrantAsync(setup.Tenant, request.RequestId))!.Status);
                if (request.Kind == ApprovalKind.ExceptionalGrant)
                {
                    var audit = await second.Services.GetRequiredService<IIamStore>().ReadAuditAsync(setup.Tenant, 200);
                    Assert.Single(audit, a => a.Action == TenantAccountEffectRules.ResultAction && a.Execution?.OperationId == request.Operation.OperationId);
                }
                Assert.Equal(before, probe.Writes); Assert.Equal(0, second.Outbound);
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
