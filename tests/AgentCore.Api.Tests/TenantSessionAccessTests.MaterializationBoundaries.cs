using AgentCore.Domain.Conversation;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed record MaterializationSetup(Guid Tenant, Guid Session, Guid Instance, Guid Attachment, Guid Actor, string Api, byte[] Bytes, string Physical)
    {
        public string SessionUrl => Api + $"/sessions/{Session}";
        public string Url => SessionUrl + $"/attachments/{Attachment}/materialize";
    }
    private static async Task<MaterializationSetup> PrepareMaterialization(HttpClient owner, string root)
    {
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
        var actor = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
        var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
        var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid();
        var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
        var bytes = "Exact original thử nghiệm"u8.ToArray();
        using var form = new MultipartFormDataContent(); var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("text/plain"); form.Add(file, "file", "source.txt");
        var uploaded = await Json(await owner.PostAsync(api + $"/sessions/{session}/attachments?uploadId={Guid.NewGuid()}&expectedRevision={await Revision(owner, tenant)}&expectedSessionRevision=1", form));
        return new(tenant, session, instance, uploaded.GetProperty("attachmentId").GetGuid(), actor, api, bytes,
            AgentWorkspacePhysicalPaths.WorkingDirectory(Path.Combine(root, "workspaces"), instance, session));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Human_materialization_compound_permissions_are_independent_and_create_grants_no_read(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-materialize-auth-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ApprovalRecordingModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); var v = await PrepareMaterialization(owner, root);
            var identity = factory.Services.GetRequiredService<IdentityService>(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var account = await identity.RegisterAsync("copy-writer@example.test", "Copy writer", "strong-copy-password");
            var parent = await access.SaveRoleAsync(v.Actor, v.Tenant, null, "Session reader", ["sessions.read"], await Revision(owner, v.Tenant));
            var invite = await access.InviteAsync(v.Actor, v.Tenant, account.LoginKey, parent.RoleId, await Revision(owner, v.Tenant));
            var member = await access.AcceptInvitationAsync(account.AccountId, v.Tenant, invite.Invitation.InvitationId, invite.Token);
            using var writer = factory.CreateClient(); await Login(writer, account.LoginKey, "strong-copy-password");
            async Task Denied()
            {
                var response = await writer.PostAsJsonAsync(v.Url, new { expectedRevision = await Revision(owner, v.Tenant) });
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.False(Directory.Exists(v.Physical));
                Assert.Empty((await Json(await owner.GetAsync(v.SessionUrl + "/artifacts"))).GetProperty("items").EnumerateArray());
            }
            await Denied();
            var source = await access.SaveRoleAsync(v.Actor, v.Tenant, null, "Source read", ["attachments.read"], await Revision(owner, v.Tenant));
            var sourceGrant = await access.AssignRoleAsync(v.Actor, v.Tenant, member.PrincipalId, source.RoleId, TenantAttachmentRules.Scope(v.Tenant, v.Attachment), await Revision(owner, v.Tenant)); await Denied();
            var write = await access.SaveRoleAsync(v.Actor, v.Tenant, null, "Working write", ["workspaces.write"], await Revision(owner, v.Tenant));
            await access.AssignRoleAsync(v.Actor, v.Tenant, member.PrincipalId, write.RoleId, TenantWorkspaceRules.Working(v.Tenant, v.Session), await Revision(owner, v.Tenant)); await Denied();
            var create = await access.SaveRoleAsync(v.Actor, v.Tenant, null, "Artifact create", ["artifacts.create"], await Revision(owner, v.Tenant));
            await access.AssignRoleAsync(v.Actor, v.Tenant, member.PrincipalId, create.RoleId, TenantSessionRules.Scope(v.Tenant, v.Session), await Revision(owner, v.Tenant));
            await access.RevokeAssignmentAsync(v.Actor, v.Tenant, sourceGrant.AssignmentId, await Revision(owner, v.Tenant)); await Denied();
            await access.AssignRoleAsync(v.Actor, v.Tenant, member.PrincipalId, source.RoleId, TenantAttachmentRules.Scope(v.Tenant, v.Attachment), await Revision(owner, v.Tenant));
            var response = await writer.PostAsJsonAsync(v.Url, new { expectedRevision = await Revision(owner, v.Tenant) }); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var id = (await Json(response)).GetProperty("artifactId").GetGuid();
            Assert.Equal(HttpStatusCode.Forbidden, (await writer.GetAsync(v.SessionUrl + $"/artifacts/{id}/content")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await writer.GetAsync(v.SessionUrl + "/workspace/content?path=%2Fworking%2Fsource.txt")).StatusCode);
            Assert.Empty((await Json(await writer.GetAsync(v.SessionUrl + "/artifacts"))).GetProperty("items").EnumerateArray());
            var saved = await factory.Services.GetRequiredService<ITenantArtifactStore>().FindTenantAsync(v.Tenant, v.Session, id); Assert.Equal(member.PrincipalId, saved!.CreatedByPrincipalId);
            var read = await access.SaveRoleAsync(v.Actor, v.Tenant, null, "Artifact exact read", ["artifacts.read"], await Revision(owner, v.Tenant));
            await access.AssignRoleAsync(v.Actor, v.Tenant, member.PrincipalId, read.RoleId, TenantArtifactRules.Scope(v.Tenant, id), await Revision(owner, v.Tenant));
            Assert.Equal(v.Bytes, await writer.GetByteArrayAsync(v.SessionUrl + $"/artifacts/{id}/content"));
            using var anonymous = factory.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(v.Url, new { expectedRevision = 1 })).StatusCode);
            writer.DefaultRequestHeaders.Remove("X-AgentCore-CSRF"); Assert.Equal(HttpStatusCode.BadRequest, (await writer.PostAsJsonAsync(v.Url, new { expectedRevision = await Revision(owner, v.Tenant) })).StatusCode);
            Assert.Single(Directory.GetFiles(v.Physical)); Assert.Empty(model.Requests);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, "before-logout")] [InlineData(true, "before-logout")]
    [InlineData(false, "after-archive")] [InlineData(true, "after-archive")]
    [InlineData(false, "workspace-ack")] [InlineData(true, "workspace-ack")]
    [InlineData(false, "publication-logout")] [InlineData(true, "publication-logout")]
    [InlineData(false, "publication-ack")] [InlineData(true, "publication-ack")]
    public async Task Human_materialization_fences_partial_effects_and_lost_acknowledgements_without_automatic_replay(bool sqlite, string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-materialize-boundary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        HttpClient? owner = null; MaterializationSetup? v = null; var attempts = 0;
        async Task Logout() { Assert.Equal(HttpStatusCode.NoContent, (await owner!.PostAsync("/api/v2/auth/logout", null)).StatusCode); await Login(owner!, "owner@example.test", "explicit-synthetic-test-password"); }
        async Task AfterWorkspace()
        {
            if (boundary == "workspace-ack") throw new IOException("private workspace acknowledgement lost");
            if (boundary == "after-archive") Assert.Equal(HttpStatusCode.OK, (await owner!.PostAsJsonAsync(v!.SessionUrl + "/archive", new { expectedRevision = await Revision(owner!, v.Tenant), expectedSessionRevision = 1 })).StatusCode);
        }
        void Configure(IServiceCollection services)
        {
            services.Replace(ServiceDescriptor.Singleton<ITenantWorkspaceMutationStore>(p => new MaterializationWorkspaceHook((ITenantWorkspaceMutationStore)p.GetRequiredService<FileSpaceWorkspaceStore>(),
                async () => { attempts++; if (boundary == "before-logout") await Logout(); }, AfterWorkspace)));
            if (boundary.StartsWith("publication", StringComparison.Ordinal))
                services.Replace(ServiceDescriptor.Singleton<ITenantAttachmentMutationStore>(p => new MaterializationPublicationHook((ITenantAttachmentMutationStore)p.GetRequiredService<IIamStore>(),
                    boundary == "publication-logout" ? Logout : null, boundary == "publication-ack")));
        }
        try
        {
            var model = new ApprovalRecordingModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            using var client = factory.CreateClient(); owner = client; v = await PrepareMaterialization(owner, root);
            var response = await owner.PostAsJsonAsync(v.Url, new { expectedRevision = await Revision(owner, v.Tenant) });
            var json = await Json(response); var text = json.GetRawText(); Assert.DoesNotContain("private", text);
            var code = json.GetProperty("reasonCode").GetString();
            if (boundary == "before-logout") { Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.False(File.Exists(Path.Combine(v.Physical, "source.txt"))); }
            else
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Equal(boundary.EndsWith("ack", StringComparison.Ordinal) ? "attachment_materialization_unknown" : "attachment_materialization_incomplete", code);
                Assert.Equal(v.Bytes, await File.ReadAllBytesAsync(Path.Combine(v.Physical, "source.txt")));
            }
            var artifacts = (await Json(await owner.GetAsync(v.SessionUrl + "/artifacts"))).GetProperty("items");
            Assert.Equal(boundary == "publication-ack" ? 1 : 0, artifacts.GetArrayLength());
            Assert.Equal(1, attempts); Assert.Equal(v.Bytes, await owner.GetByteArrayAsync(v.SessionUrl + $"/attachments/{v.Attachment}/content"));
            Assert.Empty(await factory.Services.GetRequiredService<ITenantRunStore>().ListTenantRunIdsAsync(v.Tenant, v.Session)); Assert.Empty(model.Requests);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class MaterializationPublicationHook(ITenantAttachmentMutationStore inner, Func<Task>? before, bool lost) : ITenantAttachmentMutationStore
    {
        public ValueTask<AttachmentRecord?> TryUploadAttachmentAsync(TenantAttachmentMutation mutation, TenantAttachmentUpload upload, CancellationToken ct = default) => inner.TryUploadAttachmentAsync(mutation, upload, ct);
        public ValueTask<bool> TryAbortAttachmentAsync(TenantAttachmentMutation mutation, Guid id, CancellationToken ct = default) => inner.TryAbortAttachmentAsync(mutation, id, ct);
        public async ValueTask<ArtifactRecord?> TryPublishMaterializationAsync(TenantAttachmentMutation mutation, AttachmentRecord source, ArtifactRecord artifact, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        { if (before is not null) await before(); var result = await inner.TryPublishMaterializationAsync(mutation, source, artifact, bytes, ct); if (lost) throw new IOException("private publication acknowledgement lost"); return result; }
    }
    private sealed class MaterializationWorkspaceHook(ITenantWorkspaceMutationStore inner, Func<Task> before, Func<Task> after) : ITenantWorkspaceMutationStore
    {
        public async ValueTask<bool> TryWriteNewTenantAsync(TenantWorkspaceReference owner, string path, ReadOnlyMemory<byte> bytes, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default)
        { var result = await inner.TryWriteNewTenantAsync(owner, path, bytes, async token => { await before(); await authorizeCommit(token); }, ct); if (result) await after(); return result; }
        public ValueTask TransformOfficeTenantAsync(TenantWorkspaceReference owner, string path, bool create, Func<ReadOnlyMemory<byte>, CancellationToken, byte[]> process, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.TransformOfficeTenantAsync(owner, path, create, process, authorizeCommit, ct);
        public ValueTask WriteTenantAsync(TenantWorkspaceReference owner, string path, ReadOnlyMemory<byte> bytes, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.WriteTenantAsync(owner, path, bytes, authorizeCommit, ct);
        public ValueTask DeleteTenantAsync(TenantWorkspaceReference owner, string path, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.DeleteTenantAsync(owner, path, authorizeCommit, ct);
        public ValueTask<WorkspacePatchResult> PatchTenantAsync(TenantWorkspaceReference owner, string path, IReadOnlyList<WorkspaceTextEdit> edits, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.PatchTenantAsync(owner, path, edits, authorizeCommit, ct);
        public ValueTask<WorkspaceStructureResult> StructureTenantAsync(TenantWorkspaceReference owner, IReadOnlyList<WorkspaceStructuralOperation> operations, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.StructureTenantAsync(owner, operations, authorizeCommit, ct);
        public ValueTask CopyTenantAsync(TenantWorkspaceReference sourceOwner, string source, TenantWorkspaceReference destinationOwner, string destination, Func<CancellationToken, ValueTask> authorizeCommit, CancellationToken ct = default) => inner.CopyTenantAsync(sourceOwner, source, destinationOwner, destination, authorizeCommit, ct);
    }
}
