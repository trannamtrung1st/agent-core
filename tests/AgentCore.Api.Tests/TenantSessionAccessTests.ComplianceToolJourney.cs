using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class ComplianceJourneyModel : ILanguageModel
    {
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public string Identity { get; set; } = "";
        public string Citation { get; private set; } = "";
        public string ArtifactContent { get; private set; } = "";
        public Guid Artifact { get; private set; }
        public string Answer => "Retention applies during this demonstration session. Source: " + Citation;
        public const string Markdown = "## Compliance retention\nThe report records the exact current retention policy and its citation.";
        public ModelCapabilities Capabilities { get; } = new(true, true, Vision: true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            var knowledge = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.KnowledgeRetrieve);
            if (knowledge is null)
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.KnowledgeRetrieve);
                yield return new ModelToolCallEvent(new("current-retention-policy", ToolCatalog.KnowledgeRetrieve, JsonSerializer.Serialize(new { identity = Identity })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            using var source = JsonDocument.Parse(knowledge.Text); Assert.True(source.RootElement.GetProperty("tenantWorkspaceRead").GetBoolean());
            Citation = source.RootElement.GetProperty("citation").GetString()!;
            ArtifactContent = "# Retention report\n" + source.RootElement.GetProperty("content").GetString() + "\n\nCitation: " + Citation;
            var publication = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.ArtifactsCreate);
            if (publication is null)
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.ArtifactsCreate);
                yield return new ModelToolCallEvent(new("publish-retention-report", ToolCatalog.ArtifactsCreate, JsonSerializer.Serialize(new { displayName = "retention-report.md", contentType = "text/markdown", content = ArtifactContent })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            using var receipt = JsonDocument.Parse(publication.Text); Assert.True(receipt.RootElement.GetProperty("applied").GetBoolean()); Artifact = receipt.RootElement.GetProperty("operationId").GetGuid();
            yield return new ModelSemanticResponseReady(new(Answer, new(ModelSpeechMode.None, null), [new(ModelResponseBlockKind.Markdown, Text: Markdown), new(ModelResponseBlockKind.ArtifactReference, ArtifactId: Artifact.ToString("D"))]));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Compliance_image_turn_reads_current_knowledge_cites_exact_source_and_publishes_real_report_with_Markdown(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-compliance-tool-journey-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var model = new ComplianceJourneyModel();
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            factory.UseKestrel(0); using var ready = factory.CreateClient(); var cookies = new CookieContainer();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            var value = await PrepareAttachmentRead(factory.Services, owner, "png", virtualTools: [ToolCatalog.KnowledgeRetrieve, ToolCatalog.ArtifactsCreate]); var v = value.Execution; var api = $"/api/v2/tenants/{v.Tenant}";
            const string body = "Retain records only during this demonstration session. This is an exact owned Synthetic policy, not legal advice.";
            var resource = await Json(await owner.PostAsJsonAsync(api + $"/agents/{value.Instance}/resources", new { expectedRevision = await Revision(owner, v.Tenant), expectedInstanceRevision = 1,
                input = new { logicalPath = "retention-policy.txt", kind = AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Knowledge, mediaType = "text/plain", content = Encoding.UTF8.GetBytes(body) } }));
            model.Identity = resource.GetProperty("resource").GetProperty("key").GetString()!;
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(v.Human.PrincipalId, v.Tenant, null, "Current policy and exact report", ["agents.read", "sessions.read", "resources.read", "artifacts.create", "artifacts.read"], await Revision(owner, v.Tenant));
            var state = (await factory.Services.GetRequiredService<IIamStore>().ReadTenantAsync(v.Tenant))!;
            await access.AssignRoleAsync(v.Human.PrincipalId, v.Tenant, state.SystemIdentities.Single().PrincipalId, role.RoleId, AccessScope.ForTenant(v.Tenant), await Revision(owner, v.Tenant));
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(v.Tenant, v.Session))).Accepted);
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{v.Session}/messages", new { messageId = Guid.NewGuid(), text = "Cite retention for this compliance image and publish the exact report", attachmentIds = new[] { v.Id },
                expectedRevision = await Revision(owner, v.Tenant), expectedSessionRevision = 1 }));
            var id = sent.GetProperty("runId").GetGuid(); var response = sent.GetProperty("responseId").GetGuid().ToString("D");
            var done = await AttachmentTerminal(factory.Services.GetRequiredService<ITenantRunStore>(), v.Tenant, id);
            Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}"); Assert.Equal(3, model.Requests.Count);
            Assert.Contains(model.Requests.First().Messages.SelectMany(m => m.Parts ?? []), p => p is ModelImageContent);
            var read = Assert.Single(model.Requests.Last().Messages, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.KnowledgeRetrieve);
            using var source = JsonDocument.Parse(read.Text); Assert.Equal(body, source.RootElement.GetProperty("content").GetString()); Assert.Equal(model.Identity, source.RootElement.GetProperty("identity").GetString());
            var pinned = TenantKnowledgeReadContract.Resolve(done.Admission.Configuration!.Definition, done.Admission.Configuration.Resources, model.Identity);
            Assert.Equal(pinned.Source.Citation, model.Citation); Assert.False(string.IsNullOrWhiteSpace(model.Citation));
            await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == response));
            var started = events.Single(e => e.Type == "agent.response.started" && e.ResponseId == response);
            await AwaitTenantWire(() => events.LastOrDefault(e => e.Type == "session.state.changed" && e.Sequence > started.Sequence && Equals(e.Payload["outputState"], "idle")));
            var blocks = events.Where(e => e.Type == "agent.block.upsert" && e.ResponseId == response).ToArray(); Assert.Equal(2, blocks.Length);
            Assert.Contains(blocks, b => Equals(b.Payload["kind"], "markdown") && Equals(b.Payload["text"], ComplianceJourneyModel.Markdown));
            Assert.Contains(blocks, b => Equals(b.Payload["kind"], "artifact") && Equals(b.Payload["artifactId"], model.Artifact.ToString("D")));
            var receipt = new ClientCommand<ResponseReceiptPayload> { ProtocolVersion = 1, Type = "response.received", EventId = Guid.NewGuid().ToString("D"), SessionId = v.Session.ToString("D"), AttachmentId = started.AttachmentId,
                ResponseId = response, Sequence = 1, Payload = new() { TextEndExclusive = model.Answer.Length, BlockIds = blocks.Select(b => b.Payload["blockId"]!.ToString()!).ToArray() } };
            var ack = await hub.InvokeAsync<CommandAck>("ResponseReceived", receipt); Assert.True(ack.Accepted, ack.Error?.Message);
            var history = (await Json(await owner.GetAsync(api + $"/sessions/{v.Session}/messages"))).GetProperty("items");
            var answer = Assert.Single(history.EnumerateArray(), e => e.GetProperty("responseId").GetString() == response);
            Assert.Contains("demonstration session", answer.GetProperty("text").GetString()); Assert.Contains(model.Citation, answer.GetProperty("text").GetString()); Assert.Equal(2, answer.GetProperty("blocks").GetArrayLength());
            var files = (await Json(await owner.GetAsync(api + $"/sessions/{v.Session}/artifacts"))).GetProperty("items"); Assert.Equal(model.Artifact, Assert.Single(files.EnumerateArray()).GetProperty("artifactId").GetGuid());
            Assert.Equal(model.ArtifactContent, await owner.GetStringAsync(api + $"/sessions/{v.Session}/artifacts/{model.Artifact}/content"));
            Assert.Equal(value.Bytes, await owner.GetByteArrayAsync(api + $"/sessions/{v.Session}/attachments/{v.Id}/content"));
            Assert.DoesNotContain(Convert.ToBase64String(value.Bytes), done.Checkpoint!.PayloadJson);
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
