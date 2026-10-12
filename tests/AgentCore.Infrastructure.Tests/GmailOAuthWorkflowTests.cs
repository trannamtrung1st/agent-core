using System.Net;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Credentials;
using AgentCore.Application.Email;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Email;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class GmailOAuthWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connected_account_restores_refreshes_reads_exactly_sends_once_and_disconnects_every_binding(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var c = await f.Credentials.CreateEmailAccountAsync("Gmail", EmailAccountProfile.Gmail(), ""); await f.Connect(c.CredentialId, 1);
        var a = await f.Bind(f.A, c.CredentialId, "primary"); var b = await f.Bind(f.B, c.CredentialId, "secondary");
        var available = Assert.Single(await f.Email.AccountsAsync(a, default)); Assert.Equal("GoogleOAuth2", available.Authentication); Assert.True(available.SendAvailable);
        var account = await f.Email.ResolveAsync(a, "primary", default);
        var results = await f.Email.Provider(account).SearchAsync(new("", 5)); var message = await f.Email.Provider(account).ReadAsync(new(Assert.Single(results.Results).MessageId)); Assert.Equal("Safe plain body", message.Body);
        var session = Guid.NewGuid(); var draft = await f.Email.CreateDraftAsync(session, a, "primary", new(["to@example.test"], ["cc@example.test"], ["hidden@example.test"], "Exact subject", "Exact body"), default);
        var hash = EmailDraftNormalizer.ComputeSendActionHash(draft); var before = (await f.Store.GetAsync(c.CredentialId))!;
        f.Reopen(); await f.OAuth.AccessTokenAsync(c.CredentialId, before.Revision, default);
        var after = (await f.Store.GetAsync(c.CredentialId))!; Assert.Equal(before.Revision, after.Revision); Assert.True(after.SecretRevision > before.SecretRevision);
        Assert.Equal(hash, EmailDraftNormalizer.ComputeSendActionHash(await f.Email.DraftAsync(session, a, draft.DraftId, "primary", default)));
        var grant = new ToolApprovalGrant(Guid.NewGuid(), ToolCatalog.EmailSend, hash, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(EmailSendOutcome.Sent, (await f.Email.SendAsync(session, a, draft.DraftId, "primary", grant, default)).Outcome);
        f.Reopen(); Assert.Equal(EmailSendOutcome.Sent, (await f.Email.SendAsync(session, a, draft.DraftId, "primary", grant, default)).Outcome); Assert.Equal(1, f.Http.Sends);
        Assert.Contains("hidden@example.test", f.Http.Mime!); Assert.Contains("From: verified@example.test", f.Http.Mime!);
        var current = (await f.Store.GetAsync(c.CredentialId))!; await f.OAuth.DisconnectAsync(c.CredentialId, current.Revision, default);
        Assert.Empty(await f.Email.AccountsAsync(a, default)); Assert.Empty(await f.Email.AccountsAsync(b, default));
        Assert.Equal(2, (await f.Bindings.ListBindingsAsync()).Count);
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Email.Provider(account).ReadAsync(new(message.MessageId)).AsTask());
        Assert.Equal(EmailSendOutcome.Sent, (await f.MailStore.GetSendAsync(draft.DraftId))!.Result!.Outcome);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Auth_failure_and_host_disable_before_dispatch_leave_immutable_draft_unclaimed(bool disable)
    {
        await using var f = await Fixture.Create(true); var c = await f.Credentials.CreateEmailAccountAsync("Gmail", EmailAccountProfile.Gmail(), ""); await f.Connect(c.CredentialId, 1);
        var a = await f.Bind(f.A, c.CredentialId, "gmail"); var session = Guid.NewGuid(); var draft = await f.Email.CreateDraftAsync(session, a, "gmail", new(["to@example.test"], [], [], "subject", "body"), default);
        await f.Email.Provider(await f.Email.ResolveAsync(a, "gmail", default)).SearchAsync(new("", 1));
        if (disable) f.Options.SendEnabled = false; else f.Http.InvalidGrant = true;
        var grant = new ToolApprovalGrant(Guid.NewGuid(), ToolCatalog.EmailSend, EmailDraftNormalizer.ComputeSendActionHash(draft), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Email.SendAsync(session, a, draft.DraftId, "gmail", grant, default).AsTask()); Assert.Null(await f.MailStore.GetSendAsync(draft.DraftId)); Assert.Equal(0, f.Http.Sends);
    }
    [Fact]
    public async Task Provider_revocation_of_a_cached_token_blocks_reads_and_requires_reconnect()
    {
        await using var f = await Fixture.Create(true); var c = await f.Credentials.CreateEmailAccountAsync("Gmail", EmailAccountProfile.Gmail(), ""); await f.Connect(c.CredentialId, 1);
        var a = await f.Bind(f.A, c.CredentialId, "gmail"); var account = await f.Email.ResolveAsync(a, "gmail", default);
        await f.Email.Provider(account).SearchAsync(new("", 1)); f.Http.InvalidGrant = true;
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => f.Email.Provider(account).SearchAsync(new("", 1)).AsTask());
        Assert.Equal("email_oauth_reconnect_required", error.Code);
        Assert.Equal("ReconnectRequired", (await f.OAuth.ConnectionAsync(c.CredentialId, default)).Status);
        Assert.Empty(await f.Email.AccountsAsync(a, default)); Assert.Equal("", (await f.Store.GetAsync(c.CredentialId))!.ProtectedPayload);
    }
    [Fact]
    public void Legacy_Custom_approval_hash_is_preserved_and_OAuth_epoch_is_bound()
    {
        var draft = new EmailDraftSnapshot("legacy-custom-draft", ["to@example.test"], [], ["hidden@example.test"], "Subject", "Body",
            Guid.Parse("11111111-1111-1111-1111-111111111111"), "mail", "sender@example.test", 1, 7,
            Guid.Parse("22222222-2222-2222-2222-222222222222"), 2);
        // Frozen canonical hash from the pre-OAuth format on 974159eb.
        Assert.Equal("c8ff714152994ae3a3069803b6a143003e2f9a24a855e8230802f70fe1f700be", EmailDraftNormalizer.ComputeSendActionHash(draft));
        Assert.NotEqual(EmailDraftNormalizer.ComputeSendActionHash(draft with { AuthEpoch = 1 }), EmailDraftNormalizer.ComputeSendActionHash(draft with { AuthEpoch = 2 }));
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "gmail-workflow-" + Guid.NewGuid().ToString("N")); public bool Sqlite;
        public readonly FakeHttp Http = new(); public readonly EmailTransportOptions Options = new();
        public readonly AgentInstance A = Instance(), B = Instance(); public readonly Guid Owner = Guid.NewGuid();
        public IAgentInstanceStore Instances = null!; public ICredentialStore Store = null!; public IAgentCredentialBindingStore Bindings = null!; public IEmailStore MailStore = null!;
        public CredentialService Credentials = null!; public GoogleEmailOAuthService OAuth = null!; public EmailAccountService Email = null!;
        public static async Task<Fixture> Create(bool sqlite) { var f = new Fixture { Sqlite = sqlite }; Directory.CreateDirectory(f.Root); if (sqlite) { await using var db = new Factory(f.Root).CreateDbContext(); await db.Database.MigrateAsync(); } f.Reopen(); await f.Instances.InsertAsync(f.A); await f.Instances.InsertAsync(f.B); return f; }
        public void Reopen()
        {
            var ids = new SystemIdGenerator(TimeProvider.System); var keys = new LocalCredentialProtector(Root + "/keys");
            if (Sqlite) { var contexts = new Factory(Root); Instances = new SqliteAgentInstanceStore(contexts, ids); var store = new SqliteCredentialStore(contexts); Store = store; Bindings = store; MailStore = new SqliteEmailStore(contexts); }
            else if (Store is null) { var instances = new InMemoryAgentInstanceStore(); Instances = instances; var store = new InMemoryCredentialStore(instances); Store = store; Bindings = store; MailStore = new InMemoryEmailStore(); }
            Credentials = new(Store, Bindings, keys, Instances, ids, TimeProvider.System);
            OAuth = new(Store, keys, Http, new() { ClientId = "client", ClientSecret = "protected-client", PublicAppUrl = "https://app.example.test" }, TimeProvider.System, false, new Verifier());
            Email = new(Store, Bindings, Instances, new AccountEmailProviderFactory(Store, Bindings, keys, Instances, Options, TimeProvider.System, false, OAuth, Http), MailStore, ids, TimeProvider.System);
        }
        public async Task Connect(Guid id, long revision) { var start = await OAuth.StartAsync(id, revision, Owner, default); var state = QueryHelpers.ParseQuery(new Uri(start.AuthorizationUrl).Query)["state"].ToString(); await OAuth.CompleteAsync(state, start.Correlation, Owner, "code", null, default); }
        public async Task<ToolExecutionAdmission> Bind(AgentInstance owner, Guid id, string alias) { await Credentials.BindAsync(owner.InstanceId, id, alias, (await Instances.FindAsync(owner.InstanceId))!.Revision); return new(false, TriggerKind.UserTurn, AgentInstanceId: owner.InstanceId, PinnedCredentialBindings: await Bindings.ListBindingsAsync(owner.InstanceId)); }
        public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); return ValueTask.CompletedTask; }
        private static AgentInstance Instance() => new(Guid.NewGuid(), "general-assistant", 25, new("Test", "Assistant", "Test", "neutral"), AgentInstanceLifecycle.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }
    private sealed class Factory(string root) : IDbContextFactory<AgentCoreDbContext> { public AgentCoreDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + root + "/email.db").Options); }
    private sealed class Verifier : IGoogleEmailIdentityVerifier { public Task<GoogleEmailIdentity> VerifyAsync(string token, string client, string nonce, CancellationToken ct) => Task.FromResult(new GoogleEmailIdentity("verified-sub", "verified@example.test")); }
    private sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
    {
        public int Sends; public bool InvalidGrant; public string? Mime;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/token") { var body = await request.Content!.ReadAsStringAsync(ct); if (InvalidGrant && body.Contains("grant_type=refresh_token")) return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest); return Json(new { access_token = "known-access", refresh_token = "known-refresh", id_token = "known-id", expires_in = 3600, scope = EmailAccountProfile.ReadScope + " " + EmailAccountProfile.SendScope }); }
            if (path == "/revoke") return Json(new { });
            if (path.EndsWith("/profile")) return Json(new { emailAddress = "verified@example.test" });
            if (InvalidGrant) return Json(new { error = "invalidCredentials" }, HttpStatusCode.Unauthorized);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme); Assert.Equal("known-access", request.Headers.Authorization.Parameter);
            if (path.EndsWith("/messages/send")) { Sends++; using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); Mime = Encoding.UTF8.GetString(GmailMime.DecodeBase64Url(json.RootElement.GetProperty("raw").GetString()!)); return Json(new { id = "accepted-message" }); }
            if (path.EndsWith("/messages")) return Json(new { messages = new[] { new { id = "m1" } } });
            return Json(new { id = "m1", threadId = "t1", payload = new { mimeType = "text/plain", headers = new[] { new { name = "Subject", value = "Subject" }, new { name = "From", value = "sender@example.test" } }, body = new { data = GmailMime.EncodeBase64Url(Encoding.UTF8.GetBytes("Safe plain body")) } } });
        }
        private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(body)) };
    }
}
