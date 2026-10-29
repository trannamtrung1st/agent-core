using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Http;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Realtime;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_chat_inventory_HTTP_lets_a_scoped_chat_member_select_create_and_talk_without_admin_reads(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-chat-directory-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, new ScriptedLanguageModel()) : new NativeTenantMemoryFactory(root, new ScriptedLanguageModel());
            factory.UseKestrel(0); using var ready = factory.CreateClient();
            using var owner = new HttpClient(new HttpClientHandler { CookieContainer = new() }) { BaseAddress = ready.BaseAddress };
            await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
            var principal = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
            var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }))).GetProperty("instance").GetProperty("instanceId").GetGuid();
            await GrantNativePrivateSpaceRead(owner, tenant, instance);
            await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) }));
            var identity = factory.Services.GetRequiredService<IdentityService>(); var access = factory.Services.GetRequiredService<TenantAccessService>();
            var account = await identity.RegisterAsync("chat-only@example.test", "Chat member", "strong-chat-member-password");
            var role = await access.SaveRoleAsync(principal, tenant, null, "Conversation member", ["sessions.read", "sessions.create"], await Revision(owner, tenant));
            var invitation = await access.InviteAsync(principal, tenant, account.LoginKey, role.RoleId, await Revision(owner, tenant));
            var member = await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
            var cookies = new CookieContainer(); using var reader = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
            await Login(reader, account.LoginKey, "strong-chat-member-password");
            var path = api + "/chat/agents";
            var empty = await Json(await reader.GetAsync(path)); Assert.Equal(0, empty.GetProperty("total").GetInt32());
            var chat = await access.SaveRoleAsync(principal, tenant, null, "Exact Chat", ["agents.chat"], await Revision(owner, tenant));
            var grant = await access.AssignRoleAsync(principal, tenant, member.PrincipalId, chat.RoleId,
                AgentCore.Domain.Security.AccessScope.ForResource(tenant, "agent", instance.ToString("D")), await Revision(owner, tenant));
            var response = await reader.GetAsync(path + $"?page=0&pageSize=1&principalId={principal}"); Assert.True(response.Headers.CacheControl?.NoStore);
            var page = await Json(response); Assert.Equal(1, page.GetProperty("total").GetInt32()); Assert.True(page.GetProperty("canCreateSessions").GetBoolean());
            var selected = Assert.Single(page.GetProperty("items").EnumerateArray()); Assert.Equal(instance, selected.GetProperty("instanceId").GetGuid());
            Assert.Equal(System.Text.Json.JsonValueKind.True, selected.GetProperty("voiceAvailable").ValueKind);
            foreach (var field in new[] { "tone", "settingsOverrides", "executionBudgets", "principalId", "systemIdentityId", "ownerCapability", "definition", "permissions" }) Assert.False(selected.TryGetProperty(field, out _));
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(api + $"/agents/{instance}")).StatusCode);
            Assert.Equal(0, (await Json(await reader.GetAsync(api + "/agents"))).GetProperty("total").GetInt32());
            Assert.Empty((await Json(await reader.GetAsync(path + "?page=1&pageSize=1"))).GetProperty("items").EnumerateArray());
            Assert.Equal(1, (await Json(await reader.GetAsync(path + "?q=exam&pageSize=1"))).GetProperty("total").GetInt32());
            Assert.Equal(0, (await Json(await reader.GetAsync(path + "?q=no-matching-label"))).GetProperty("total").GetInt32());
            Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(path + "?page=-1")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(path + "?pageSize=101")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync(path + "?q=" + new string('x', 201))).StatusCode);
            var created = await Json(await reader.PostAsJsonAsync(api + "/sessions", new { agentInstanceId = selected.GetProperty("instanceId").GetGuid(),
                expectedRevision = page.GetProperty("securityRevision").GetInt64(), expectedInstanceRevision = selected.GetProperty("revision").GetInt64() }));
            var session = created.GetProperty("sessionId").GetGuid(); Assert.Equal(member.PrincipalId, created.GetProperty("createdByPrincipalId").GetGuid());
            Assert.False(created.GetProperty("canManage").GetBoolean());
            var view = await Json(await reader.GetAsync(api + $"/sessions/{session}/conversation"));
            Assert.Equal(session, view.GetProperty("sessionId").GetGuid()); Assert.Equal("Alex", view.GetProperty("agentName").GetString());
            Assert.True(view.TryGetProperty("model", out _)); Assert.False(view.TryGetProperty("definition", out _));
            Assert.False(view.TryGetProperty("principalId", out _)); Assert.False(view.TryGetProperty("ownerCapability", out _));
            var models = await Json(await reader.GetAsync(api + "/chat/models")); Assert.True(models.TryGetProperty("models", out _));
            var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, cookies, events); await hub.StartAsync();
            Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            var attached = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "session.ready"));
            var text = new ClientCommand<UserTextPayload> { ProtocolVersion = 1, Type = "user.text", EventId = Guid.NewGuid().ToString("D"),
                SessionId = session.ToString("D"), AttachmentId = attached.AttachmentId, Sequence = 1, Payload = new() { Text = "Hello from scoped Chat discovery",
                    ExpectedRevision = await Revision(reader, tenant), ExpectedSessionRevision = created.GetProperty("revision").GetInt64() } };
            var sent = await hub.InvokeAsync<CommandAck>("SendText", text); Assert.True(sent.Accepted, sent.Error?.Code);
            await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed"));
            var history = await Json(await reader.GetAsync(api + $"/sessions/{session}/messages"));
            Assert.Contains(history.GetProperty("items").EnumerateArray(), e => e.GetProperty("text").GetString() == "Hello from synthetic.");
            await access.RevokeAssignmentAsync(principal, tenant, grant.AssignmentId, await Revision(owner, tenant));
            Assert.Equal(0, (await Json(await reader.GetAsync(path))).GetProperty("total").GetInt32());
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(api + $"/sessions/{session}/conversation")).StatusCode);
            Assert.False((await hub.InvokeAsync<CommandAck>("SendText", text)).Accepted);
            Assert.False((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
            using var anonymous = new HttpClient { BaseAddress = ready.BaseAddress };
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            anonymous.DefaultRequestHeaders.Add("X-AgentCore-Owner-Capability", "unused-owner-token");
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsync("/api/v2/auth/logout", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await reader.GetAsync(path)).StatusCode);
            Assert.Equal(0, factory.Services.GetRequiredService<OutboundHttpProbe>().Attempts);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
