using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using AgentCore.Api.Security;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory] [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, false)] [InlineData(true, false, false)]
    [InlineData(false, true, true)] [InlineData(true, true, true)]
    public async Task Tenant_Artifact_reference_is_recipient_scoped_and_receipted_into_exact_owned_history(bool sqlite, bool agentRead, bool reviewRead)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-artifact-references-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var proof = !sqlite && agentRead && !reviewRead && System.Environment.GetEnvironmentVariable("AGENTCORE_ARTIFACT_DELIVERY_UI_PROOF") == "1";
        const string proofPath = "/private/tmp/agent-core-artifact-delivery-ui";
        try
        {
            var content = "# Delivered exact file\nUTF-8: thử nghiệm\n<script>alert('literal')</script>" + (proof ? "\n" + string.Join("\n", Enumerable.Range(1, 35).Select(i => $"{i}: Exact operations handover — thử nghiệm")) : "");
            var model = new ApprovalRecordingModel { ProposedArtifact = System.Text.Json.JsonSerializer.Serialize(new { displayName = proof ? "Operations handover — exact customer support delivery report with a deliberately long readable file name.md" : "delivered.md", content }), EmitArtifactReference = true };
            void Configure(IServiceCollection services)
            { if (proof) ((IdentityHttpOptions)services.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowedOrigins = ["http://127.0.0.1:5217"]; }
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, Configure) : new NativeTenantMemoryFactory(root, model, Configure);
            factory.UseKestrel(0); using var ready = factory.CreateClient();
            var cookies = new CookieContainer(); using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var human = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var agent = await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "customer-support", version = 3, expectedRevision = await Revision(owner, tenant) }));
            var instance = agent.GetProperty("instance").GetProperty("instanceId").GetGuid(); var executor = agent.GetProperty("principalId").GetGuid();
            var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
            var access = factory.Services.GetRequiredService<TenantAccessService>();
            var role = await access.SaveRoleAsync(human, tenant, null, "Publish in this Session", ["sessions.read", "artifacts.create"], await Revision(owner, tenant));
            await access.AssignRoleAsync(human, tenant, executor, role.RoleId, TenantSessionRules.Scope(tenant, session), await Revision(owner, tenant));
            if (agentRead)
            {
                role = await access.SaveRoleAsync(human, tenant, null, "Read tenant Artifacts", ["artifacts.read"], await Revision(owner, tenant));
                await access.AssignRoleAsync(human, tenant, executor, role.RoleId, AccessScope.ForTenant(tenant), await Revision(owner, tenant));
            }
            if (reviewRead) Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(api + "/operation-policies/artifacts.read", new { mode = "always", independentReview = false, expectedRevision = await Revision(owner, tenant), expectedPolicyRevision = 0 })).StatusCode);
            var identity = factory.Services.GetRequiredService<IdentityService>();
            var account = await identity.RegisterAsync("artifact-recipient@example.test", "Artifact recipient", "strong-artifact-test-password");
            var readerRole = await access.SaveRoleAsync(human, tenant, null, "Read conversation only", ["sessions.read", "agents.chat"], await Revision(owner, tenant));
            var invitation = await access.InviteAsync(human, tenant, account.LoginKey, readerRole.RoleId, await Revision(owner, tenant));
            var recipient = await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
            var readerCookies = new CookieContainer(); using var reader = new HttpClient(new HttpClientHandler { CookieContainer = readerCookies }) { BaseAddress = ready.BaseAddress };
            await Login(reader, account.LoginKey, "strong-artifact-test-password");
            var readerEvents = new ConcurrentQueue<ServerEvent>(); await using var readerHub = TenantSocket(ready.BaseAddress!, readerCookies, readerEvents); await readerHub.StartAsync();
            Assert.True((await readerHub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var sent = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Publish and show this exact file", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
            var response = sent.GetProperty("responseId").GetGuid();
            var block = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.block.upsert"));
            var readable = agentRead && !reviewRead;
            Assert.Equal(readable ? "artifact" : "unknown", block.Payload["kind"]);
            var files = (await Json(await owner.GetAsync(api + $"/sessions/{session}/artifacts"))).GetProperty("items").EnumerateArray().ToArray();
            var id = Assert.Single(files).GetProperty("artifactId").GetGuid();
            if (readable) { Assert.Equal(id.ToString("D"), block.Payload["artifactId"]); Assert.Equal("", block.Payload["text"]); }
            else { Assert.Null(block.Payload["artifactId"]); Assert.Equal("[Unavailable artifact]", block.Payload["fallbackText"]); }
            var run = await AwaitBrowserRestartRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, sent.GetProperty("runId").GetGuid(), false);
            Assert.Equal(AgentRunStatus.Completed, run.Status);
            var before = (await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items").EnumerateArray().Single(e => e.GetProperty("role").GetString() == "assistant");
            Assert.Empty(before.GetProperty("blocks").EnumerateArray());
            var receipt = new ClientCommand<ResponseReceiptPayload> { ProtocolVersion = 1, Type = "response.received", EventId = Guid.NewGuid().ToString("D"),
                SessionId = session.ToString("D"), AttachmentId = block.AttachmentId, ResponseId = response.ToString("D"), Sequence = 1,
                Payload = new() { TextEndExclusive = "The file was published.".Length, BlockIds = [block.Payload["blockId"]!.ToString()!] } };
            var ack = await hub.InvokeAsync<CommandAck>("ResponseReceived", receipt); Assert.True(ack.Accepted, ack.Error?.Code);
            var after = (await Json(await owner.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items").EnumerateArray().Single(e => e.GetProperty("role").GetString() == "assistant");
            var savedBlock = Assert.Single(after.GetProperty("blocks").EnumerateArray());
            Assert.Equal(readable ? "artifact" : "unknown", savedBlock.GetProperty("kind").GetString());
            Assert.Equal(readable ? id.ToString("D") : null, savedBlock.GetProperty("artifactId").GetString());
            Assert.Equal(content, await owner.GetStringAsync(api + $"/sessions/{session}/artifacts/{id}/content"));
            if (readable)
            {
                Assert.DoesNotContain(readerEvents, e => e.Type == "agent.block.upsert" && e.Payload.GetValueOrDefault("kind")?.ToString() == "artifact");
                var hidden = (await Json(await reader.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items").EnumerateArray().Single(e => e.GetProperty("role").GetString() == "assistant");
                Assert.Empty(hidden.GetProperty("blocks").EnumerateArray());
                var readRole = await access.SaveRoleAsync(human, tenant, null, "Read the delivered Artifact", ["artifacts.read"], await Revision(owner, tenant));
                var assignment = await access.AssignRoleAsync(human, tenant, recipient.PrincipalId, readRole.RoleId, TenantArtifactRules.Scope(tenant, id), await Revision(owner, tenant));
                Assert.Single((await Json(await reader.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items").EnumerateArray().Single(e => e.GetProperty("role").GetString() == "assistant").GetProperty("blocks").EnumerateArray());
                if (proof)
                {
                    await File.WriteAllTextAsync(proofPath + ".json", System.Text.Json.JsonSerializer.Serialize(new { baseUrl = owner.BaseAddress!.ToString(), tenant, session, artifactId = id }));
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                    while (!File.Exists(proofPath + "-revoke")) await Task.Delay(100, deadline.Token);
                }
                await access.RevokeAssignmentAsync(human, tenant, assignment.AssignmentId, await Revision(owner, tenant));
                Assert.Empty((await Json(await reader.GetAsync(api + $"/sessions/{session}/messages"))).GetProperty("items").EnumerateArray().Single(e => e.GetProperty("role").GetString() == "assistant").GetProperty("blocks").EnumerateArray());
                Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(api + $"/sessions/{session}/artifacts/{id}/content")).StatusCode);
                if (proof)
                {
                    await File.WriteAllTextAsync(proofPath + "-revoked.json", "{\"revoked\":true}");
                    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                    while (!File.Exists(proofPath + "-release")) await Task.Delay(100, deadline.Token);
                }
            }
            Assert.Equal(0, factory.Services.GetRequiredService<AgentCore.Api.Http.OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
