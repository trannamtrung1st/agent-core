using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Execution;
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
    private const string JourneySkill = "instance:journey-billing";
    private const string JourneyProcedure = "BILLING-PROCEDURE: Compare the exact saved account totals and answer once.";
    private const string JourneyNotice = "Still checking the billing case.";
    private const string JourneyAnswer = "Billing review is complete.";
    private const string JourneyInput = "Please review this billing account and report progress once.";
    private sealed class OwnedMessagingSkillModel : ILanguageModel
    {
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield(); ct.ThrowIfCancellationRequested(); Requests.Enqueue(request);
            var source = request.Messages.LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.WorkspaceRead);
            if (source is null)
            {
                Assert.DoesNotContain(request.Tools!, t => t.Name == ToolCatalog.AppMessageSend);
                yield return new ModelToolCallEvent(new("read-billing", ToolCatalog.WorkspaceRead, """{"path":"/home/billing.txt"}"""));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            using var observation = JsonDocument.Parse(source.Text); Assert.Equal("Exact saved billing total: 91.", observation.RootElement.GetProperty("content").GetString());
            if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AppMessageSend))
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.AppMessageSend);
                yield return new ModelToolCallEvent(new("billing-notice", ToolCatalog.AppMessageSend, JsonSerializer.Serialize(new { text = JourneyNotice })));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            if (!request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.SkillsLoad))
            {
                Assert.Contains(request.Tools!, t => t.Name == ToolCatalog.SkillsLoad);
                yield return new ModelToolCallEvent(new("load-missed-billing-skill", ToolCatalog.SkillsLoad, TenantSkillLoadContract.Arguments([JourneySkill])));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            Assert.Contains(JourneyProcedure, string.Join("\n", request.Messages.Select(m => m.Text)));
            yield return new ModelSemanticResponseReady(new(JourneyAnswer, new(ModelSpeechMode.None, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_owned_source_notice_and_on_demand_Skill_load_answer_once_without_replay_after_SQLite_host_recreation(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-messaging-skill-journey-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        WebApplicationFactory<Program>? first = null, second = null;
        try
        {
            var model = new OwnedMessagingSkillModel(); first = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            first.UseKestrel(0); using var ready = first.CreateClient(); var cookies = new CookieContainer(); Guid tenant, session, runId; JsonElement initialHistory;
            using (var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress })
            {
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
                var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
                var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "general-assistant", version = 32, expectedRevision = await Revision(owner, tenant) }));
                var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
                long instanceRevision = 1;
                foreach (var (id, procedure) in new[] { ("journey-billing", JourneyProcedure), ("journey-order", "UNUSED-ORDER-PROCEDURE") })
                {
                    var skill = await owner.PostAsJsonAsync(api + $"/agents/{instance}/skills/commands", new { operation = "create", expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision,
                        input = new { id, name = id, description = "Exact on-demand procedure metadata", procedure, projection = "OnDemand", enabled = true, requiredCapabilities = new[] { "chat.respond" } } });
                    Assert.Equal(HttpStatusCode.OK, skill.StatusCode); instanceRevision++;
                }
                session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = instanceRevision }))).GetProperty("sessionId").GetGuid();
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PutAsync(api + $"/agents/{instance}/workspace/content?path=%2Fhome%2Fbilling.txt&expectedRevision={await Revision(owner, tenant)}", new ByteArrayContent("Exact saved billing total: 91."u8.ToArray()))).StatusCode);
                var access = first.Services.GetRequiredService<TenantAccessService>();
                var role = await access.SaveRoleAsync(human, tenant, null, "Exact source and missed Skill", ["agents.read", "sessions.read", "workspaces.read", "skills.read"], await Revision(owner, tenant));
                await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
                var events = new ConcurrentQueue<ServerEvent>();
                await using (var hub = TenantSocket(ready.BaseAddress!, cookies, events))
                {
                    await hub.StartAsync(); Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
                    var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = JourneyInput, expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
                    runId = sent.GetProperty("runId").GetGuid(); var response = sent.GetProperty("responseId").GetGuid().ToString("D");
                    var done = await AttachmentTerminal(first.Services.GetRequiredService<ITenantRunStore>(), tenant, runId);
                    Assert.True(done.Status == AgentRunStatus.Completed, $"{done.Status}: {done.Failure?.Code}: {done.Failure?.Summary}");
                    Assert.Equal(4, model.Requests.Count); Assert.Equal(1, done.SkillLoadCount); Assert.Equal(new[] { JourneySkill }, done.ActiveSkillKeys);
                    foreach (var request in model.Requests)
                    {
                        Assert.NotNull(request.ContextManifest); Assert.Equal(ContextSectionId.OperatingContract, request.Context!.Sections[0].Id);
                        Assert.Equal(request.Context.Sections.Count, request.Context.Sections.Select(s => s.Id).Distinct().Count());
                        Assert.DoesNotContain(request.Messages.Select(m => m.Role).SkipWhile(r => r is ModelRole.System or ModelRole.Developer), r => r is ModelRole.System or ModelRole.Developer);
                        Assert.Equal(1, request.Messages.Count(m => m.Role == ModelRole.User && m.Text == JourneyInput));
                        Assert.DoesNotContain(request.Messages.Where(m => m.Role == ModelRole.System), m => m.Text.Contains(JourneyProcedure));
                    }
                    var requests = model.Requests.ToArray();
                    foreach (var request in requests.Take(3))
                    {
                        var text = string.Join("\n", request.Messages.Select(m => m.Text)); Assert.DoesNotContain(JourneyProcedure, text); Assert.DoesNotContain("UNUSED-ORDER-PROCEDURE", text);
                    }
                    Assert.Contains("key: instance:journey-order", string.Join("\n", requests[0].Messages.Select(m => m.Text)));
                    Assert.DoesNotContain("UNUSED-ORDER-PROCEDURE", string.Join("\n", requests[3].Messages.Select(m => m.Text)));
                    Assert.True(AgentRunToolCallCheckpoint.TryRead(done.Checkpoint, out var checkpoint)); Assert.DoesNotContain(checkpoint!, m => m.Role is ModelRole.System or ModelRole.Developer);
                    using var checkpointJson = JsonDocument.Parse(done.Checkpoint!.PayloadJson); Assert.Equal(AgentRunToolCallCheckpoint.FormatVersion, checkpointJson.RootElement.GetProperty("ContextFormatVersion").GetInt32());
                    await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == response));
                    var started = events.Single(e => e.Type == "agent.response.started" && e.ResponseId == response);
                    var receipt = new ClientCommand<ResponseReceiptPayload> { ProtocolVersion = 1, Type = "response.received", EventId = Guid.NewGuid().ToString("D"), SessionId = session.ToString("D"), AttachmentId = started.AttachmentId, ResponseId = response, Sequence = 1,
                        Payload = new() { TextEndExclusive = JourneyAnswer.Length, BlockIds = [] } };
                    var ack = await hub.InvokeAsync<CommandAck>("ResponseReceived", receipt); Assert.True(ack.Accepted, ack.Error?.Message);
                    initialHistory = (await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items");
                    var application = Assert.Single(initialHistory.EnumerateArray(), e => e.GetProperty("role").GetString() == "applicationMessage");
                    Assert.Equal(JourneyNotice, application.GetProperty("text").GetString()); Assert.Equal(response, application.GetProperty("responseId").GetString());
                    Assert.Equal(0, application.GetProperty("heardTextEndExclusive").GetInt32()); Assert.Equal(JourneyNotice.Length, application.GetProperty("receivedTextEndExclusive").GetInt32()); Assert.Empty(application.GetProperty("blocks").EnumerateArray());
                    Assert.Equal(JourneyAnswer, Assert.Single(initialHistory.EnumerateArray(), e => e.GetProperty("role").GetString() == "assistant").GetProperty("text").GetString());
                    Assert.DoesNotContain(events, e => e.Type is "speech.output.segment" or "speech.output.completed");
                }
                Assert.Equal(0, first.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
            }
            var restoredModel = sqlite ? new OwnedMessagingSkillModel() : model;
            if (sqlite) { await first.DisposeAsync(); first = null; second = new NativeTenantSqliteFactory(root, restoredModel); }
            var host = second ?? first!; using var again = host.CreateClient(); await Login(again, "owner@example.test", "explicit-synthetic-test-password");
            var retained = (await Json(await again.GetAsync($"/api/v2/tenants/{tenant}/sessions/{session}/messages"))).GetProperty("items");
            Assert.Equal(initialHistory.GetRawText(), retained.GetRawText());
            Assert.Equal(sqlite ? 0 : 4, restoredModel.Requests.Count);
            var savedRun = (await host.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, runId))!;
            Assert.Equal(AgentRunStatus.Completed, savedRun.Status); Assert.Equal(1, savedRun.SkillLoadCount); Assert.Equal(new[] { JourneySkill }, savedRun.ActiveSkillKeys);
            Assert.Equal(0, host.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally
        {
            if (second is not null) await second.DisposeAsync(); if (first is not null) await first.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
