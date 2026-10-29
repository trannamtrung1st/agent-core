using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_owned_knowledge_read_preserves_actual_citation_in_bounded_JSON_or_returns_body_free_limit(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-knowledge-read-budget-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new AttachmentReadModel { ReadTool = ToolCatalog.KnowledgeRetrieve, HoldAfterRead = true };
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); var value = await PrepareAttachmentRead(factory.Services, owner, "text", virtualTool: ToolCatalog.KnowledgeRetrieve); var v = value.Execution;
            var api = $"/api/v2/tenants/{v.Tenant}";
            var body = string.Concat(Enumerable.Repeat("Current source retention rule → thử nghiệm.\n", 400));
            var saved = await Json(await owner.PostAsJsonAsync(api + $"/agents/{value.Instance}/resources", new {
                expectedRevision = await Revision(owner, v.Tenant), expectedInstanceRevision = 1,
                input = new { logicalPath = "bounded-policy.txt", kind = AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Knowledge, mediaType = "text/plain", content = Encoding.UTF8.GetBytes(body) } }));
            var identity = saved.GetProperty("resource").GetProperty("key").GetString()!;
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(v.Human.PrincipalId, v.Tenant, null, "Exact owned knowledge source", ["agents.read", "sessions.read", "resources.read"], await Revision(owner, v.Tenant));
            var state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(v.Tenant))!;
            await access.AssignRoleAsync(v.Human.PrincipalId, v.Tenant, state.SystemIdentities.Single(identity => identity.AgentInstanceId == value.Instance).PrincipalId, role.RoleId, AccessScope.ForTenant(v.Tenant), await Revision(owner, v.Tenant));
            model.Arguments = JsonSerializer.Serialize(new { identity }); model.ReadEnabled = true;
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{v.Session}/messages", new { messageId = Guid.NewGuid(), text = "Inspect the admitted knowledge policy",
                expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = 1 }));
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var runs = factory.Services.GetRequiredService<ITenantRunStore>(); var id = sent.GetProperty("runId").GetGuid();
            var running = (await runs.FindTenantRunAsync(v.Tenant, id))!;
            var actual = Assert.Single(model.Requests.Last().Messages, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.KnowledgeRetrieve);
            using var observation = JsonDocument.Parse(actual.Text);
            var citation = observation.RootElement.GetProperty("citation").GetString(); Assert.False(string.IsNullOrWhiteSpace(citation));
            var frame = new TenantRuntimeFrame(running.Owner, v.Session, id, running.Claim!.Generation);
            var reader = factory.Services.GetRequiredService<TenantWorkspaceRuntimeReadService>();
            var call = new ModelToolCall("bounded-knowledge", ToolCatalog.KnowledgeRetrieve, model.Arguments);
            var bounded = await reader.InspectAsync(frame, call, 4096);
            Assert.InRange(Encoding.UTF8.GetByteCount(bounded), 1, 4096);
            using var json = JsonDocument.Parse(bounded);
            Assert.True(json.RootElement.GetProperty("truncated").GetBoolean()); Assert.Equal(citation, json.RootElement.GetProperty("citation").GetString());
            Assert.Equal(identity, json.RootElement.GetProperty("identity").GetString()); Assert.True(json.RootElement.GetProperty("tenantWorkspaceRead").GetBoolean());
            var content = json.RootElement.GetProperty("content").GetString()!; Assert.NotEmpty(content); Assert.StartsWith(content, body, StringComparison.Ordinal);
            Assert.DoesNotContain("\uFFFD", bounded);
            foreach (var budget in new[] { 0, 1, 2, 48, 220 })
            {
                var tiny = await reader.InspectAsync(frame, call, budget); Assert.InRange(Encoding.UTF8.GetByteCount(tiny), 0, budget);
                if (tiny.Length == 0) continue;
                using var valid = JsonDocument.Parse(tiny); Assert.False(valid.RootElement.TryGetProperty("content", out _));
                Assert.DoesNotContain("Current source retention rule", tiny);
            }
            Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agent-runs/{id}/cancel", new { expectedRevision = await Revision(owner, v.Tenant), expectedRunRevision = running.Revision })).StatusCode);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_targeted_Attachment_read_without_Office_processor_never_substitutes_an_excerpt(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-attachment-office-unavailable-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new AttachmentReadModel { HoldAfterRead = true };
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); var value = await PrepareAttachmentRead(factory.Services, owner, "xlsx-range"); var v = value.Execution;
            var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            var first = await factory.Services.GetRequiredService<TenantRunService>().SubmitMessageAsync(v.Tenant, v.Session, v.Human, Guid.NewGuid(), "Keep this workbook available", [v.Id], await Revision(owner, v.Tenant), 1);
            Assert.Equal(AgentRunStatus.Completed, (await AttachmentTerminal(runs, v.Tenant, first.RunId)).Status);
            model.Requests.Clear(); model.ReadEnabled = true; model.Arguments = JsonSerializer.Serialize(new { attachmentId = v.Id, sheet = "Data", range = "A1:B1" });
            var saved = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(v.Tenant, v.Session))!;
            var sent = await Json(await owner.PostAsJsonAsync($"/api/v2/tenants/{v.Tenant}/sessions/{v.Session}/messages", new { messageId = Guid.NewGuid(), text = "Read the exact workbook window",
                expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = saved.Snapshot.Revision }));
            await model.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var running = (await runs.FindTenantRunAsync(v.Tenant, sent.GetProperty("runId").GetGuid()))!;
            var frame = new TenantRuntimeFrame(running.Owner, v.Session, running.AgentRunId, running.Claim!.Generation);
            var unavailable = await Assert.ThrowsAsync<AccessDeniedException>(() => factory.Services.GetRequiredService<TenantAttachmentRuntimeContextService>()
                .InspectReadAsync(frame, model.Arguments, false, ToolLimits.MaxOutputBytes, office: null).AsTask());
            Assert.Equal("office_processing_unavailable", unavailable.ReasonCode);
            Assert.Equal(2, model.Requests.Count);
            var actual = Assert.Single(model.Requests.Last().Messages, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AttachmentsRead);
            Assert.Contains("Private read body", actual.Text); Assert.True(actual.Parts is null or { Count: 0 });
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
