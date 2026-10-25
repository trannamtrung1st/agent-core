using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false, "batch-1")] [InlineData(true, "batch-1")]
    [InlineData(false, "batch-7")] [InlineData(true, "batch-7")]
    [InlineData(false, "batch-20")] [InlineData(true, "batch-20")]
    public Task Canonical_Drive_manifests_complete_exact_one_seven_and_twenty_targets_without_replay(bool sqlite, string mode)
        => AccountTransferScenario(sqlite, mode, null);

    public static TheoryData<bool, string> DriveCombinationCases
    {
        get
        {
            var cases = new TheoryData<bool, string>();
            foreach (var sqlite in new[] { false, true })
            foreach (var mode in new[] { "batch-reversed", "batch-stale", "batch-rejected", "batch-unknown-before", "batch-unknown-after",
                "batch-interrupted", "batch-revoked", "rename-slash", "trash-slash", "move-slash", "move-destination", "folder-destination", "upload-destination" })
                cases.Add(sqlite, mode);
            return cases;
        }
    }
    [Theory] [MemberData(nameof(DriveCombinationCases))]
    public Task Canonical_Drive_combinations_preserve_exact_order_stop_boundaries_and_unavailable_parent_rules(bool sqlite, string mode)
        => AccountTransferScenario(sqlite, mode, null);

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Canonical_Drive_invalid_manifests_never_create_consent_or_reach_the_provider(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await RuntimeCredential(f, true);
        var services = ActionServices(f, new OAuthHttp(_ => throw new InvalidOperationException("Invalid manifests must never reach protected HTTP.")));
        var run = s.Run; var store = (ITenantApprovalStore)f.Iam;
        var before = (await store.ListApprovalsAsync(s.Tenant, null, null, new())).Items.Select(r => r.RequestId).ToArray();
        var tooMany = JsonSerializer.Serialize(Enumerable.Range(1, 21).Select(i => new { fileId = "file-" + i, expectedVersion = "1" }));
        foreach (var files in new[] { "[]", tooMany,
            """[{"fileId":"same","expectedVersion":"1"},{"fileId":"same","expectedVersion":"1"}]""",
            """[{"fileId":"file","expectedVersion":"1","approved":true}]""", """[{"fileId":"*","expectedVersion":"1"}]""" })
        {
            var arguments = "{\"accountRef\":\"work-drive\",\"authEpoch\":1,\"files\":" + files + "}";
            run = await AccountPendingCall(f, run, AccountIntentCall(ToolCatalog.DriveTrashBatch, arguments));
            await Assert.ThrowsAsync<ArgumentException>(() => services.Approvals.PrepareAccountActionAsync(
                new(s.Tenant, run.AgentRunId), new(ToolCatalog.DriveTrashBatch, arguments), run.Revision, run.Claim!.Generation).AsTask());
            Assert.Equal(before, (await store.ListApprovalsAsync(s.Tenant, null, null, new())).Items.Select(r => r.RequestId).ToArray());
        }
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Canonical_Drive_sequential_uploads_and_fresh_operations_preserve_original_bytes_and_terminal_Trash_evidence(bool sqlite, bool uncertainFirst)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await RuntimeCredential(f, true);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Exact transfers",
            ["sessions.read", "agent-runs.read", "drive.read", "drive.write", "agents.read", "workspaces.read", "workspaces.write"], await Revision());
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, AccessScope.ForTenant(s.Tenant), await Revision());
        await new TenantOperationPolicyService(f.Iam, (ITenantOperationPolicyStore)f.Iam, f.Authorizer, f.Identity, f.Ids, f.Clock).SaveAsync(s.Tenant, s.Owner,
            "drive.write", new(OperationApprovalMode.Always, false), await Revision(), 0);
        var remote = new Dictionary<string, DriveFile>(StringComparer.Ordinal);
        var content = new Dictionary<string, byte[]>(StringComparer.Ordinal); var starts = new List<string>(); var puts = new List<string>();
        var serial = 0; var tokenPosts = 0; string? activeId = null; string? activeName = null; string? activeMime = null;
        var http = new OAuthHttp(async request => {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/token") { tokenPosts++; return OAuthJson(new { access_token = "private-access", refresh_token = "rotated-private-refresh", token_type = "Bearer", expires_in = 3600 }); }
            var id = uri.AbsolutePath.Split('/').Last();
            if (id == "generateIds") return OAuthJson(new { ids = new[] { "uploaded-" + ++serial } });
            if (request.Method == HttpMethod.Post && uri.AbsolutePath.StartsWith("/upload/", StringComparison.Ordinal))
            {
                using var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                activeId = metadata.RootElement.GetProperty("id").GetString()!; activeName = metadata.RootElement.GetProperty("name").GetString()!;
                activeMime = metadata.RootElement.GetProperty("mimeType").GetString()!;
                var type = Assert.Single(request.Headers.GetValues("X-Upload-Content-Type"));
                Assert.Equal(MediaTypeHeaderValue.Parse(type).MediaType, activeMime);
                starts.Add(activeId); var response = OAuthJson(new { id = activeId });
                response.Headers.Location = new("https://www.googleapis.com/upload/drive/v3/session/" + activeId); return response;
            }
            if (request.Method == HttpMethod.Put)
            {
                if (request.Content!.Headers.ContentRange?.From is null) throw new HttpRequestException("Status remains unknown; no bytes may be replayed.");
                Assert.Equal(activeId, id); puts.Add(id); var bytes = await request.Content.ReadAsByteArrayAsync(); content[id] = bytes;
                remote[id] = new(id, activeName!, activeMime!, bytes.Length, "1", Convert.ToHexStringLower(MD5.HashData(bytes)), ["root"], uncertainFirst && serial == 1);
                if (uncertainFirst && serial == 1) throw new HttpRequestException("Accepted upload acknowledgement lost before the target was trashed.");
                return OAuthJson(new { id });
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            if (uri.Query.Contains("alt=media", StringComparison.Ordinal)) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(content[id]) };
            var file = remote[id]; return OAuthJson(new { id, name = file.Name, mimeType = file.MimeType, size = file.ByteSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                version = file.Version, md5Checksum = file.Md5Checksum, parents = file.Parents, trashed = file.Trashed });
        });
        var services = ActionServices(f, http); var run = s.Run;
        var owner = new TenantWorkspaceReference.SessionWorking(s.Tenant, run.SessionId);
        var names = uncertainFirst ? new[] { "retry.csv", "retry.csv" } : ["sample.docx", "sample.xlsx", "sample.csv", "sample.js", "sample.doc", "sample.xls", "sample.cs"];
        var operations = new List<Guid>(); TenantApprovalRequest? first = null; TenantOperationReceipt? firstReceipt = null;
        foreach (var name in names)
        {
            if (uncertainFirst && operations.Count == 1)
            {
                // A genuinely new human activation has a new root and no previous
                // effect checkpoint. Reusing the old root cannot mint a retry grant.
                run = await AccountFreshActivation(f, s, "Upload the original file as a new explicit operation");
                owner = new(s.Tenant, run.SessionId);
            }
            var bytes = Encoding.UTF8.GetBytes("Exact original fixture bytes for " + name);
            var path = "/working/" + name;
            await ((ITenantWorkspaceMutationStore)services.Files).WriteTenantAsync(owner, path, bytes, _ => ValueTask.CompletedTask);
            var args = TenantAccountActionContract.Parse(run.SessionId, ToolCatalog.DriveUpload,
                JsonSerializer.Serialize(new { accountRef = "work-drive", authEpoch = 1, path, name, folderId = "root" })).Json;
            run = await AccountPendingCall(f, run, AccountIntentCall(ToolCatalog.DriveUpload, args));
            var request = (await services.Approvals.PrepareAccountActionAsync(new(s.Tenant, run.AgentRunId), new(ToolCatalog.DriveUpload, args), run.Revision, run.Claim!.Generation)).Request!;
            Assert.Equal(operations.Count, starts.Count); Assert.Equal(operations.Count, puts.Count); operations.Add(request.Operation.OperationId);
            if (first is not null) Assert.NotEqual(first.RootActivationId, request.RootActivationId);
            Assert.Equal(operations.Count, operations.Distinct().Count());
            run = await ApproveAccountContinuation(f, s, services, request);
            if (sqlite) services = ActionServices(f, http, new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock));
            await services.Continuations.PrepareExternalEffectAsync(run);
            var receipt = (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId))!;
            var uncertain = uncertainFirst && operations.Count == 1;
            Assert.Equal(uncertain ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Completed, receipt.Outcome);
            var native = (await services.Ledger.GetAsync(s.Agent.Instance.InstanceId, request.Operation.OperationId, default))!;
            Assert.Equal(bytes, content[native.FileId]); Assert.Equal(uncertain, remote[native.FileId].Trashed);
            run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, run.AgentRunId))!;
            run = (await services.Continuations.ResumeAsync(run)).Run;
            await services.Continuations.PrepareExternalEffectAsync(run); await services.Continuations.ResumeAsync(run);
            Assert.Equal(operations.Count, starts.Count); Assert.Equal(operations.Count, puts.Count);
            if (uncertain)
            {
                first = request; firstReceipt = receipt;
                // An uncertain effect ends its original Run. A separate human activation
                // obtains only new read consent; it never resumes the upload session.
                run = await AccountFreshActivation(f, s, "Inspect the previous exact upload operation");
                var read = TenantAccountReadContract.Parse(ToolCatalog.DriveGet, JsonSerializer.Serialize(new { accountRef = "work-drive", operationId = request.Operation.OperationId }));
                run = await AccountPendingCall(f, run, AccountIntentCall(ToolCatalog.DriveGet, read.Json));
                var review = (await services.Approvals.PrepareAccountReadAsync(new(s.Tenant, run.AgentRunId), new(ToolCatalog.DriveGet, read.Json), run.Revision, run.Claim!.Generation)).Request!;
                run = await ApproveAccountContinuation(f, s, services, review); run = (await services.Continuations.ResumeAsync(run)).Run;
                Assert.True(AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var transcript));
                using var result = JsonDocument.Parse(transcript!.Last(m => m.Role == ModelRole.Tool).Text);
                var operation = result.RootElement.GetProperty("accountRead").GetProperty("result").GetProperty("operation");
                Assert.Equal("Indeterminate", operation.GetProperty("recordedOutcome").GetString()); Assert.False(operation.GetProperty("replayAllowed").GetBoolean());
                var item = Assert.Single(operation.GetProperty("items").EnumerateArray());
                Assert.Equal("failed", item.GetProperty("status").GetString()); Assert.Equal("current_remote_metadata:verified_target_trashed", item.GetProperty("evidence").GetString());
                native = (await services.Ledger.GetAsync(s.Agent.Instance.InstanceId, request.Operation.OperationId, default))!;
                Assert.Equal("Failed", native.State); Assert.Null(native.ProtectedUploadSession); Assert.Single(puts);
            }
        }
        Assert.Equal(names.Length, remote.Count); Assert.Equal(names.Length, starts.Distinct().Count()); Assert.Equal(starts, puts);
        Assert.Equal(names.Length + (uncertainFirst ? 1 : 0), tokenPosts);
        if (first is not null)
        {
            Assert.Equal(firstReceipt, await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(s.Tenant, first.Operation.OperationId));
            Assert.True(remote[starts[0]].Trashed); Assert.False(remote[starts[1]].Trashed);
            Assert.NotEqual(operations[0], operations[1]); Assert.NotEqual(starts[0], starts[1]);
        }
    }
    private static async Task<AgentRun> AccountFreshActivation(Fixture f, RuntimeCredentialSetup s, string message)
    {
        var revision = (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var session = await f.Sessions.CreateAsync(s.Tenant, s.Agent.Instance.InstanceId, s.Owner, SessionMode.Text, revision, s.Agent.Instance.Revision);
        revision = (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var submitted = await f.RunService().SubmitTextAsync(s.Tenant, session.Owner.SessionId, s.Owner, f.Ids.NewId(), message, revision, 1);
        var queued = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, submitted.RunId))!;
        return await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
    }

}
