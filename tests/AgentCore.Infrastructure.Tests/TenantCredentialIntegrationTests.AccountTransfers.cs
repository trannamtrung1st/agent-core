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
            : mode.StartsWith("rename", StringComparison.Ordinal) ? ToolCatalog.DriveRename : mode == "move" ? ToolCatalog.DriveMove : mode == "trash" ? ToolCatalog.DriveTrash : ToolCatalog.DriveTrashBatch;
        var primary = tool == ToolCatalog.DriveDownload ? "workspaces.write" : tool is ToolCatalog.DriveTrash or ToolCatalog.DriveTrashBatch ? "drive.delete" : "drive.write";
        if (tool is ToolCatalog.DriveUpload or ToolCatalog.DriveDownload)
            await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock).SaveAsync(s.Tenant, s.Owner,
                primary, new(OperationApprovalMode.Always, false), await Revision(), 0);
        byte[] bytes = [0, 255, 1, 42, 13, 10, 200]; byte[]? uploaded = null;
        var remote = new Dictionary<string, DriveFile>(StringComparer.Ordinal) {
            ["file"] = new("file", "Original.bin", "application/octet-stream", bytes.Length, "1", Convert.ToHexStringLower(MD5.HashData(bytes)), ["before"], false),
            ["second"] = new("second", "Second.bin", "application/octet-stream", bytes.Length, "1", null, ["before"], false),
            ["third"] = new("third", "Third.bin", "application/octet-stream", bytes.Length, "1", null, ["before"], false),
            ["after"] = new("after", "Destination", "application/vnd.google-apps.folder", 0, "1", null, ["root"], false)
        };
        var writes = new List<string>(); var tokenPosts = 0;
        var http = new OAuthHttp(async request => {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/token") { tokenPosts++; return OAuthJson(new { access_token = "private-access", refresh_token = "rotated-private-refresh", token_type = "Bearer", expires_in = 3600 }); }
            var id = uri.AbsolutePath.Split('/').Last();
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
                if (mode == "batch-partial" && id == "second") return new(HttpStatusCode.Forbidden);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var file = remote[id];
                var name = body.RootElement.TryGetProperty("name", out var n) ? n.GetString()! : file.Name;
                var trashed = body.RootElement.TryGetProperty("trashed", out var t) ? t.GetBoolean() : file.Trashed;
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
                remote[id] = file with { Name = name, Trashed = trashed, Version = "2", Parents = query.TryGetValue("addParents", out StringValues parent) ? [parent.ToString()] : file.Parents };
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
        var sourceIds = tool == ToolCatalog.DriveUpload ? Array.Empty<string>() : tool == ToolCatalog.DriveMove ? new[] { "file", "after" }
            : tool == ToolCatalog.DriveTrashBatch ? ["file", "second", "third"] : ["file"];
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
            ToolCatalog.DriveUpload => """{"accountRef":"work-drive","authEpoch":1,"path":"/working/Original.bin","name":"Original.bin","folderId":"root"}""",
            ToolCatalog.DriveDownload => """{"accountRef":"work-drive","fileId":"file","expectedVersion":"1","path":"/working/Downloaded.bin"}""",
            ToolCatalog.DriveRename => """{"accountRef":"work-drive","authEpoch":1,"fileId":"file","expectedVersion":"1","name":"Renamed.bin"}""",
            ToolCatalog.DriveMove => """{"accountRef":"work-drive","authEpoch":1,"fileId":"file","expectedVersion":"1","sourceParentId":"before","destinationParentId":"after"}""",
            ToolCatalog.DriveTrash => """{"accountRef":"work-drive","authEpoch":1,"fileId":"file","expectedVersion":"1"}""",
            _ => """{"accountRef":"work-drive","authEpoch":1,"files":[{"fileId":"file","expectedVersion":"1"},{"fileId":"second","expectedVersion":"1"},{"fileId":"third","expectedVersion":"1"}]}""" };
        var intent = new TenantAccountActionIntent(tool, TenantAccountActionContract.Parse(run.SessionId, tool, arguments).Json);
        run = await AccountPendingCall(f, run, AccountIntentCall(tool, intent.ArgumentsJson));
        var prepared = await services.Approvals.PrepareAccountActionAsync(new(s.Tenant, run.AgentRunId), intent, run.Revision, run.Claim!.Generation);
        var request = prepared.Request!; Assert.NotNull(request); Assert.Empty(writes);
        run = await ApproveAccountContinuation(f, s, services, request);
        if (mode == "upload-changed") await ((ITenantWorkspaceMutationStore)services.Files).WriteTenantAsync(owner, "/working/Original.bin", new byte[] {99}, _ => ValueTask.CompletedTask);
        if (mode == "download-occupied") await ((ITenantWorkspaceMutationStore)services.Files).WriteTenantAsync(owner, "/working/Downloaded.bin", new byte[] {99}, _ => ValueTask.CompletedTask);
        if (mode == "rename-stale") remote["file"] = remote["file"] with { Version = "3" };
        await services.Continuations.PrepareExternalEffectAsync(run);
        var receipt = (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId))!;
        var refused = mode is "upload-changed" or "download-occupied" or "rename-stale";
        Assert.Equal(refused ? TenantOperationOutcome.Denied : mode == "batch-partial" || reconciliation is "uncertain" or "unconfirmed" or "native-trashed" ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Completed, receipt.Outcome);
        run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, run.AgentRunId))!;
        var resumed = await services.Continuations.ResumeAsync(run);
        if (refused) { Assert.Empty(writes); if (mode == "download-occupied") Assert.Equal(new byte[] {99}, (await ((ITenantWorkspaceArtifactSourceStore)services.Files).CaptureArtifactSourceTenantAsync(owner, "/working/Downloaded.bin")).CopyContent()); }
        else if (tool == ToolCatalog.DriveUpload) Assert.Equal(bytes, uploaded);
        else if (tool == ToolCatalog.DriveDownload) Assert.Equal(bytes, (await ((ITenantWorkspaceArtifactSourceStore)services.Files).CaptureArtifactSourceTenantAsync(owner, "/working/Downloaded.bin")).CopyContent());
        else if (mode == "batch-partial")
        {
            Assert.Equal(new[] { "file", "second" }, writes); Assert.True(remote["file"].Trashed); Assert.False(remote["third"].Trashed);
            Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var transcript));
            using var result = JsonDocument.Parse(transcript!.Last(m => m.Role == ModelRole.Tool).Text);
            Assert.Equal(new[] { "completed", "denied", "unattempted" }, result.RootElement.GetProperty("result").GetProperty("Items").EnumerateArray().Select(i => i.GetProperty("Status").GetString()).ToArray());
        }
        var count = writes.Count; await services.Continuations.PrepareExternalEffectAsync(resumed.Run); await services.Continuations.ResumeAsync(resumed.Run); Assert.Equal(count, writes.Count);
        Assert.Equal(sourceIds.Length + (refused && mode != "rename-stale" ? 0 : 1), tokenPosts);
        if (reconciliation is not null)
            await ReconcileAccountTransfer(f, s, services, resumed.Run, request, receipt, writes, reconciliation);
    }
}
