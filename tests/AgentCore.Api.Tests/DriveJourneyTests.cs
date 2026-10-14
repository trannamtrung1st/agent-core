using AgentCore.Application.Drive;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Credentials;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Contracts.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
namespace AgentCore.Api.Tests;

public sealed partial class DriveJourneyTests
{
    private sealed class Factory : AgentCoreApiFactory
    { protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?> { ["GoogleDrive:Enabled"] = "true", ["GoogleDriveOAuth:PublicAppUrl"] = "http://localhost" }; }
    [Fact]
    public async Task Synthetic_connect_bind_upload_verify_download_and_epoch_fences_share_only_explicit_grants()
    {
        using var host = new Factory(); var client = host.CreateClient(new() { AllowAutoRedirect = false }); client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(host.Services));
        var created = await client.PostAsJsonAsync("/api/v2/admin/credentials/google-drive", new { displayName = "Drive work" }); created.EnsureSuccessStatusCode(); Assert.True(created.Headers.CacheControl!.NoStore); var account = (await created.Content.ReadFromJsonAsync<AdminCredentialResponse>())!;
        var start = await client.PostAsJsonAsync($"/api/v2/admin/credentials/{account.CredentialId}/drive-connect", new { expectedRevision = account.Revision }); start.EnsureSuccessStatusCode();
        using var startJson = JsonDocument.Parse(await start.Content.ReadAsStringAsync()); var url = startJson.RootElement.GetProperty("authorizationUrl").GetString()!; var state = QueryHelpers.ParseQuery(new Uri("http://localhost" + url).Query)["state"].ToString();
        var consent = await client.PostAsync("/api/v2/integrations/google-drive/oauth/synthetic", new FormUrlEncodedContent(new Dictionary<string, string> { { "state", state }, { "decision", "authorize" } })); Assert.Equal(HttpStatusCode.SeeOther, consent.StatusCode);
        var connected = await host.Services.GetRequiredService<CredentialService>().GetAsync(account.CredentialId); Assert.Equal("Connected", connected.DriveAccount!.ConnectionStatus.ToString());
        Assert.DoesNotContain("synthetic-drive-refresh", JsonSerializer.Serialize(connected));
        async Task<(Guid Owner, Guid Session)> Instance(bool bind)
        {
            var made = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new { definitionId = "general-assistant", version = 27 }); made.EnsureSuccessStatusCode(); using var ownerJson = JsonDocument.Parse(await made.Content.ReadAsStringAsync()); var owner = Guid.Parse(ownerJson.RootElement.GetProperty("instanceId").GetString()!);
            if (bind) await host.Services.GetRequiredService<CredentialService>().BindAsync(owner, account.CredentialId, "drive-work", 1);
            var session = await client.PostAsJsonAsync("/api/v2/sessions", new { agentInstanceId = owner, mode = "text" }); session.EnsureSuccessStatusCode(); using var sessionJson = JsonDocument.Parse(await session.Content.ReadAsStringAsync()); return (owner, sessionJson.RootElement.GetProperty("sessionId").GetGuid());
        }
        var a = await Instance(true); var b = await Instance(true); var unbound = await Instance(false); var tools = host.Services.GetRequiredService<SessionToolExecutor>();
        async Task<JsonElement> Tool((Guid Owner, Guid Session) current, string name, object args, ToolApprovalGrant? grant = null)
        {
            var definition = (await host.Services.GetRequiredService<IMemoryStore>().LoadAsync(current.Session))!.Definition;
            var result = await tools.ExecuteAsync(definition, current.Session, new ModelToolCall(Guid.NewGuid().ToString(), name, JsonSerializer.Serialize(args)), 256 * 1024, approvalGrant: grant,
                admission: new(false, TriggerKind.UserTurn, AgentInstanceId: current.Owner, OwnedSessionId: current.Session)); using var json = JsonDocument.Parse(result.Text); return json.RootElement.Clone();
        }
        var denied = await Tool(unbound, "drive.list", new { accountRef = "drive-work" }); Assert.Contains("drive_not_connected", denied.ToString());
        var folder = await Tool(a, "drive.create_folder", new { accountRef = "drive-work", authEpoch = connected.DriveAccount.AuthEpoch, name = "Reports" }); Assert.False(folder.TryGetProperty("error", out _), folder.ToString()); var parent = folder.GetProperty("receipt").GetProperty("file").GetProperty("id").GetString();
        var source = "Office report exact bytes"u8.ToArray(); (await client.PutAsync($"/api/v2/sessions/{a.Session}/workspace/content?path=/home/report.txt", new ByteArrayContent(source))).EnsureSuccessStatusCode(); var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(source));
        var stale = await Tool(a, "drive.upload", new { accountRef = "drive-work", authEpoch = connected.DriveAccount.AuthEpoch, path = "/home/report.txt", expectedSha256 = new string('0', 64), folderId = parent }); Assert.Contains("drive_source_revision_conflict", stale.ToString());
        var uploaded = await Tool(a, "drive.upload", new { accountRef = "drive-work", authEpoch = connected.DriveAccount.AuthEpoch, path = "/home/report.txt", expectedSha256 = sha, folderId = parent }); Assert.False(uploaded.TryGetProperty("error", out _), uploaded.ToString()); var remote = uploaded.GetProperty("receipt").GetProperty("file");
        var listing = await Tool(b, "drive.list", new { accountRef = "drive-work", folderId = parent }); Assert.Contains(remote.GetProperty("id").GetString()!, listing.ToString());
        var downloaded = await Tool(b, "drive.download", new { accountRef = "drive-work", fileId = remote.GetProperty("id").GetString(), expectedVersion = remote.GetProperty("version").GetString(), path = "/home/download.txt" }); Assert.False(downloaded.TryGetProperty("error", out _), downloaded.ToString()); Assert.Equal(sha, downloaded.GetProperty("sha256").GetString());
        var duplicate = await Tool(b, "drive.download", new { accountRef = "drive-work", fileId = remote.GetProperty("id").GetString(), expectedVersion = remote.GetProperty("version").GetString(), path = "/home/download.txt" }); Assert.True(duplicate.TryGetProperty("error", out _));
        var accounts = (await client.GetFromJsonAsync<JsonElement[]>($"/api/v2/agent-instances/{a.Owner}/drive/accounts"))!; var bindingId = accounts[0].GetProperty("bindingId").GetGuid();
        var protectedContent = $"/api/v2/agent-instances/{a.Owner}/drive/accounts/drive-work/files/{remote.GetProperty("id").GetString()}/content?expectedVersion=1&authEpoch={connected.DriveAccount.AuthEpoch}&bindingId={bindingId}";
        Assert.Equal(source, await client.GetByteArrayAsync(protectedContent)); Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync(protectedContent)).StatusCode);
        var effective = await client.GetFromJsonAsync<JsonElement>($"/api/v2/admin/instances/{a.Owner}/effective-config");
        var archived = await client.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{a.Owner}/lifecycle", new { expectedRevision = effective.GetProperty("instanceRevision").GetInt64(), lifecycle = "Archived" }); archived.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(protectedContent)).StatusCode);
        var archivedAccounts = (await client.GetFromJsonAsync<JsonElement[]>($"/api/v2/agent-instances/{a.Owner}/drive/accounts"))!; Assert.False(archivedAccounts[0].GetProperty("available").GetBoolean()); Assert.Equal("Archived", archivedAccounts[0].GetProperty("status").GetString());
        effective = await client.GetFromJsonAsync<JsonElement>($"/api/v2/admin/instances/{a.Owner}/effective-config"); (await client.PatchAsJsonAsync($"/api/v2/admin/agent-instances/{a.Owner}/lifecycle", new { expectedRevision = effective.GetProperty("instanceRevision").GetInt64(), lifecycle = "Active" })).EnsureSuccessStatusCode();
        var trashArgs = new { accountRef = "drive-work", authEpoch = connected.DriveAccount.AuthEpoch, fileId = remote.GetProperty("id").GetString(), expectedVersion = remote.GetProperty("version").GetString() };
        Assert.Contains("approval_required", (await Tool(a, "drive.trash", trashArgs)).ToString()); using var exact = JsonDocument.Parse(JsonSerializer.Serialize(trashArgs)); var grant = new ToolApprovalGrant(Guid.NewGuid(), "drive.trash", ToolActionHash.Compute("drive.trash", exact.RootElement), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var trashed = await Tool(a, "drive.trash", trashArgs, grant); Assert.False(trashed.TryGetProperty("error", out _), trashed.ToString()); Assert.True(trashed.GetProperty("receipt").GetProperty("file").GetProperty("trashed").GetBoolean());
        await host.Services.GetRequiredService<IGoogleDriveOAuthService>().DisconnectAsync(account.CredentialId, connected.Revision, default);
        Assert.Contains("drive_not_connected", (await Tool(a, "drive.list", new { accountRef = "drive-work" })).ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(protectedContent)).StatusCode);
    }
    [Fact]
    public async Task Lost_upload_response_reconciles_known_identity_without_second_write_or_source_bytes()
    {
        using var host = new Factory(); var client = host.CreateClient(new() { AllowAutoRedirect = false }); client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(host.Services));
        var account = await host.Services.GetRequiredService<CredentialService>().CreateGoogleDriveAccountAsync("Recovery"); var oauth = host.Services.GetRequiredService<IGoogleDriveOAuthService>(); var ownerProfile = (await host.Services.GetRequiredService<ILocalUserProfileService>().GetLocalProfileAsync()).ProfileId;
        var start = await oauth.StartAsync(account.CredentialId, 1, ownerProfile, default); var state = QueryHelpers.ParseQuery(start.AuthorizationUrl[start.AuthorizationUrl.IndexOf('?')..])["state"].ToString(); await oauth.CompleteAsync(state, start.Correlation, ownerProfile, "consent", null, default);
        var made = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new { definitionId = "general-assistant", version = 27 }); made.EnsureSuccessStatusCode(); using var json = JsonDocument.Parse(await made.Content.ReadAsStringAsync()); var owner = json.RootElement.GetProperty("instanceId").GetGuid(); await host.Services.GetRequiredService<CredentialService>().BindAsync(owner, account.CredentialId, "drive", 1);
        var fault = new LostFactory(host.Services.GetRequiredService<IGoogleDriveProviderFactory>()); var service = new DriveService(host.Services.GetRequiredService<ICredentialStore>(), host.Services.GetRequiredService<IAgentCredentialBindingStore>(), host.Services.GetRequiredService<IAgentInstanceStore>(), fault, host.Services.GetRequiredService<IDriveOperationStore>(), TimeProvider.System);
        var access = await service.ResolveAsync(owner, "drive", null, true, default); var id = Guid.NewGuid(); var bytes = "hi"u8.ToArray(); var effect = new DriveEffect("report.txt", "root", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), 2, "text/plain");
        var pending = await Assert.ThrowsAsync<DriveIndeterminateException>(() => service.EffectAsync(access, id, "drive.upload", 1, null, effect, bytes, default).AsTask()); Assert.Equal(id, pending.OperationId); Assert.Equal(1, fault.Uploads);
        var recovered = await service.InspectOperationAsync(access, id, default); Assert.Equal("Succeeded", recovered.Status); Assert.Equal(pending.FileId, recovered.File.Id); Assert.Equal(1, fault.Uploads);
        var retry = await service.EffectAsync(access, id, "drive.upload", 1, null, effect, bytes, default); Assert.Equal(recovered.File.Id, retry.File.Id); Assert.Equal(1, fault.Uploads); Assert.Single((await service.ListAsync(access, null, null, null, 20, default)).Items);
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => service.EffectAsync(access, id, "drive.upload", 1, null, effect with { Name = "other.txt" }, bytes, default).AsTask());
        var wrong = new DriveAccess(Guid.NewGuid(), access.Binding, access.Credential, access.Provider); await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => service.InspectOperationAsync(wrong, id, default).AsTask());
    }
    private sealed class LostFactory(IGoogleDriveProviderFactory inner) : IGoogleDriveProviderFactory
    {
        public int Uploads; public string Mode = "after"; public TaskCompletionSource? Release; public IDriveOperationStore? Operations;
        public bool Enabled => inner.Enabled; public bool Configured => inner.Configured; public bool Synthetic => inner.Synthetic;
        public IGoogleDriveProvider Create(Guid id, long revision) => new LostProvider(inner.Create(id, revision), this);
    }
    private sealed class LostProvider(IGoogleDriveProvider inner, LostFactory owner) : IGoogleDriveProvider
    {
        public ValueTask<string> GenerateIdAsync(CancellationToken ct) => inner.GenerateIdAsync(ct);
        public ValueTask<DriveFilePage> ListAsync(string? folder, string? query, string? cursor, int limit, CancellationToken ct) => inner.ListAsync(folder, query, cursor, limit, ct);
        public ValueTask<DriveFile> GetAsync(string id, CancellationToken ct) => inner.GetAsync(id, ct);
        public ValueTask CreateFolderAsync(string id, string name, string parent, CancellationToken ct) => inner.CreateFolderAsync(id, name, parent, ct);
        public async ValueTask UploadAsync(string id, string name, string parent, string mime, ReadOnlyMemory<byte> bytes, DriveOperation op, CancellationToken ct)
        {
            owner.Uploads++;
            if (owner.Mode == "rejected") throw new DriveEffectNotExecutedException("drive_scope_limited", "Check the destination permission.");
            if (owner.Mode == "before") throw new HttpRequestException("unknown before creation");
            if (owner.Mode == "partial")
            {
                await owner.Operations!.UpdateAsync(op with { ProtectedUploadSession = "protected-test-session", Revision = op.Revision + 1 }, op.Revision, ct);
                throw new HttpRequestException("lost partial upload");
            }
            await inner.UploadAsync(id, name, parent, mime, bytes, op, ct);
            if (owner.Release is not null) await owner.Release.Task.WaitAsync(ct);
            if (owner.Mode == "after") throw new HttpRequestException("lost response");
        }
        public ValueTask<byte[]> DownloadAsync(DriveFile file, CancellationToken ct) => inner.DownloadAsync(file, ct);
        public ValueTask RenameAsync(string id, string name, CancellationToken ct) => inner.RenameAsync(id, name, ct);
        public ValueTask MoveAsync(string id, string source, string destination, CancellationToken ct) => inner.MoveAsync(id, source, destination, ct);
        public ValueTask TrashAsync(string id, CancellationToken ct) => inner.TrashAsync(id, ct);
    }

}
