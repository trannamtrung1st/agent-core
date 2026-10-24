using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_large_document_publication_and_next_turn_format_repair_preserve_original_bytes_and_one_Artifact(bool sqlite, bool responseFunction)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-document-repair-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new OwnedDocumentRecoveryModel(responseFunction);
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            using var owner = factory.CreateClient(); await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, modelKey = responseFunction ? "scripted-alpha" : "scripted-beta", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Owned document publication and independent reads", ["agents.read", "sessions.read", "workspaces.read", "workspaces.write", "artifacts.create", "artifacts.read"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            async Task<Guid> Submit(string text, long revision) => (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = revision }))).GetProperty("runId").GetGuid();
            var runs = factory.Services.GetRequiredService<ITenantRunStore>();
            var firstId = await Submit("Write the substantial playbook and publish the exact saved bytes.", 1);
            var first = await AwaitBrowserRestartRun(runs, tenant, firstId, false);
            Assert.Equal(AgentRunStatus.Completed, first.Status);
            Assert.Equal(responseFunction ? "scripted-alpha" : "scripted-beta", first.PinnedModel.CatalogKey);
            var artifact = Assert.Single((await Json(await owner.GetAsync(api + $"/sessions/{session}/artifacts"))).GetProperty("items").EnumerateArray());
            var id = artifact.GetProperty("artifactId").GetGuid();
            Assert.True(Encoding.UTF8.GetByteCount(OwnedDocumentRecoveryModel.Document) > 16 * 1024);
            Assert.Equal(OwnedDocumentRecoveryModel.Document, await owner.GetStringAsync(api + $"/sessions/{session}/artifacts/{id}/content"));
            var firstHistory = (await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(tenant, session, null, null, 100))!;
            var answer = firstHistory.History.Items.Single(e => e.Role == ConversationRole.Assistant);
            Assert.True(answer.Envelope!.Blocks.Count == 1, $"status={answer.Status}; textLength={answer.Text.Length}; modelCalls={model.Requests.Count}; catalog={first.PinnedModel.CatalogKey}; lastTools={string.Join(",", model.Requests.Last().Tools?.Select(t => t.Name) ?? [])}");
            Assert.Equal(id.ToString("D"), Assert.Single(answer.Envelope.Blocks).ArtifactId);
            var nextId = await Submit("Write the same document into notes.md first, then report the saved note.", firstHistory.SessionRevision);
            var next = await AwaitBrowserRestartRun(runs, tenant, nextId, false);
            Assert.Equal(AgentRunStatus.Completed, next.Status);
            Assert.Equal(OwnedDocumentRecoveryModel.Document, await owner.GetStringAsync(api + $"/sessions/{session}/workspace/content?path=%2Fworking%2Fnotes.md"));
            Assert.Equal(OwnedDocumentRecoveryModel.Document, await owner.GetStringAsync(api + $"/sessions/{session}/artifacts/{id}/content"));
            Assert.Single((await Json(await owner.GetAsync(api + $"/sessions/{session}/artifacts"))).GetProperty("items").EnumerateArray());
            var history = (await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(tenant, session, null, null, 100))!.History.Items.Where(e => e.Role == ConversationRole.Assistant).ToArray();
            Assert.Equal(2, history.Length); Assert.All(history, h => Assert.Equal(EntryStatus.Completed, h.Status));
            Assert.Equal("Saved the note in notes.md.", history[1].Text); Assert.Empty(history[1].Envelope!.Blocks);
            Assert.Equal(new[] { ToolCatalog.WorkspaceWrite, ToolCatalog.ArtifactsCreateFromWorkspace, ToolCatalog.WorkspaceWrite }, model.Calls);
            Assert.Equal(6, model.Requests.Count); var repair = model.Requests.Last();
            Assert.Empty(repair.Tools ?? []); Assert.Null(repair.ResponseContract);
            Assert.Contains(repair.Messages, m => m.Text == ProtocolFailures.InvalidBlocksInstruction);
            Assert.Single(repair.Messages, m => m.Role == ModelRole.Tool && m.ToolCallId == "document-note" && m.Name == ToolCatalog.WorkspaceWrite);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class OwnedDocumentRecoveryModel(bool responseFunction) : ILanguageModel
    {
        public static readonly string Document = string.Concat(Enumerable.Repeat("# Scrum playbook\nObserve the current evidence before making changes.\n", 400));
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public ConcurrentQueue<string> Calls { get; } = new();
        public ModelCapabilities Capabilities => new(true, true, Tools: true, StructuredOutput: !responseFunction);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request); await Task.CompletedTask;
            var count = Requests.Count;
            ModelToolCall? call = count switch
            {
                1 => new("document-write", ToolCatalog.WorkspaceWrite, JsonSerializer.Serialize(new { path = "/working/playbook.md", content = Document })),
                2 => new("document-publish", ToolCatalog.ArtifactsCreateFromWorkspace, """{"path":"/working/playbook.md","displayName":"Scrum playbook.md"}"""),
                4 => new("document-note", ToolCatalog.WorkspaceWrite, JsonSerializer.Serialize(new { path = "/working/notes.md", content = Document })),
                _ => null
            };
            if (call is not null)
            {
                Assert.Contains(request.Tools!, t => t.Name == call.Name); Calls.Enqueue(call.Name);
                yield return new ModelToolCallEvent(call); yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            string text;
            if (count == 3)
            {
                using var result = JsonDocument.Parse(request.Messages.Last(m => m.Role == ModelRole.Tool).Text);
                Assert.True(result.RootElement.GetProperty("applied").GetBoolean());
                var id = result.RootElement.GetProperty("operationId").GetGuid().ToString("D");
                text = JsonSerializer.Serialize(new { disposition = "Complete", action = new { kind = "chat.respond" }, displayText = "Saved the playbook.", speech = new { mode = "same" }, memory = Array.Empty<object>(), blocks = new[] { new { kind = "artifactReference", artifactId = id } } });
            }
            else text = count == 5 ? """{"disposition":"Complete","action":{"kind":"chat.respond"},"displayText":"Saved the note.","speech":{"mode":"same"},"memory":[],"blocks":[{"kind":"widget"}]}""" : "Saved the note in notes.md.";
            if (responseFunction && count is 3 or 5)
            {
                yield return new ModelToolCallEvent(new("document-response-" + count, AssistantResponseSchema.ResponseFunctionName, text));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
            else { yield return new ModelTextDelta(text); yield return new ModelCompleted(ModelStopReason.Completed); }
        }
    }
}
