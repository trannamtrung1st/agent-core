using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    [Theory]
    [InlineData(false, "complete")] [InlineData(true, "complete")]
    [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "queued-revoke")] [InlineData(true, "queued-revoke")]
    [InlineData(false, "queued-logout")] [InlineData(true, "queued-logout")]
    [InlineData(false, "admission-race")] [InlineData(true, "admission-race")]
    public async Task Tenant_text_receipts_are_attachment_bounded_durable_and_never_credit_heard_or_revoked_recipients(bool sqlite, string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-receipts-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var model = new TenantPrefixModel(); var gate = new TenantReceiptRaceStore();
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model, gate.Install) : new NativeTenantMemoryFactory(root, model, gate.Install);
            factory.UseKestrel(0); using var ready = factory.CreateClient();
            try
            {
                var cookies = new CookieContainer(); using var owner = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = ready.BaseAddress };
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
                var principal = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
                var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) })))
                    .GetProperty("instance").GetProperty("instanceId").GetGuid();
                await GrantNativePrivateSpaceRead(owner, tenant, instance);
                var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
                var identity = factory.Services.GetRequiredService<IdentityService>(); var access = factory.Services.GetRequiredService<TenantAccessService>();
                var account = await identity.RegisterAsync("receipt-reader@example.test", "Receipt reader", "strong-receipt-password");
                var role = await access.SaveRoleAsync(principal, tenant, null, "Receive conversation", ["sessions.read", "agents.chat"], await Revision(owner, tenant));
                var invitation = await access.InviteAsync(principal, tenant, account.LoginKey, role.RoleId, await Revision(owner, tenant));
                await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
                var readerCookies = new CookieContainer(); using var reader = new HttpClient(new HttpClientHandler { CookieContainer = readerCookies }) { BaseAddress = ready.BaseAddress };
                await Login(reader, account.LoginKey, "strong-receipt-password");
                var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, readerCookies, events); await hub.StartAsync();
                Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
                var admitted = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Hello receipt stream", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
                var response = admitted.GetProperty("responseId").GetGuid(); var run = admitted.GetProperty("runId").GetGuid();
                var first = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.text.delta")); Assert.Equal("Hello", first.Payload["text"]);
                long sequence = 0; var attachment = first.AttachmentId!;
                ClientCommand<ResponseReceiptPayload> Receipt(int end, string[]? blocks = null) => new() { ProtocolVersion = 1, Type = "response.received",
                    EventId = Guid.NewGuid().ToString("D"), SessionId = session.ToString("D"), AttachmentId = attachment, ResponseId = response.ToString("D"),
                    Sequence = ++sequence, Payload = new() { TextEndExclusive = end, BlockIds = blocks } };
                var content = factory.Services.GetRequiredService<ITenantConversationStore>();
                async Task<ConversationEntry> Saved() => (await content.ReadTenantConversationAsync(tenant, session, null, null, 50))!.History.Items.Single(e => e.ResponseId == response);
                async Task Receive(ClientCommand<ResponseReceiptPayload> command, bool accepted)
                { var ack = await hub.InvokeAsync<CommandAck>("ResponseReceived", command); Assert.True(accepted == ack.Accepted, $"Expected accepted={accepted}; actual={ack.Accepted}; reason={ack.Error?.Code}"); }
                foreach (var invalid in new[] { Receipt(-1), Receipt(6), Receipt(5, ["unsent-block"]) }) await Receive(invalid, false);
                var unknown = Receipt(1); unknown.ResponseId = Guid.NewGuid().ToString("D"); await Receive(unknown, false);
                var confirmed = Receipt(5);
                if (boundary is "queued-revoke" or "queued-logout")
                {
                    gate.HoldReceipt = true; var pending = Receive(confirmed, false);
                    await gate.ReceiptEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    if (boundary == "queued-revoke") await access.SaveRoleAsync(principal, tenant, role.RoleId, role.Name, ["sessions.read"], await Revision(owner, tenant));
                    else Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsync("/api/v2/auth/logout", null)).StatusCode);
                    gate.ReleaseReceipt.TrySetResult(); await pending;
                    model.Release.TrySetResult(); await model.Returned.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.Equal(AgentRunStatus.Completed, (await AwaitCancelledTenantRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, run)).Status);
                    Assert.Equal(0, (await Saved()).ReceivedTextEndExclusive); Assert.Equal(0, (await Saved()).HeardTextEndExclusive);
                    return;
                }
                await Receive(confirmed, true);
                Assert.Equal(5, (await Saved()).ReceivedTextEndExclusive); Assert.Equal(0, (await Saved()).HeardTextEndExclusive);
                var rev = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!.Snapshot.Revision;
                await Receive(confirmed, true);
                Assert.Equal(rev, (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!.Snapshot.Revision);
                await Receive(Receipt(4), false);
                if (boundary is "complete" or "admission-race")
                {
                    Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
                    var resumed = await AwaitTenantWire(() => events.LastOrDefault(e => e.Type == "session.ready" && e.AttachmentId != attachment));
                    var stale = Receipt(5); await Receive(stale, false); attachment = resumed.AttachmentId!; sequence = 0;
                    // A second attachment may receive less than the Session aggregate, without regressing it.
                    await Receive(Receipt(2), true); Assert.Equal(5, (await Saved()).ReceivedTextEndExclusive);
                }
                if (boundary == "revoke") await access.SaveRoleAsync(principal, tenant, role.RoleId, role.Name, ["sessions.read"], await Revision(owner, tenant));
                if (boundary == "logout") Assert.Equal(HttpStatusCode.NoContent, (await reader.PostAsync("/api/v2/auth/logout", null)).StatusCode);
                if (boundary is "revoke" or "logout") { await Receive(confirmed, false); await Receive(Receipt(5), false); }
                if (boundary == "cancel")
                {
                    var active = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, run))!;
                    Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(api + $"/agent-runs/{run}/cancel", new { expectedRevision = await Revision(owner, tenant), expectedRunRevision = active.Revision })).StatusCode);
                    await Receive(Receipt(5), false);
                }
                model.Release.TrySetResult(); await model.Returned.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var final = await AwaitCancelledTenantRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, run);
                Assert.Equal(boundary == "cancel" ? AgentRunStatus.Cancelled : AgentRunStatus.Completed, final.Status);
                if (boundary is "complete" or "admission-race")
                {
                    await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed"));
                    if (boundary == "complete")
                    {
                        var full = Receipt("Hello from synthetic.".Length); await Receive(full, true);
                        Assert.Equal("Hello from synthetic.".Length, (await Saved()).ReceivedTextEndExclusive);
                        await Receive(full, true);
                    }
                    // Reattach a completed history projection and acknowledge without any active output source.
                    var beforeCompletedAttach = events.Count;
                    Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
                    var completed = await AwaitTenantWire(() => events.Skip(beforeCompletedAttach).FirstOrDefault(e => e.Type == "session.ready"));
                    attachment = completed.AttachmentId!; sequence = 0;
                    Guid? following = null;
                    if (boundary == "admission-race")
                    {
                        gate.HoldNextSnapshot();
                        var savedSession = (await factory.Services.GetRequiredService<ITenantSessionStore>().FindTenantSessionAsync(tenant, session))!;
                        following = (await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Following turn", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = savedSession.Snapshot.Revision }))).GetProperty("runId").GetGuid();
                        await gate.SnapshotLoaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    }
                    await Receive(Receipt("Hello from synthetic.".Length), true);
                    if (following is { } next)
                    {
                        gate.ReleaseSnapshot.TrySetResult();
                        Assert.Equal(AgentRunStatus.Completed, (await AwaitCancelledTenantRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, next)).Status);
                        Assert.True(gate.LoadsAfterArm >= 2, "Admission must discard the snapshot loaded before the quiescent receipt.");
                        Assert.Equal("Hello from synthetic.".Length, (await Saved()).ReceivedTextEndExclusive);
                    }
                }
                else Assert.Equal(5, (await Saved()).ReceivedTextEndExclusive);
                Assert.Equal(0, (await Saved()).HeardTextEndExclusive);
            }
            finally { model.Release.TrySetResult(); gate.ReleaseReceipt.TrySetResult(); gate.ReleaseSnapshot.TrySetResult(); }
        }
        finally { model.Release.TrySetResult(); gate.ReleaseReceipt.TrySetResult(); gate.ReleaseSnapshot.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
