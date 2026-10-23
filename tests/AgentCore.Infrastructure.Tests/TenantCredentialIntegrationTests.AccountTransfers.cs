using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.Extensions.Primitives;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    private static async Task<AgentRun> AccountPendingCall(Fixture f, AgentRun run, ModelToolCall call)
    {
        var messages = AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var prior) ? prior!.ToList() : [];
        messages.Add(new(ModelRole.Assistant, "", ToolCalls: [call]));
        var budget = run.Checkpoint?.PauseBudgetClock(f.Clock.GetUtcNow());
        return await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), run.Claim!.Generation,
                new(AgentRunToolCallCheckpoint.Write(messages, executionState: AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint)),
                    (budget?.StepCount ?? 0) + 1, budget?.OutputBytes ?? 0, budget?.RemainingOverallBudgetMs ?? 120000, budget?.ActiveExecutionMs ?? 100), null), f.Ids.NewId());
    }
    private static ModelToolCall AccountIntentCall(string tool, string arguments)
    {
        using var json = JsonDocument.Parse(arguments);
        return new(Guid.NewGuid().ToString("N"), ToolCatalog.RequestAuthorization, JsonSerializer.Serialize(new {
            operation = TenantAccountActionContract.IsTool(tool) ? TenantAccountActionContract.Operation : TenantAccountReadContract.Operation,
            action = TenantAccountActionContract.IsTool(tool) ? new { tool, arguments = json.RootElement } : null,
            read = !TenantAccountActionContract.IsTool(tool) ? new { tool, arguments = json.RootElement } : null
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
    }
    private static async Task<AgentRun> ApproveAccountContinuation(Fixture f, RuntimeCredentialSetup s, AccountActionServices services, TenantApprovalRequest request)
    {
        var view = await services.Approvals.ReadViewAsync(s.Tenant, request.RequestId, s.Owner);
        await services.Approvals.DecideAsync(s.Tenant, request.RequestId, s.Owner, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        Assert.Equal(1, await services.Approvals.DispatchPendingAsync());
        var parent = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, request.RunId))!;
        var successor = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, parent.Result!.ContinuationRunId!.Value))!;
        return await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(successor.Owner, successor.AgentRunId,
            new AgentRunCommand.Claim(successor.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
    }
    public static TheoryData<bool, string> AccountTransferCases
    {
        get
        {
            var cases = new TheoryData<bool, string>();
            foreach (var sqlite in new[] { false, true })
            foreach (var mode in new[] { "upload", "upload-changed", "download", "download-occupied", "rename", "move", "trash", "batch", "batch-partial", "rename-stale" }) cases.Add(sqlite, mode);
            return cases;
        }
    }
    [Theory] [MemberData(nameof(AccountTransferCases))]
    public async Task Account_transfers_and_mutations_bind_approved_metadata_and_original_bytes_with_ordered_partial_receipts(bool sqlite, string mode)
        => await AccountTransferScenario(sqlite, mode, null);

    private async Task AccountTransferScenario(bool sqlite, string mode, string? reconciliation)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await RuntimeCredential(f, true);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Exact transfers", ["sessions.read", "agent-runs.read", "drive.read", "drive.write", "drive.delete", "agents.read", "workspaces.read", "workspaces.write"], await Revision());
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), await Revision());
        var tool = mode.StartsWith("upload", StringComparison.Ordinal) ? ToolCatalog.DriveUpload : mode.StartsWith("download", StringComparison.Ordinal) ? ToolCatalog.DriveDownload
            : mode.StartsWith("rename", StringComparison.Ordinal) ? ToolCatalog.DriveRename : mode.StartsWith("move", StringComparison.Ordinal) ? ToolCatalog.DriveMove : mode.StartsWith("trash", StringComparison.Ordinal) ? ToolCatalog.DriveTrash : mode.StartsWith("folder", StringComparison.Ordinal) ? ToolCatalog.DriveCreateFolder : ToolCatalog.DriveTrashBatch;
        var primary = tool == ToolCatalog.DriveDownload ? "workspaces.write" : tool is ToolCatalog.DriveTrash or ToolCatalog.DriveTrashBatch ? "drive.delete" : "drive.write";
        if (tool is ToolCatalog.DriveUpload or ToolCatalog.DriveDownload or ToolCatalog.DriveCreateFolder)
            await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock).SaveAsync(s.Tenant, s.Owner,
                primary, new(OperationApprovalMode.Always, false), await Revision(), 0);
        byte[] bytes = [0, 255, 1, 42, 13, 10, 200]; byte[]? uploaded = null;
        var remote = new Dictionary<string, DriveFile>(StringComparer.Ordinal) {
            ["file"] = new("file", "Original.bin", "application/octet-stream", bytes.Length, "1", Convert.ToHexStringLower(MD5.HashData(bytes)), ["before"], false),
            ["second"] = new("second", "Second.bin", "application/octet-stream", bytes.Length, "1", null, ["before"], false),
            ["third"] = new("third", "Third.bin", "application/octet-stream", bytes.Length, "1", null, ["before"], false),
            ["after"] = new("after", "Destination", "application/vnd.google-apps.folder", 0, "1", null, ["root"], false)
        };
        var manifestCount = mode is "batch-1" ? 1 : mode is "batch-20" ? 20 : mode.StartsWith("batch-", StringComparison.Ordinal) && mode != "batch-partial" ? 7 : 3;
        var manifestIds = new[] { "file", "second", "third" }.Concat(Enumerable.Range(4, 18).Select(i => "file-" + i)).Take(manifestCount).ToArray();
        if (mode == "batch-reversed") Array.Reverse(manifestIds);
        foreach (var id in manifestIds.Where(id => !remote.ContainsKey(id))) remote[id] = new(id, id + ".bin", "application/octet-stream", bytes.Length, "1", null, ["before"], false);
        remote["sentinel"] = new("sentinel", "Untouched.bin", "application/octet-stream", bytes.Length, "1", null, ["before"], false);
        if (mode.EndsWith("-slash", StringComparison.Ordinal)) remote["file"] = remote["file"] with { Name = "Budget / 2026.txt" };
        var writes = new List<string>(); var tokenPosts = 0; var hiddenParentReads = 0; var destinationUnavailable = false;
        using var interrupted = new CancellationTokenSource();
        var http = new OAuthHttp(async request => {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/token") { tokenPosts++; return OAuthJson(new { access_token = "private-access", refresh_token = "rotated-private-refresh", token_type = "Bearer", expires_in = 3600 }); }
            var id = uri.AbsolutePath.Split('/').Last();
            if (id == "before") { hiddenParentReads++; return new(HttpStatusCode.NotFound); }
            if (id == "after" && destinationUnavailable) return new(HttpStatusCode.NotFound);
            if (id == "generateIds") return OAuthJson(new { ids = new[] { "uploaded-file" } });
            if (request.Method == HttpMethod.Post && uri.AbsolutePath.StartsWith("/upload/", StringComparison.Ordinal))
            {
                writes.Add("upload-start"); var response = OAuthJson(new { id = "uploaded-file" }); response.Headers.Location = new("https://www.googleapis.com/upload/drive/v3/session/exact"); return response;
            }
            if (request.Method == HttpMethod.Put)
            {
                if (request.Content!.Headers.ContentRange?.From is null && reconciliation is "uncertain" or "native-trashed")
                    throw new HttpRequestException("Upload status probe remains unconfirmed.");
                writes.Add("upload-bytes"); uploaded = await request.Content!.ReadAsByteArrayAsync();
                remote["uploaded-file"] = new("uploaded-file", "Original.bin", "application/octet-stream", uploaded.Length, "1", Convert.ToHexStringLower(MD5.HashData(uploaded)), ["root"], false);
                if (reconciliation is "uncertain" or "native-trashed")
                {
                    if (reconciliation == "native-trashed") remote["uploaded-file"] = remote["uploaded-file"] with { Trashed = true };
                    throw new HttpRequestException("Accepted upload acknowledgement lost.");
                }
                return OAuthJson(new { id = "uploaded-file" });
            }
            if (request.Method == HttpMethod.Patch)
            {
                writes.Add(id);
                if (mode is "batch-partial" or "batch-rejected" && id == "second") return new(HttpStatusCode.Forbidden);
                if (mode == "batch-unknown-before" && id == "second") throw new HttpRequestException("Dispatch result unavailable before application.");
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var file = remote[id];
                var name = body.RootElement.TryGetProperty("name", out var n) ? n.GetString()! : file.Name;
                var trashed = body.RootElement.TryGetProperty("trashed", out var t) ? t.GetBoolean() : file.Trashed;
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
                remote[id] = file with { Name = name, Trashed = trashed, Version = "2", Parents = query.TryGetValue("addParents", out StringValues parent) ? [parent.ToString()] : file.Parents };
                if (mode == "batch-unknown-after" && id == "second") throw new HttpRequestException("Accepted second mutation acknowledgement lost.");
                if (mode == "batch-interrupted" && id == "file") interrupted.Cancel();
                if (mode == "batch-revoked" && id == "file")
                {
                    var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
                    var assignment = state.Assignments.Single(a => a.PrincipalId == s.Agent.PrincipalId && a.RoleId == role.RoleId);
                    await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, assignment.AssignmentId, await Revision());
                }
                if (reconciliation is "uncertain" or "unconfirmed") throw new HttpRequestException("Accepted mutation acknowledgement lost.");
                return OAuthJson(new { id });
            }
            if (reconciliation == "unconfirmed" && writes.Count > 0) throw new HttpRequestException("Verification unavailable.");
            if (uri.Query.Contains("alt=media", StringComparison.Ordinal)) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            var metadata = remote[id];
            return OAuthJson(new { id = metadata.Id, name = metadata.Name, mimeType = metadata.MimeType, size = metadata.ByteSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                version = metadata.Version, md5Checksum = metadata.Md5Checksum, parents = metadata.Parents, trashed = metadata.Trashed });
        });
        var services = ActionServices(f, http); var run = s.Run;
        // Reads run through actual consent, receipt, controlled Google HTTP and successor checkpoint owners.
        var sourceIds = tool is ToolCatalog.DriveUpload or ToolCatalog.DriveCreateFolder ? mode.EndsWith("-destination", StringComparison.Ordinal) ? ["after"] : Array.Empty<string>()
            : tool == ToolCatalog.DriveMove ? new[] { "file", "after" } : tool == ToolCatalog.DriveTrashBatch ? manifestIds : ["file"];
        foreach (var id in sourceIds)
        {
            var input = TenantAccountReadContract.Parse(ToolCatalog.DriveGet, JsonSerializer.Serialize(new { accountRef = "work-drive", fileId = id }));
            run = await AccountPendingCall(f, run, AccountIntentCall(ToolCatalog.DriveGet, input.Json));
            var consent = (await services.Approvals.PrepareAccountReadAsync(new(s.Tenant, run.AgentRunId), new(ToolCatalog.DriveGet, input.Json), run.Revision, run.Claim!.Generation)).Request!;
            run = await ApproveAccountContinuation(f, s, services, consent);
            run = (await services.Continuations.ResumeAsync(run)).Run;
            Assert.Empty(writes);
        }
        var owner = new TenantWorkspaceReference(s.Tenant, s.Agent.Instance.InstanceId, run.SessionId);
        if (tool == ToolCatalog.DriveUpload) await ((ITenantWorkspaceMutationStore)services.Files).WriteTenantAsync(owner, "/working/Original.bin", bytes, _ => ValueTask.CompletedTask);
        var arguments = tool switch {
            ToolCatalog.DriveCreateFolder => """{"accountRef":"work-drive","authEpoch":1,"name":"New folder","parentId":"after"}""",
            ToolCatalog.DriveUpload => """{"accountRef":"work-drive","authEpoch":1,"path":"/working/Original.bin","name":"Original.bin","folderId":"root"}""",
            ToolCatalog.DriveDownload => """{"accountRef":"work-drive","fileId":"file","expectedVersion":"1","path":"/working/Downloaded.bin"}""",
            ToolCatalog.DriveRename => """{"accountRef":"work-drive","authEpoch":1,"fileId":"file","expectedVersion":"1","name":"Renamed.bin"}""",
            ToolCatalog.DriveMove => """{"accountRef":"work-drive","authEpoch":1,"fileId":"file","expectedVersion":"1","sourceParentId":"before","destinationParentId":"after"}""",
            ToolCatalog.DriveTrash => """{"accountRef":"work-drive","authEpoch":1,"fileId":"file","expectedVersion":"1"}""",
            _ => JsonSerializer.Serialize(new { accountRef = "work-drive", authEpoch = 1, files = manifestIds.Select(id => new { fileId = id, expectedVersion = "1" }) }) };
        if (mode == "upload-destination") arguments = arguments.Replace("\"folderId\":\"root\"", "\"folderId\":\"after\"", StringComparison.Ordinal);
        var intent = new TenantAccountActionIntent(tool, TenantAccountActionContract.Parse(run.SessionId, tool, arguments).Json);
        run = await AccountPendingCall(f, run, AccountIntentCall(tool, intent.ArgumentsJson));
        var prepared = await services.Approvals.PrepareAccountActionAsync(new(s.Tenant, run.AgentRunId), intent, run.Revision, run.Claim!.Generation);
        var request = prepared.Request!; Assert.NotNull(request); Assert.Empty(writes);
        run = await ApproveAccountContinuation(f, s, services, request);
        if (mode == "upload-changed") await ((ITenantWorkspaceMutationStore)services.Files).WriteTenantAsync(owner, "/working/Original.bin", new byte[] {99}, _ => ValueTask.CompletedTask);
        if (mode == "download-occupied") await ((ITenantWorkspaceMutationStore)services.Files).WriteTenantAsync(owner, "/working/Downloaded.bin", new byte[] {99}, _ => ValueTask.CompletedTask);
        if (mode == "rename-stale") remote["file"] = remote["file"] with { Version = "3" };
        if (mode == "batch-stale") remote["second"] = remote["second"] with { Version = "3" };
        if (mode.EndsWith("-destination", StringComparison.Ordinal)) destinationUnavailable = true;
        if (sqlite) services = ActionServices(f, http, new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock));
        if (mode == "batch-interrupted")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => services.Continuations.PrepareExternalEffectAsync(run, interrupted.Token).AsTask());
        else await services.Continuations.PrepareExternalEffectAsync(run);
        var receipt = (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId))!;
        var refused = mode is "upload-changed" or "download-occupied" or "rename-stale" or "batch-stale";
        Assert.Equal(refused ? TenantOperationOutcome.Denied : mode is "batch-partial" or "batch-rejected" or "batch-unknown-before" or "batch-unknown-after" or "batch-interrupted" or "batch-revoked" || mode.EndsWith("-destination", StringComparison.Ordinal) || reconciliation is "uncertain" or "unconfirmed" or "native-trashed" ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Completed, receipt.Outcome);
        run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, run.AgentRunId))!;
        if (mode == "batch-revoked")
        {
            Assert.Equal(new[] { "file" }, writes); Assert.True(remote["file"].Trashed);
            Assert.All(manifestIds.Skip(1), id => Assert.False(remote[id].Trashed)); Assert.False(remote["sentinel"].Trashed);
            // The result is withheld after revocation, while the factual native receipt survives.
            Assert.True(AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var withheld));
            using var payload = JsonDocument.Parse(withheld!.Last(m => m.Role == ModelRole.Tool).Text);
            Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("result").ValueKind);
            await services.Continuations.PrepareExternalEffectAsync(run); Assert.Single(writes); return;
        }
        var resumed = await services.Continuations.ResumeAsync(run);
        if (refused) { Assert.Empty(writes); if (mode == "download-occupied") Assert.Equal(new byte[] {99}, (await ((ITenantWorkspaceArtifactSourceStore)services.Files).CaptureArtifactSourceTenantAsync(owner, "/working/Downloaded.bin")).CopyContent()); }
        else if (tool == ToolCatalog.DriveUpload && !destinationUnavailable) Assert.Equal(bytes, uploaded);
        else if (tool == ToolCatalog.DriveDownload) Assert.Equal(bytes, (await ((ITenantWorkspaceArtifactSourceStore)services.Files).CaptureArtifactSourceTenantAsync(owner, "/working/Downloaded.bin")).CopyContent());
        if (tool == ToolCatalog.DriveTrashBatch && !refused && mode != "batch-interrupted")
        {
            var partial = mode is "batch-partial" or "batch-rejected" or "batch-unknown-before" or "batch-unknown-after";
            var attempted = reconciliation == "uncertain" ? 1 : partial ? 2 : manifestIds.Length;
            Assert.Equal(manifestIds.Take(attempted), writes);
            var applied = mode is "batch-partial" or "batch-rejected" or "batch-unknown-before" ? 1 : attempted;
            Assert.Equal(manifestIds.Take(applied), manifestIds.Where(id => remote[id].Trashed));
            Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var transcript));
            using var result = JsonDocument.Parse(transcript!.Last(m => m.Role == ModelRole.Tool).Text);
            var items = result.RootElement.GetProperty("result").GetProperty("Items").EnumerateArray().ToArray();
            Assert.Equal(manifestIds, items.Select(i => i.GetProperty("FileId").GetString()));
            var expected = manifestIds.Select((_, i) => reconciliation == "uncertain" ? i == 0 ? "indeterminate" : "unattempted" : partial ? i == 0 ? "completed" : i > 1 ? "unattempted" : mode is "batch-partial" or "batch-rejected" ? "denied" : "indeterminate" : "completed");
            Assert.Equal(expected, items.Select(i => i.GetProperty("Status").GetString()));
        }
        if (mode == "batch-interrupted")
        {
            Assert.Equal(new[] { "file" }, writes); Assert.True(remote["file"].Trashed);
            Assert.All(manifestIds.Skip(1), id => Assert.False(remote[id].Trashed));
        }
        Assert.False(remote["sentinel"].Trashed); Assert.Equal(0, hiddenParentReads);
        if (destinationUnavailable) Assert.Empty(writes);
        if (sqlite) services = ActionServices(f, http, new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock));
        var count = writes.Count; await services.Continuations.PrepareExternalEffectAsync(resumed.Run); await services.Continuations.ResumeAsync(resumed.Run); Assert.Equal(count, writes.Count);
        Assert.Equal(sourceIds.Length + (refused && mode is "upload-changed" or "download-occupied" ? 0 : 1), tokenPosts);
        if (reconciliation is not null)
            await ReconcileAccountTransfer(f, s, services, resumed.Run, request, receipt, writes, reconciliation);
        else if (mode.StartsWith("batch-", StringComparison.Ordinal))
            await ReconcileAccountTransfer(f, s, services, resumed.Run, request, receipt, writes,
                mode is "batch-unknown-before" or "batch-unknown-after" ? mode : mode == "batch-interrupted" ? "uncertain" : "retained");
    }
}
