using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Realtime;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantSessionAccessTests
{
    private sealed class TenantPrefixModel : ILanguageModel
    {
        private readonly ScriptedLanguageModel inner = new();
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModelCapabilities Capabilities => inner.Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var held = false;
            await foreach (var item in inner.GenerateAsync(request, cancellationToken))
            {
                if (!held && item is ModelTextDelta delta)
                {
                    held = true;
                    var start = delta.Text.IndexOf("\"displayText\":\"", StringComparison.Ordinal);
                    var split = start >= 0 ? start + "\"displayText\":\"".Length + 5 : delta.Text.Length;
                    yield return new ModelTextDelta(delta.Text[..split]);
                    await Release.Task; // Deliberately noncooperative: late output must be fenced independently.
                    Returned.TrySetResult();
                    if (split < delta.Text.Length) yield return new ModelTextDelta(delta.Text[split..]);
                }
                else yield return item;
            }
        }
    }
    private static ClientCommand<TenantAttachPayload> TenantAttach(Guid tenant, Guid session) => new() {
        ProtocolVersion = 1, EventId = Guid.NewGuid().ToString("D"), SessionId = session.ToString("D"), Sequence = 0,
        Type = "session.attach", Payload = new() { TenantId = tenant.ToString("D") }
    };
    private static HubConnection TenantSocket(Uri address, CookieContainer cookies, ConcurrentQueue<ServerEvent> events)
    {
        var hub = new HubConnectionBuilder().WithUrl(new Uri(address, "/hubs/session"), options => {
            options.SkipNegotiation = true; options.Transports = HttpTransportType.WebSockets; options.Cookies = cookies;
            options.Headers["Origin"] = address.GetLeftPart(UriPartial.Authority);
        }).AddMessagePackProtocol().Build();
        hub.On<ServerEvent>("SessionEvent", events.Enqueue); return hub;
    }
    private static async Task<T> AwaitTenantWire<T>(Func<T?> read) where T : class
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true) { if (read() is { } value) return value; await Task.Delay(25, deadline.Token); }
    }
    [Theory]
    [InlineData(false, "resume")] [InlineData(true, "resume")]
    [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "cancel-revoke")] [InlineData(true, "cancel-revoke")]
    public async Task Tenant_live_reconnect_and_authority_boundaries_preserve_contiguous_prefix_and_exclude_late_output(bool sqlite, string boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-live-boundary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var model = new TenantPrefixModel();
        try
        {
            await using WebApplicationFactory<Program> factory = sqlite ? new NativeTenantSqliteFactory(root, model) : new NativeTenantMemoryFactory(root, model);
            factory.UseKestrel(0); using var ready = factory.CreateClient();
            try
            {
                var ownerCookies = new CookieContainer(); using var owner = new HttpClient(new HttpClientHandler { CookieContainer = ownerCookies }) { BaseAddress = ready.BaseAddress };
                await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
                var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var api = $"/api/v2/tenants/{tenant}";
                var principal = (await Json(await owner.GetAsync(api + "/access/me"))).GetProperty("principalId").GetGuid();
                var instance = (await Json(await owner.PostAsJsonAsync(api + "/agents", new { templateId = "examiner", version = 1, expectedRevision = await Revision(owner, tenant) })))
                    .GetProperty("instance").GetProperty("instanceId").GetGuid();
                await GrantNativePrivateSpaceRead(owner, tenant, instance);
                var session = (await Json(await owner.PostAsJsonAsync(api + "/sessions", new { retentionKind = "saved", agentInstanceId = instance, expectedRevision = await Revision(owner, tenant), expectedInstanceRevision = 1 }))).GetProperty("sessionId").GetGuid();
                var identity = factory.Services.GetRequiredService<IdentityService>(); var access = factory.Services.GetRequiredService<TenantAccessService>();
                var account = await identity.RegisterAsync("recipient@example.test", "Recipient", "strong-recipient-password");
                var role = await access.SaveRoleAsync(principal, tenant, null, "Read current conversation", boundary == "cancel-revoke"
                    ? ["sessions.read", "agents.chat", "agent-runs.read", "agent-runs.cancel"] : ["sessions.read", "agents.chat"], await Revision(owner, tenant));
                var invitation = await access.InviteAsync(principal, tenant, account.LoginKey, role.RoleId, await Revision(owner, tenant));
                await access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
                var recipientCookies = new CookieContainer(); using var recipient = new HttpClient(new HttpClientHandler { CookieContainer = recipientCookies }) { BaseAddress = ready.BaseAddress };
                await Login(recipient, account.LoginKey, "strong-recipient-password");
                var events = new ConcurrentQueue<ServerEvent>(); await using var hub = TenantSocket(ready.BaseAddress!, recipientCookies, events); await hub.StartAsync();
                Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
                var receipt = await Json(await owner.PostAsJsonAsync(api + $"/sessions/{session}/messages", new { messageId = Guid.NewGuid(), text = "Hello gated tenant stream", expectedRevision = await Revision(owner, tenant), expectedSessionRevision = 1 }));
                var response = receipt.GetProperty("responseId").GetGuid(); var runId = receipt.GetProperty("runId").GetGuid();
                var first = await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.text.delta"));
                Assert.Equal("Hello", first.Payload["text"]); Assert.Equal(0, Convert.ToInt64(first.Payload["textStart"]));
                string? renewed = null; var after = events.Count;
                if (boundary == "resume")
                {
                    Assert.True((await hub.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
                    var snapshot = await AwaitTenantWire(() => events.Skip(after).FirstOrDefault(e => e.Type == "session.ready")); renewed = snapshot.AttachmentId;
                    Assert.NotEqual(first.AttachmentId, renewed); Assert.Equal(1, snapshot.Sequence);
                    using var body = JsonDocument.Parse(JsonSerializer.Serialize(snapshot.Payload));
                    var assistant = Assert.Single(body.RootElement.GetProperty("history").EnumerateArray(), e => e.GetProperty("role").GetString() == "assistant");
                    Assert.Equal("Hello", assistant.GetProperty("text").GetString()); Assert.Equal(0, assistant.GetProperty("receivedTextEndExclusive").GetInt32());
                    Assert.Equal(response.ToString("D"), body.RootElement.GetProperty("activeResponseId").GetString()); after = events.Count;
                }
                if (boundary == "revoke") await access.SaveRoleAsync(principal, tenant, role.RoleId, role.Name, ["sessions.read"], await Revision(owner, tenant));
                if (boundary == "logout") Assert.Equal(HttpStatusCode.NoContent, (await recipient.PostAsync("/api/v2/auth/logout", null)).StatusCode);
                if (boundary is "cancel" or "cancel-revoke")
                {
                    var active = (await factory.Services.GetRequiredService<ITenantRunStore>().FindTenantRunAsync(tenant, runId))!;
                    ClientCommand<CancelResponsePayload> Stop(string attachment) => new() { ProtocolVersion = 1, SessionId = session.ToString("D"),
                        EventId = Guid.NewGuid().ToString("D"), Sequence = 1, AttachmentId = attachment, ResponseId = response.ToString("D"), Type = "agent.response.cancel",
                        Payload = new() { ExpectedRunRevision = active.Revision } };
                    var unauthorized = Stop(first.AttachmentId!); unauthorized.Payload.ExpectedRevision = await Revision(owner, tenant);
                    if (boundary == "cancel") Assert.False((await hub.InvokeAsync<CommandAck>("CancelResponse", unauthorized)).Accepted);
                    var ownerEvents = new ConcurrentQueue<ServerEvent>(); await using var controller = TenantSocket(ready.BaseAddress!, ownerCookies, ownerEvents);
                    await controller.StartAsync(); Assert.True((await controller.InvokeAsync<CommandAck>("AttachTenant", TenantAttach(tenant, session))).Accepted);
                    var attached = await AwaitTenantWire(() => ownerEvents.LastOrDefault(e => e.Type == "session.ready"));
                    var stop = Stop(boundary == "cancel" ? attached.AttachmentId! : first.AttachmentId!); stop.Payload.ExpectedRevision = await Revision(owner, tenant);
                    var actualController = boundary == "cancel" ? controller : hub;
                    var cancelled = await actualController.InvokeAsync<CommandAck>("CancelResponse", stop);
                    Assert.True(cancelled.Accepted, cancelled.Error?.Code); Assert.Equal("cancelled", cancelled.Result!["status"]);
                    Assert.True((await actualController.InvokeAsync<CommandAck>("CancelResponse", stop)).Accepted);
                    if (boundary == "cancel-revoke")
                    {
                        await access.SaveRoleAsync(principal, tenant, role.RoleId, role.Name, ["sessions.read", "agents.chat", "agent-runs.read"], await Revision(owner, tenant));
                        Assert.False((await actualController.InvokeAsync<CommandAck>("CancelResponse", stop)).Accepted);
                    }
                    Assert.Single(await factory.Services.GetRequiredService<IIamStore>().ReadAuditAsync(tenant, 100), a => a.Action == "agent-runs.cancel");
                    await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.interrupted" && e.ResponseId == response.ToString("D")));
                    var saved = await factory.Services.GetRequiredService<ITenantConversationStore>().ReadTenantConversationAsync(tenant, session, null, null, 50);
                    var interrupted = Assert.Single(saved!.History.Items, e => e.ResponseId == response && e.Role == ConversationRole.Assistant);
                    Assert.Equal("Hello", interrupted.Text); Assert.Equal(EntryStatus.Interrupted, interrupted.Status);
                    Assert.Equal(0, interrupted.ReceivedTextEndExclusive);
                    after = events.Count;
                }
                model.Release.TrySetResult(); await model.Returned.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var final = await AwaitCancelledTenantRun(factory.Services.GetRequiredService<ITenantRunStore>(), tenant, runId);
                Assert.Equal(boundary is "cancel" or "cancel-revoke" ? AgentRunStatus.Cancelled : AgentRunStatus.Completed, final.Status);
                if (boundary == "resume")
                {
                    await AwaitTenantWire(() => events.FirstOrDefault(e => e.Type == "agent.response.completed" && e.ResponseId == response.ToString("D")));
                    var tail = events.Skip(after).Where(e => e.Type == "agent.text.delta").ToArray(); Assert.NotEmpty(tail);
                    var text = "Hello";
                    foreach (var chunk in tail) { Assert.Equal(renewed, chunk.AttachmentId); Assert.Equal(text.Length, Convert.ToInt64(chunk.Payload["textStart"])); text += (string)chunk.Payload["text"]!; }
                    Assert.Equal("Hello from synthetic.", text);
                }
                else Assert.DoesNotContain(events.Skip(after), e => e.Type is "agent.text.delta" or "agent.response.completed");
                var authenticated = (await identity.SignInAsync("owner@example.test", "explicit-synthetic-test-password"))!;
                var stored = await factory.Services.GetRequiredService<TenantConversationService>().ReadAsync(tenant, session,
                    new(authenticated.Account.AccountId, authenticated.Session.SessionId, principal));
                Assert.Equal(0, stored.Items.Last(e => e.ResponseId == response).ReceivedTextEndExclusive);
            }
            finally { model.Release.TrySetResult(); }
        }
        finally { model.Release.TrySetResult(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
