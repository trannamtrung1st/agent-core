using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false, "none")] [InlineData(true, "none")]
    [InlineData(false, "binding")] [InlineData(true, "binding")] [InlineData(false, "revision")] [InlineData(true, "revision")] [InlineData(false, "iam")] [InlineData(true, "iam")]
    public Task Space_knowledge_snapshot_uses_actual_shared_owner_and_native_protected_model_reader(bool sqlite, string mutation) => SpaceSourceReadWorkflow(sqlite,mutation);
    [Theory] [InlineData(false,"docx")] [InlineData(true,"docx")] [InlineData(false,"xlsx")] [InlineData(true,"xlsx")]
    public Task Space_office_snapshot_inspection_uses_original_bytes_and_actual_source_provenance(bool sqlite,string format) => SpaceSourceReadWorkflow(sqlite,"none",format);
    private async Task SpaceSourceReadWorkflow(bool sqlite,string mutation,string? officeFormat=null)
    {
        var root = Path.Combine(Path.GetTempPath(), "space-resource-native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tool=officeFormat=="docx"?ToolCatalog.OfficeDocumentInspect:officeFormat=="xlsx"?ToolCatalog.OfficeSpreadsheetInspect:ToolCatalog.KnowledgeRetrieve;
            var model = new AttachmentReadModel { ReadTool = tool, HoldAfterRead = mutation != "none", ReadRelease = release };
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient();
            var prepared = await PrepareAttachmentRead(factory.Services, owner, "text", virtualTool: tool); var v = prepared.Execution;
            var api = $"/api/v2/tenants/{v.Tenant}"; var space = Guid.NewGuid(); var resource = Guid.NewGuid();
            Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(api + "/spaces", new { spaceId = space, name = "Shared source", description = "", instructions = "", expectedRevision = await Revision(owner, v.Tenant) })).StatusCode);
            var files = new CanonicalWorkspaceFilesystem(AgentWorkspacePhysicalPaths.SpaceHomeDirectory(Path.Combine(root, "workspaces"), v.Tenant, space), "/home"); files.Ensure();
            var body = "Immutable shared knowledge → thử nghiệm";
            var sourcePath="/home/policy."+(officeFormat??"txt");var logicalPath=officeFormat is null?"knowledge/policy.txt":"office/shared."+officeFormat;var sourceKind=officeFormat is null?"Knowledge":"Reference";
            var processor=factory.Services.GetRequiredService<IOfficeProcessor>();
            var bytes=officeFormat=="docx"?processor.CreateDocument(new("Space source report",[new(null,[body],null)]),default).Bytes
                :officeFormat=="xlsx"?processor.CreateWorkbook(new([new("Data",[[new("text",Text:body)]])]),default).Bytes:Encoding.UTF8.GetBytes(body);
            await files.WriteAsync(sourcePath,bytes,65536,131072);
            var path = api + $"/spaces/{space}/resources/{resource}";
            var create = new { sourcePath, logicalPath, kind = sourceKind, expectedRevision = await Revision(owner, v.Tenant), expectedSpaceRevision = 1, expectedResourceRevision = 0 };
            var created = await owner.PutAsJsonAsync(path, create); var source = await Json(created);
            Assert.True(created.IsSuccessStatusCode, source.GetRawText()); Assert.Equal(space, source.GetProperty("spaceId").GetGuid());
            Assert.Equal(1, source.GetProperty("revision").GetInt64()); Assert.DoesNotContain("instanceId", source.GetRawText());
            var afterCreate = await Revision(owner, v.Tenant);
            Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(path, create)).StatusCode);
            Assert.Equal(afterCreate, await Revision(owner, v.Tenant));
            await files.WriteAsync(sourcePath, "New mutable home bytes"u8.ToArray(), 65536, 131072);
            var key = SpaceResourceKeys.Create(space, resource);
            var membership = await owner.PutAsJsonAsync(api + $"/spaces/{space}/memberships/{prepared.Instance}", new {
                responsibility = "Reader", instructions = "", disabledInheritedBindingKeys = Array.Empty<string>(),
                addedBindings = new[] { new { kind = "SpaceFile", resourceKey = key } }, expectedRevision = await Revision(owner, v.Tenant), expectedSpaceRevision = 2, expectedMembershipRevision = 0 });
            Assert.Equal(HttpStatusCode.OK, membership.StatusCode);
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(v.Human.PrincipalId, v.Tenant, null, "Read shared immutable source", ["spaces.read", "agents.read", "sessions.read"], await Revision(owner, v.Tenant));
            var agent = (await factory.Services.GetRequiredService<ITenantAgentStore>().FindTenantAgentAsync(v.Tenant, prepared.Instance))!;
            await access.AssignRoleAsync(v.Human.PrincipalId, v.Tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(v.Tenant), await Revision(owner, v.Tenant));
            var sourceRole = await access.SaveRoleAsync(v.Human.PrincipalId, v.Tenant, null, "Read exact Space source", ["resources.read"], await Revision(owner, v.Tenant));
            var sourceGrant = await access.AssignRoleAsync(v.Human.PrincipalId, v.Tenant, agent.PrincipalId, sourceRole.RoleId, TenantResourceScopes.Source(v.Tenant, prepared.Instance, key), await Revision(owner, v.Tenant));
            var selected = await owner.PostAsJsonAsync(api + $"/sessions/{v.Session}/context/switch", new { transitionId = Guid.NewGuid(), spaceId = space, expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = 1, expectedSelectionRevision = 1 });
            Assert.True(selected.IsSuccessStatusCode, await selected.Content.ReadAsStringAsync());
            var virtualPath=SpaceResourceKeys.VirtualPath(space,resource,logicalPath);
            model.Arguments=officeFormat=="xlsx"?JsonSerializer.Serialize(new{path=virtualPath,sheet="Data",range="A1:A1"}):officeFormat=="docx"?JsonSerializer.Serialize(new{path=virtualPath}):JsonSerializer.Serialize(new{identity=key}); model.ReadEnabled=true;
            var session = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!;
            var submitted = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{v.Session}/messages", new { messageId = Guid.NewGuid(), text = "Read the shared policy", expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = session.Snapshot.Revision }));
            if (mutation != "none")
            {
                await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                if (mutation == "binding")
                    Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + $"/spaces/{space}/memberships/{prepared.Instance}", new {
                        responsibility = "Reader", instructions = "", disabledInheritedBindingKeys = Array.Empty<string>(), addedBindings = Array.Empty<object>(),
                        expectedRevision = await Revision(owner, v.Tenant), expectedSpaceRevision = 3, expectedMembershipRevision = 1 })).StatusCode);
                else if (mutation == "revision")
                    Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(path, new { sourcePath, logicalPath, kind = sourceKind,
                        expectedRevision = await Revision(owner, v.Tenant), expectedSpaceRevision = 3, expectedResourceRevision = 1 })).StatusCode);
                else await access.RevokeAssignmentAsync(v.Human.PrincipalId, v.Tenant, sourceGrant.AssignmentId, await Revision(owner, v.Tenant));
                release.TrySetResult();
            }
            var run = await AttachmentTerminal(factory.Services.GetRequiredService<ITenantRunStore>(), v.Tenant, submitted.GetProperty("runId").GetGuid());
            Assert.True(run.Status == (mutation == "none" ? AgentRunStatus.Completed : AgentRunStatus.Failed), $"{run.Status}: {run.Failure}");
            if (mutation != "none") { Assert.Equal("protected-output-unavailable", run.Failure?.Code); return; }
            var pin = Assert.Single(run.Admission.Configuration!.Resources, r => r.Key == key);
            Assert.Equal(new SpaceResourceOrigin(v.Tenant, space, resource, 1), pin.SpaceSource);
            Assert.StartsWith("/space/resources/", pin.VirtualPath, StringComparison.Ordinal);
            Assert.DoesNotContain(body, string.Join("\n", model.Requests.First().Messages.Select(m => m.Text)));
            var result = Assert.Single(model.Requests.Last().Messages, m => m.Role == ModelRole.Tool && m.Name == tool);
            using var json = JsonDocument.Parse(result.Text);
            if(officeFormat is null){Assert.Equal(body,json.RootElement.GetProperty("content").GetString());Assert.Equal(key,json.RootElement.GetProperty("identity").GetString());}
            else {Assert.True(json.RootElement.GetProperty("tenantWorkspaceRead").GetBoolean());Assert.Contains("Immutable shared knowledge",result.Text);Assert.Equal(virtualPath,json.RootElement.GetProperty("path").GetString());}

            Assert.Equal(HttpStatusCode.Conflict, (await owner.DeleteAsync(path + $"?expectedRevision={await Revision(owner, v.Tenant)}&expectedSpaceRevision=3&expectedResourceRevision=1")).StatusCode);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
