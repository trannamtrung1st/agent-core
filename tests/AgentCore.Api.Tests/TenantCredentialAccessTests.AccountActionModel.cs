using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api.Http;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    private static async Task<AgentRun> AwaitAccountConsent(ITenantRunStore runs, Guid tenant, Guid initial, IReadOnlySet<Guid> seen)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var id = initial;
        while (true)
        {
            var run = (await runs.FindTenantRunAsync(tenant, id, deadline.Token))!;
            if (run.Result?.ContinuationRunId is { } next) { id = next; continue; }
            if (run.IsTerminal || run.Status == AgentRunStatus.WaitingForApproval && run.ApprovalRequestId is { } request && !seen.Contains(request)) return run;
            await Task.Delay(25, deadline.Token);
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Model_account_actions_create_immutable_draft_then_request_separate_exact_send_and_return_factual_results(bool sqlite)
    {
        var model = new CredentialModel(); var diagnostics = new AccountReadDiagnostics();
        await using var source = new OAuthFactory(sqlite: sqlite, execute: true, model: model);
        await using var factory = source.WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(diagnostics)));
        using var owner = ConnectedClient(factory); var s = await ConnectedReadAccount(factory, owner, "EmailAccount", tools: true);
        var api = $"/api/v2/tenants/{s.Tenant}";
        var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var access = factory.Services.GetRequiredService<TenantAccessService>(); var agents = factory.Services.GetRequiredService<ITenantAgentStore>();
        var agent = (await agents.FindTenantAgentAsync(s.Tenant, s.Instance))!;
        var role = await access.SaveRoleAsync(actor, s.Tenant, null, "Exact email actions", ["sessions.read", "credentials.read", "credentials.use", "email.draft", "email.send"], await Revision(owner, s.Tenant));
        await access.AssignRoleAsync(actor, s.Tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), await Revision(owner, s.Tenant));
        const string subject = "Exact <script>inert</script> subject";
        const string body = "This is the full saved email body.\n<script>window.untrustedAccountBody = true</script>\nThe final line remains literal.";
        var command = "synthetic account actions " + JsonSerializer.Serialize(new { steps = new object[] {
            new { tool = ToolCatalog.EmailCreateDraft, arguments = new { accountRef = s.Alias, to = new[] { "friend@example.test" }, cc = new[] { "copy@example.test" }, bcc = new[] { "private-copy@example.test" }, subject, body } },
            new { tool = ToolCatalog.EmailSend, arguments = new { accountRef = s.Alias, draftId = "$draft" } }
        } });
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = s.Instance, expectedRevision = await Revision(owner, s.Tenant), expectedInstanceRevision = agent.Instance.Revision }))).GetProperty("sessionId").GetGuid();
        var initial = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = command,
            expectedRevision = await Revision(owner, s.Tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
        var seen = new HashSet<Guid>(); var requests = new List<TenantApprovalRequest>(); string? draft = null;
        foreach (var tool in new[] { ToolCatalog.EmailCreateDraft, ToolCatalog.EmailSend })
        {
            AgentRun waiting;
            try { waiting = await AwaitAccountConsent(runs, s.Tenant, initial, seen); }
            catch (OperationCanceledException) { throw new Xunit.Sdk.XunitException(string.Join("\n", diagnostics.Failures.TakeLast(8))); }
            Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}: {waiting.Failure?.Summary}; result={waiting.Result?.Text}; expected={tool}; modelCalls={model.Requests.Count}; schemaOffersAction={model.Requests.Last().Tools?.Any(t => t.Name == ToolCatalog.RequestAuthorization && t.ParametersJson.Contains(tool))}; tools={string.Join(" | ", model.Requests.Last().Messages.Where(m => m.Role == ModelRole.Tool).Select(m => m.Text))}");
            var request = (await approvals.FindApprovalAsync(s.Tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.Equal(tool, request.Operation.AccountAction!.ToolName); Assert.True(seen.Add(request.RequestId)); requests.Add(request);
            Assert.Equal(ApprovalKind.Review, request.Kind); Assert.Equal(s.Credential, request.Operation.AccountAction.Credential.CredentialId);
            Assert.Contains(TenantAccountActionContract.Operation, model.Requests.Last().Tools!.Single(t => t.Name == ToolCatalog.RequestAuthorization).ParametersJson);
            if (tool == ToolCatalog.EmailSend) Assert.Equal(draft, request.Operation.AccountAction.Draft!.DraftId);
            draft = request.Operation.AccountAction.Draft!.DraftId;
            Assert.Null(await factory.Services.GetRequiredService<IEmailStore>().GetSendAsync(draft));
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            Assert.Equal("use_connected_account", detail.GetProperty("preparedAction").GetProperty("operation").GetString());
            var changes = detail.GetProperty("review").GetProperty("changes").EnumerateArray().ToArray();
            Assert.Equal(body, Assert.Single(changes, c => c.GetProperty("field").GetString() == "emailBody").GetProperty("after").GetString());
            Assert.Equal("private-copy@example.test", Assert.Single(changes, c => c.GetProperty("field").GetString() == "emailBcc").GetProperty("after").GetString());
            using var decision = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision });
            Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        }
        var finished = await AwaitCredentialRun(runs, s.Tenant, initial, false);
        Assert.True(finished.Status == AgentRunStatus.Completed, $"{finished.Status}: {finished.Failure?.Code}: {finished.Failure?.Summary}");
        Assert.Contains("confirmed acceptance", finished.Result!.Text);
        var email = factory.Services.GetRequiredService<IEmailStore>(); var saved = (await email.GetDraftAsync(draft!))!;
        Assert.Equal(body, saved.Draft.Body); Assert.Equal(session, saved.SessionId); Assert.Equal(s.Instance, saved.AgentInstanceId);
        Assert.Equal(EmailSendOutcome.Sent, (await email.GetSendAsync(draft!))!.Result!.Outcome);
        var receipts = factory.Services.GetRequiredService<ITenantApprovedOperationStore>();
        foreach (var request in requests)
        {
            var receipt = (await receipts.FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId))!;
            Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome); Assert.Equal(TenantAccountEffectRules.Completed, receipt.ReasonCode);
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            var result = detail.GetProperty("operationResult").GetProperty("accountAction");
            Assert.Equal(request.Operation.AccountAction!.ToolName == ToolCatalog.EmailCreateDraft ? "draft_created" : "sent", result.GetProperty("status").GetString());
            Assert.Equal(draft, result.GetProperty("draftId").GetString());
            Assert.DoesNotContain("ProtectedPayload", detail.GetRawText());
        }
        Assert.Equal(0, source.Outbound); Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
        var audit = await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(s.Tenant, 200);
        Assert.Equal(2, audit.Count(a => a.Action == TenantAccountEffectRules.ResultAction));
    }
    public static TheoryData<bool, string> ModelDriveActions
    {
        get {
            var cases = new TheoryData<bool, string>();
            foreach (var sqlite in new[] { false, true })
            foreach (var tool in new[] { ToolCatalog.DriveCreateFolder, ToolCatalog.DriveUpload, ToolCatalog.DriveDownload, ToolCatalog.DriveRename, ToolCatalog.DriveMove, ToolCatalog.DriveTrash, ToolCatalog.DriveTrashBatch }) cases.Add(sqlite, tool);
            return cases;
        }
    }
    [Theory] [MemberData(nameof(ModelDriveActions))]
    public Task Model_drive_actions_use_approved_metadata_and_exact_original_bytes(bool sqlite, string tool) => ModelDriveScenario(sqlite, tool, false);

    [Theory] [MemberData(nameof(ModelDriveActions))]
    public Task Model_drive_operation_reads_use_separate_exact_consent_and_preserve_factual_receipts(bool sqlite, string tool) => ModelDriveScenario(sqlite, tool, true);

    private async Task ModelDriveScenario(bool sqlite, string tool, bool reconcile)
    {
        var model = new CredentialModel();
        await using var factory = new OAuthFactory(sqlite: sqlite, execute: true, model: model);
        using var owner = ConnectedClient(factory); var s = await ConnectedReadAccount(factory, owner, "GoogleDriveAccount", tools: true);
        var api = $"/api/v2/tenants/{s.Tenant}";
        var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(s.Tenant, s.Instance))!;
        var role = await access.SaveRoleAsync(actor, s.Tenant, null, "Exact model Drive actions", ["sessions.read", "agent-runs.read", "credentials.read", "credentials.use", "drive.read", "drive.write", "drive.delete", "workspaces.read", "workspaces.write", "agents.read"], await Revision(owner, s.Tenant));
        await access.AssignRoleAsync(actor, s.Tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), await Revision(owner, s.Tenant));
        var remote = factory.Services.GetRequiredService<ISyntheticDriveStore>(); byte[] bytes = [0, 255, 1, 42, 13, 10, 200];
        foreach (var id in new[] { "file", "second", "destination" })
            await remote.SaveAsync(s.Credential, new(id, id + ".bin", id == "destination" ? "application/vnd.google-apps.folder" : "application/octet-stream", id == "destination" ? 0 : bytes.Length, "1", null, ["root"], false), id == "destination" ? [] : bytes, null, default);
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = s.Instance, expectedRevision = await Revision(owner, s.Tenant), expectedInstanceRevision = 2 }))).GetProperty("sessionId").GetGuid();
        var workspace = new TenantWorkspaceReference.SessionWorking(s.Tenant, session);
        var files = factory.Services.GetRequiredService<ITenantWorkspaceMutationStore>();
        if (tool == ToolCatalog.DriveUpload) await files.WriteTenantAsync(workspace, "/working/Original.bin", bytes, _ => ValueTask.CompletedTask);
        object args = tool switch {
            ToolCatalog.DriveCreateFolder => new { accountRef = s.Alias, authEpoch = 1, name = "Exact folder", parentId = "root" },
            ToolCatalog.DriveUpload => new { accountRef = s.Alias, authEpoch = 1, path = "/working/Original.bin", name = "Original.bin", folderId = "root" },
            ToolCatalog.DriveDownload => new { accountRef = s.Alias, fileId = "file", expectedVersion = "1", path = "/working/Downloaded.bin" },
            ToolCatalog.DriveRename => new { accountRef = s.Alias, authEpoch = 1, fileId = "file", expectedVersion = "1", name = "Renamed.bin" },
            ToolCatalog.DriveMove => new { accountRef = s.Alias, authEpoch = 1, fileId = "file", expectedVersion = "1", sourceParentId = "root", destinationParentId = "destination" },
            ToolCatalog.DriveTrash => new { accountRef = s.Alias, authEpoch = 1, fileId = "file", expectedVersion = "1" },
            _ => new { accountRef = s.Alias, authEpoch = 1, files = new[] { new { fileId = "file", expectedVersion = "1" }, new { fileId = "second", expectedVersion = "1" } } }
        };
        var ids = tool is ToolCatalog.DriveCreateFolder or ToolCatalog.DriveUpload ? Array.Empty<string>()
            : tool == ToolCatalog.DriveMove ? ["file", "destination"] : tool == ToolCatalog.DriveTrashBatch ? ["file", "second"] : new[] { "file" };
        var steps = ids.Select(id => (object)new { tool = ToolCatalog.DriveGet, arguments = new { accountRef = s.Alias, fileId = id } }).ToList();
        steps.Add(new { tool, arguments = args });
        if (reconcile) steps.Add(new { tool = ToolCatalog.DriveGet, arguments = new { accountRef = s.Alias, operationId = "$operation" } });
        var command = "synthetic account actions " + JsonSerializer.Serialize(new { steps });
        var initial = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = command,
            expectedRevision = await Revision(owner, s.Tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var approvals = factory.Services.GetRequiredService<ITenantApprovalStore>();
        var seen = new HashSet<Guid>(); TenantApprovalRequest? action = null; TenantApprovalRequest? operationRead = null;
        foreach (var expected in ids.Select(_ => ToolCatalog.DriveGet).Append(tool).Concat(reconcile ? new[] { ToolCatalog.DriveGet } : []))
        {
            var waiting = await AwaitAccountConsent(runs, s.Tenant, initial, seen);
            Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{tool}: expected {expected}; {waiting.Status}: {waiting.Failure?.Code}; {waiting.Result?.Text}");
            var request = (await approvals.FindApprovalAsync(s.Tenant, waiting.ApprovalRequestId!.Value))!; Assert.True(seen.Add(request.RequestId));
            Assert.Equal(expected, request.Operation.AccountRead?.ToolName ?? request.Operation.AccountAction?.ToolName);
            if (request.Operation.AccountAction is not null) action = request;
            if (request.Operation.AccountRead?.Source is { } operationSource)
            {
                operationRead = request;
                Assert.Equal(action!.Operation.OperationId, operationSource.OperationId);
                Assert.Equal("Completed", operationSource.RecordedOutcome);
                Assert.Contains("operationId", request.Operation.AccountRead.ArgumentsJson);
            }
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            using var decision = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision });
            Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        }
        var finished = await AwaitCredentialRun(runs, s.Tenant, initial, false);
        Assert.True(finished.Status == AgentRunStatus.Completed, $"{finished.Status}: {finished.Failure?.Code}: {finished.Failure?.Summary}");
        Assert.Contains(reconcile ? "prior Drive operation was inspected without repeating it" : "exact Drive action completed", finished.Result!.Text);
        if (reconcile) Assert.NotNull(operationRead);
        var receipt = (await factory.Services.GetRequiredService<ITenantApprovedOperationStore>().FindOperationReceiptAsync(s.Tenant, action!.Operation.OperationId))!;
        Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome);
        var publicResult = (await Json(await owner.GetAsync(api + $"/approvals/{action.RequestId}"))).GetProperty("operationResult").GetProperty("accountAction");
        Assert.Equal(tool == ToolCatalog.DriveDownload ? "downloaded" : "completed", publicResult.GetProperty("status").GetString());
        if (tool == ToolCatalog.DriveDownload) {
            Assert.Equal(bytes, (await factory.Services.GetRequiredService<ITenantWorkspaceArtifactSourceStore>().CaptureArtifactSourceTenantAsync(workspace, "/working/Downloaded.bin")).CopyContent());
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), publicResult.GetProperty("sha256").GetString());
        }
        if (tool == ToolCatalog.DriveUpload) Assert.Equal(bytes, await remote.ReadAsync(s.Credential, publicResult.GetProperty("fileId").GetString()!, default));
        if (tool == ToolCatalog.DriveRename) Assert.Equal("Renamed.bin", (await remote.GetAsync(s.Credential, "file", default))!.Name);
        if (tool == ToolCatalog.DriveMove) Assert.Equal(new[] { "destination" }, (await remote.GetAsync(s.Credential, "file", default))!.Parents);
        if (tool is ToolCatalog.DriveTrash or ToolCatalog.DriveTrashBatch) Assert.True((await remote.GetAsync(s.Credential, "file", default))!.Trashed);
        if (tool == ToolCatalog.DriveTrashBatch) Assert.Equal(new[] { "file", "second" }, publicResult.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("fileId").GetString()));
        Assert.Equal(0, factory.Outbound); Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }

    private sealed class AccountActionBackgroundModel(string objective, ILanguageModel? model = null) : ILanguageModel
    {
        private readonly ILanguageModel inner = model ?? new CredentialModel();
        public Guid TargetInstanceId { get; set; }
        public ModelCapabilities Capabilities => inner.Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (request.Context?.Sections.Any(s => s.Id == AgentCore.Application.Context.ContextSectionId.CoreBackground) == true)
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.WorkComplete);
                Assert.Contains(request.Tools!, t => t.Name == "tasks.delegate");
                Assert.DoesNotContain(request.Tools!, t => t.Name is ToolCatalog.EmailSend or ToolCatalog.DriveRename);
                await foreach (var item in inner.GenerateAsync(request, ct)) yield return item;
                yield break;
            }
            var result = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == "tasks.delegate");
            if (result is null && request.Messages.Any(m => m.Role == ModelRole.User && m.Text == "Start exact account background test"))
            {
                Assert.Contains(request.Tools!, t => t.Name == "tasks.delegate");
                yield return new ModelToolCallEvent(new("account-background-start", "tasks.delegate", JsonSerializer.Serialize(new { objective, title = "Exact account action", reportOnCompletion = false, target = new { instanceId = TargetInstanceId }, requestKey = "native-account-task-once" })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            yield return new ModelSemanticResponseReady(new("Background account task admitted.", new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Model_Task_account_actions_preserve_worker_consent_successors_and_exact_effects(bool sqlite, bool drive)
    {
        var steps = drive ? new object[] {
            new { tool = ToolCatalog.DriveGet, arguments = (object)new { accountRef = "exact-account", fileId = "file" } },
            new { tool = ToolCatalog.DriveRename, arguments = (object)new { accountRef = "exact-account", authEpoch = 1, fileId = "file", expectedVersion = "1", name = "Background renamed.bin" } }
        } : new object[] {
            new { tool = ToolCatalog.EmailCreateDraft, arguments = (object)new { accountRef = "exact-account", to = new[] { "offline@example.test" }, subject = "Background exact draft", body = "Exact child task content." } },
            new { tool = ToolCatalog.EmailSend, arguments = (object)new { accountRef = "exact-account", draftId = "$draft" } }
        };
        var model = new AccountActionBackgroundModel("synthetic account actions " + JsonSerializer.Serialize(new { steps }));
        await using var factory = new OAuthFactory(sqlite: sqlite, execute: true, model: model);
        using var owner = ConnectedClient(factory); var s = await ConnectedReadAccount(factory, owner, drive ? "GoogleDriveAccount" : "EmailAccount", tools: true);
        var api = $"/api/v2/tenants/{s.Tenant}";
        var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var access = factory.Services.GetRequiredService<TenantAccessService>();
        var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(s.Tenant, s.Instance))!;
        var role = await access.SaveRoleAsync(actor, s.Tenant, null, "Exact background account actions", ["tasks.delegate", "tasks.read", "spaces.read", "sessions.create", "agent-runs.create", "sessions.read", "credentials.read", "credentials.use", "email.draft", "email.send", "drive.read", "drive.write", "agents.read"], await Revision(owner, s.Tenant));
        await access.AssignRoleAsync(actor, s.Tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), await Revision(owner, s.Tenant));
        await EnableAccountTask(owner, s, model);
        agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(s.Tenant, s.Instance))!;
        if (drive) await factory.Services.GetRequiredService<ISyntheticDriveStore>().SaveAsync(s.Credential, new("file", "Original.bin", "application/octet-stream", 3, "1", null, ["root"], false), [0, 255, 3], null, default);
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = s.Instance, expectedRevision = await Revision(owner, s.Tenant), expectedInstanceRevision = agent.Instance.Revision }))).GetProperty("sessionId").GetGuid();
        var initial = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Start exact account background test",
            expectedRevision = await Revision(owner, s.Tenant), expectedSessionRevision = 1 }))).GetProperty("runId").GetGuid();
        var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var sessions = factory.Services.GetRequiredService<ITenantSessionStore>();
        var parent = await AwaitCredentialRun(runs, s.Tenant, initial, false);
        Assert.True(parent.Status == AgentRunStatus.Completed, $"{parent.Status}: {parent.Failure?.Code}; {parent.Result?.Text}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        TenantSession? child = null;
        while (child is null) {
            child = (await sessions.ListTenantSessionsAsync(s.Tenant, null, 0, 100, deadline.Token)).Items.SingleOrDefault(c => c.Snapshot.Origin.OriginatingAgentRunId == initial);
            if (child is null) await Task.Delay(25, deadline.Token);
        }
        var seen = new HashSet<Guid>(); var requests = new List<TenantApprovalRequest>(); var childRun = child.Snapshot.Origin.InitialBackgroundAgentRunId!.Value;
        foreach (var expected in steps.Select(step => JsonSerializer.SerializeToElement(step).GetProperty("tool").GetString()))
        {
            var waiting = await AwaitAccountConsent(runs, s.Tenant, childRun, seen);
            Assert.True(waiting.Status == AgentRunStatus.WaitingForApproval, $"{waiting.Status}: {waiting.Failure?.Code}; {waiting.Result?.Text}");
            var request = (await factory.Services.GetRequiredService<ITenantApprovalStore>().FindApprovalAsync(s.Tenant, waiting.ApprovalRequestId!.Value))!;
            Assert.True(seen.Add(request.RequestId)); requests.Add(request);
            Assert.Equal(child.Owner.SessionId, request.SessionId); Assert.NotEqual(session, request.SessionId);
            Assert.Equal(expected, request.Operation.AccountRead?.ToolName ?? request.Operation.AccountAction?.ToolName);
            var detail = await Json(await owner.GetAsync(api + $"/approvals/{request.RequestId}"));
            using var decision = await owner.PostAsJsonAsync(api + $"/approvals/{request.RequestId}/decision", new { decision = "approve", actionHash = request.Operation.ActionHash,
                expectedRevision = detail.GetProperty("securityRevision").GetInt64(), expectedRequestRevision = request.Revision });
            Assert.Equal(HttpStatusCode.OK, decision.StatusCode);
        }
        var finished = await AwaitCredentialRun(runs, s.Tenant, childRun, false);
        Assert.True(finished.Status == AgentRunStatus.Completed, $"{finished.Status}: {finished.Failure?.Code}; {finished.Result?.Text}");
        Assert.Equal(AgentRunOutputContract.BackgroundOutcome, finished.Admission.OutputContract);
        Assert.Equal(child.Owner.SessionId, finished.SessionId);
        if (drive) Assert.Equal("Background renamed.bin", (await factory.Services.GetRequiredService<ISyntheticDriveStore>().GetAsync(s.Credential, "file", default))!.Name);
        else {
            var draft = requests[0].Operation.AccountAction!.Draft!.DraftId;
            Assert.Equal(EmailSendOutcome.Sent, (await factory.Services.GetRequiredService<IEmailStore>().GetSendAsync(draft))!.Result!.Outcome);
        }
        Assert.Equal(0, factory.Outbound); Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
    }

    private static async Task EnableAccountTask(HttpClient owner, ConnectedReadSetup setup, AccountActionBackgroundModel model)
    {
        model.TargetInstanceId = setup.Instance;
        var response = await owner.PutAsJsonAsync($"/api/v2/tenants/{setup.Tenant}/agents/{setup.Instance}/cooperation", new {
            cooperation = new { acceptTasks = true, acceptDirectMessages = false, allowDirectMessageWake = false, maxConcurrentTasks = 2 },
            expectedRevision = await Revision(owner, setup.Tenant), expectedInstanceRevision = 2 });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

}
