using System.Net;
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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class EmailAccountIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bound_account_search_read_draft_exact_approval_dispatch_replay_and_reopen(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var account = await f.Credentials.CreateEmailAccountAsync("Demo", EmailAccountProfile.Gmail("sender@example.test"), "never-model-visible");
        var a = await f.Bind(f.A, account, "primary"); var b = await f.Bind(f.B, account, "other");
        Assert.Equal("primary", Assert.Single(await f.Email.AccountsAsync(a, default)).AccountRef);
        Assert.Equal("other", Assert.Single(await f.Email.AccountsAsync(b, default)).AccountRef);
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Email.ResolveAsync(b, "primary", default).AsTask());
        var resolved = await f.Email.ResolveAsync(a, null, default);
        var results = await f.Email.Provider(resolved).SearchAsync(new("harness", 5));
        var message = await f.Email.Provider(resolved).ReadAsync(new(Assert.Single(results.Results).MessageId));
        Assert.Contains("deterministic", message.Body);
        var second = await f.Credentials.CreateEmailAccountAsync("Other", EmailAccountProfile.Gmail("other@example.test"), "other-secret");
        var b2 = await f.Bind(f.B, second, "secondary");
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Email.ResolveAsync(b2, null, default).AsTask());
        var foreign = await f.Email.ResolveAsync(b2, "secondary", default);
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Email.Provider(foreign).ReadAsync(new(message.MessageId)).AsTask());
        var session = Guid.NewGuid(); var body = new string('x', 500) + " exact final line";
        var draft = await f.Email.CreateDraftAsync(session, a, null, new(["to@example.test"], [], ["hidden@example.test"], "Subject", body), default);
        Assert.Equal(account.CredentialId, draft.AccountId); Assert.Equal("sender@example.test", draft.Sender);
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Email.DraftAsync(session, b, draft.DraftId, null, default).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Email.DraftAsync(Guid.NewGuid(), a, draft.DraftId, null, default).AsTask());
        var executor = new SessionToolExecutor(emailAccounts: f.Email, configurationGate: ToolConfigurationGates.AllowAll);
        var preview = await executor.PrepareEmailSendApprovalAsync(JsonSerializer.SerializeToElement(new { draftId = draft.DraftId }), default, a, session);
        Assert.Equal(body, preview.Preparation!.Details["Body"]);
        Assert.Contains("hidden@example.test", preview.Preparation.Details["Bcc"]);
        Assert.Equal("sender@example.test", preview.Preparation.Details["From"]);
        var hash = preview.Preparation.ActionHash;
        Assert.NotEqual(hash, EmailDraftNormalizer.ComputeSendActionHash(draft with { AccountRevision = 2 }));
        Assert.NotEqual(hash, EmailDraftNormalizer.ComputeSendActionHash(draft with { Sender = "forged@example.test" }));
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Email.SendAsync(session, a, draft.DraftId, null, Grant("wrong"), default).AsTask());
        // Distinct approvals race on the same immutable draft, not just the same approval identity.
        var sends = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.Email.SendAsync(session, a, draft.DraftId, null, Grant(hash), default).AsTask()));
        Assert.Contains(sends, s => s.Outcome == EmailSendOutcome.Sent); Assert.Equal(1, f.Factory.Dispatches);
        f.Reopen();
        Assert.Equal(hash, EmailDraftNormalizer.ComputeSendActionHash(await f.Email.DraftAsync(session, a, draft.DraftId, null, default)));
        Assert.Equal(EmailSendOutcome.Sent, (await f.Email.SendAsync(session, a, draft.DraftId, null, Grant(hash), default)).Outcome);
        Assert.Equal(1, f.Factory.Dispatches);
        Assert.DoesNotContain("never-model-visible", JsonSerializer.Serialize(new { results, message, draft, preview }));
        Assert.NotEqual("never-model-visible", (await f.Store.GetAsync(account.CredentialId))!.ProtectedPayload);
        var protector = new LocalCredentialProtector(f.Root + "/keys");
        var c = (await f.Store.GetAsync(account.CredentialId))!;
        Assert.Equal("never-model-visible", protector.Unprotect(c.CredentialId, c.ProtectionVersion, c.ProtectedPayload));
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Credentials.ResolvePasswordAsync(f.A.InstanceId, "primary", "https://mail.google.com").AsTask());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rotation_disable_unbind_and_old_pins_prevent_dispatch_and_unknown_claim_never_retries(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var account = await f.Credentials.CreateEmailAccountAsync("Mail", EmailAccountProfile.Gmail("sender@example.test"), "protected-value");
        var a = await f.Bind(f.A, account, "mail"); var session = Guid.NewGuid();
        async Task<EmailDraftSnapshot> Draft() => await f.Email.CreateDraftAsync(session, a, null, new(["to@example.test"], [], [], "Subject", "Body"), default);
        var draft = await Draft(); var hash = EmailDraftNormalizer.ComputeSendActionHash(draft);
        await f.Credentials.ReplaceAsync(account.CredentialId, 1, "rotated");
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Email.SendAsync(session, a, draft.DraftId, null, Grant(hash), default).AsTask());
        Assert.Equal(0, f.Factory.Dispatches);
        draft = await Draft(); hash = EmailDraftNormalizer.ComputeSendActionHash(draft);
        Assert.True(await f.EmailStore.TryClaimSendAsync(new(draft.DraftId, account.CredentialId, 1, Guid.NewGuid(), hash, null, DateTimeOffset.UtcNow)));
        f.Reopen();
        Assert.Equal(EmailSendOutcome.Indeterminate, (await f.Email.SendAsync(session, a, draft.DraftId, null, Grant(hash), default)).Outcome);
        Assert.Equal(0, f.Factory.Dispatches);
        account = await f.Credentials.GetAsync(account.CredentialId);
        await f.Credentials.UpdateAsync(account.CredentialId, account.Revision, account.DisplayName, "Disabled", account.Metadata, []);
        Assert.Empty(await f.Email.AccountsAsync(a, default));
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Email.SendAsync(session, a, draft.DraftId, null, Grant(hash), default).AsTask());
        account = await f.Credentials.GetAsync(account.CredentialId);
        await f.Credentials.UpdateAsync(account.CredentialId, account.Revision, account.DisplayName, "Active", account.Metadata, []);
        var binding = Assert.Single(await f.Credentials.BindingsAsync(f.A.InstanceId));
        await f.Credentials.UnbindAsync(f.A.InstanceId, binding.BindingId, binding.Revision, (await f.Owners.FindAsync(f.A.InstanceId))!.Revision);
        await f.Bind(f.A, account, "mail");
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Email.ResolveAsync(a, "mail", default).AsTask());
    }
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    public void Prohibited_DNS_answers_are_rejected_even_in_mixed_results(string address) =>
        Assert.Throws<AgentCoreException>(() => MailTransportPolicy.ValidateAddresses([IPAddress.Parse("8.8.8.8"), IPAddress.Parse(address)]));
    [Fact]
    public void Custom_endpoints_require_explicit_allowlist_and_TLS_enums_are_validated()
    {
        var profile = EmailAccountProfile.Gmail("sender@example.test") with { Preset = EmailProviderPreset.Custom, ImapEndpoint = new("mail.example.test", 993, MailTlsMode.ImplicitTls) };
        Assert.Throws<AgentCoreException>(() => new MailTransportPolicy(new()).ValidateEndpoint(profile, false));
        new MailTransportPolicy(new() { AllowedCustomEndpoints = ["mail.example.test:993"] }).ValidateEndpoint(profile, false);
        Assert.Throws<ArgumentException>(() => (profile with { ImapEndpoint = new("mail.example.test", 993, (MailTlsMode)3) }).Validate());
        Assert.Throws<ArgumentException>(() => EmailAccountProfile.Gmail("forged\r\nTo: someone@example.test").Validate());
    }
    [Fact]
    public async Task Upgrade_preserves_existing_general_credentials_and_ciphertext()
    {
        var root = Path.Combine(Path.GetTempPath(), "email-upgrade-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var factory = new DbFactory(root); var id = Guid.NewGuid().ToString("D");
            var previous = new AgentCoreDbContext(factory.Options).Database.GetMigrations().Last(m => !m.EndsWith("AccountScopedEmail"));
            await using (var db = factory.CreateDbContext())
            {
                await db.GetService<IMigrator>().MigrateAsync(previous);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Credentials (CredentialId,DisplayName,Kind,Status,MetadataJson,AllowedOriginsJson,ProtectedPayload,ProtectionVersion,Revision,CreatedAtUtc,UpdatedAtUtc) VALUES ({id},{"existing"},{"Password"},{"Active"},{"{}"},{"[]"},{"unchanged-ciphertext"},{1},{7},{1L},{1L})");
            }
            await using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync();
            var restored = await new SqliteCredentialStore(factory).GetAsync(Guid.Parse(id));
            Assert.Equal(CredentialPurpose.General, restored!.Purpose); Assert.Equal(7, restored.Revision); Assert.Equal("unchanged-ciphertext", restored.ProtectedPayload);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private static ToolApprovalGrant Grant(string hash) => new(Guid.NewGuid(), ToolCatalog.EmailSend, hash, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Malformed_account_alias_returns_safe_tool_and_approval_errors(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var account = await f.Credentials.CreateEmailAccountAsync("Mail", EmailAccountProfile.Gmail("sender@example.test"), "protected-fixture");
        var admission = await f.Bind(f.A, account, "mail"); var session = Guid.NewGuid();
        var draft = await f.Email.CreateDraftAsync(session, admission, "mail", new(["to@example.test"], [], [], "Subject", "Body"), default);
        var definition = new AgentDefinition(1, "general-assistant", 25, new("Test", "Assistant", "Test", "neutral"), [], "instructions",
            new("answerNewTurn", true, true), new("balanced", false, "en", 2048), new(false, 60_000, 120_000, 1, ["longSilence"], 0),
            new(false, "default", 1), new("primary-llm", "primary-stt", "primary-tts"), new Dictionary<string, string>(),
            new(ToolAllowlist: [ToolCatalog.EmailSearch]));
        var executor = new SessionToolExecutor(emailAccounts: f.Email, configurationGate: ToolConfigurationGates.AllowAll);
        var search = await executor.ExecuteAsync(definition, session,
            new("bad-alias", ToolCatalog.EmailSearch, """{"query":"harness","accountRef":"../mail"}"""), ToolLimits.MaxOutputBytes, admission: admission);
        Assert.Contains("Validation", search.Text); Assert.DoesNotContain("protected-fixture", search.Text);
        var approval = await executor.PrepareEmailSendApprovalAsync(JsonSerializer.SerializeToElement(new { draftId = draft.DraftId, accountRef = "../mail" }), default, admission, session);
        Assert.Null(approval.Preparation); Assert.Contains("Validation", approval.ErrorJson!);
        Assert.Equal(0, f.Factory.Dispatches);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recent_inbox_tool_is_account_scoped_and_disabled_SMTP_does_not_claim_the_draft(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var account = await f.Credentials.CreateEmailAccountAsync("Mail", EmailAccountProfile.Gmail("sender@example.test"), "protected-fixture");
        var admission = await f.Bind(f.A, account, "mail"); var session = Guid.NewGuid();
        var definition = new AgentDefinition(1, "general-assistant", 25, new("Test", "Assistant", "Test", "neutral"), [], "instructions",
            new("answerNewTurn", true, true), new("balanced", false, "en", 2048), new(false, 60_000, 120_000, 1, ["longSilence"], 0),
            new(false, "default", 1), new("primary-llm", "primary-stt", "primary-tts"), new Dictionary<string, string>(),
            new(ToolAllowlist: [ToolCatalog.EmailAccounts, ToolCatalog.EmailRequestAccount, ToolCatalog.EmailSearch, ToolCatalog.EmailCreateDraft, ToolCatalog.EmailSend]));
        var gate = new AgentCore.Infrastructure.Tools.ToolConfigurationGate(null, null, null, emailFactory: f.Factory);
        var executor = new SessionToolExecutor(emailAccounts: f.Email, emailStore: f.EmailStore, configurationGate: gate);
        var search = await executor.ExecuteAsync(definition, session, new("latest-ten", ToolCatalog.EmailSearch, """{"limit":10}"""), ToolLimits.MaxOutputBytes, admission: admission);
        using var results = JsonDocument.Parse(search.Text);
        Assert.Equal("mail", results.RootElement.GetProperty("accountRef").GetString());
        Assert.Equal(new EmailSearchRequest("", 10), f.Factory.LastSearchRequest);
        Assert.NotEmpty(results.RootElement.GetProperty("results").EnumerateArray());
        var draft = await f.Email.CreateDraftAsync(session, admission, "mail", new(["to@example.test"], [], [], "Subject", "Body"), default);
        var args = JsonSerializer.SerializeToElement(new { draftId = draft.DraftId });
        var approved = await executor.PrepareEmailSendApprovalAsync(args, default, admission, session);
        f.Factory.IsSendEnabled = false;
        Assert.True(gate.IsConfigured(ToolCatalog.EmailSearch)); Assert.True(gate.IsConfigured(ToolCatalog.EmailCreateDraft));
        Assert.False(gate.IsConfigured(ToolCatalog.EmailSend));
        var inventory = await executor.ExecuteAsync(definition, session, new("accounts", ToolCatalog.EmailAccounts, "{}"), ToolLimits.MaxOutputBytes, admission: admission);
        using var inventoryJson = JsonDocument.Parse(inventory.Text);
        Assert.False(inventoryJson.RootElement.GetProperty("sendAvailable").GetBoolean());
        Assert.Contains("Email:SmtpEnabled", inventoryJson.RootElement.GetProperty("sendSetupGuidance").GetString()!);
        var requestAdmission = admission with { AgentRunId = Guid.NewGuid() };
        var request = await executor.ExecuteAsync(definition, session, new("send-account", ToolCatalog.EmailRequestAccount, """{"target":"email.send"}"""), ToolLimits.MaxOutputBytes, admission: requestAdmission);
        Assert.Contains("smtp_disabled", request.Text);
        Assert.Null(await f.EmailStore.GetSelectionAsync(requestAdmission.AgentRunId!.Value));
        var context = new AgentContext(definition, [], "", null, AgentCore.Domain.Conversation.SessionMode.Text, null, false, null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "send email"), ModelSupportsTools: true);
        Assert.DoesNotContain(ToolCatalog.For(definition, context, gate), tool => tool.Name == ToolCatalog.EmailSend);
        var blocked = await executor.PrepareEmailSendApprovalAsync(args, default, admission, session);
        Assert.Null(blocked.Preparation); Assert.Contains("smtp_disabled", blocked.ErrorJson!); Assert.Contains("Email:SmtpEnabled", blocked.ErrorJson!);
        var dispatched = await executor.ExecuteAsync(definition, session, new("blocked", ToolCatalog.EmailSend, args.GetRawText()), ToolLimits.MaxOutputBytes,
            approvalGrant: Grant(approved.Preparation!.ActionHash), admission: admission);
        Assert.Contains("smtp_disabled", dispatched.Text);
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Email.SendAsync(session, admission, draft.DraftId, "mail", Grant(approved.Preparation.ActionHash), default).AsTask());
        f.Reopen();
        Assert.Null(await f.EmailStore.GetSendAsync(draft.DraftId)); Assert.Equal(0, f.Factory.Dispatches);
        f.Factory.IsSendEnabled = true;
        var fresh = await executor.PrepareEmailSendApprovalAsync(args, default, admission, session);
        var sent = await f.Email.SendAsync(session, admission, draft.DraftId, "mail", Grant(fresh.Preparation!.ActionHash), default);
        Assert.Equal(EmailSendOutcome.Sent, sent.Outcome); Assert.Equal(1, f.Factory.Dispatches);
    }

    [Fact]
    public async Task Real_SMTP_requires_opt_in_while_Synthetic_dispatch_remains_key_free()
    {
        await using var f = await Fixture.Create(false);
        var options = new EmailTransportOptions();
        var real = new AccountEmailProviderFactory(f.Store, f.Bindings, new LocalCredentialProtector(f.Root + "/keys"), f.Owners, options, TimeProvider.System, false);
        Assert.True(real.IsEnabled); Assert.False(real.IsSendEnabled); Assert.True(f.Factory.IsSendEnabled);
        options.SmtpEnabled = true; Assert.True(real.IsSendEnabled);
        options.Enabled = false; Assert.False(real.IsSendEnabled); Assert.False(real.IsEnabled);
    }
    private sealed class DbFactory(string root) : IDbContextFactory<AgentCoreDbContext>
    {
        public DbContextOptions<AgentCoreDbContext> Options => new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={root}/email.db").Options;
        public AgentCoreDbContext CreateDbContext() => new(Options);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public string Root = Path.Combine(Path.GetTempPath(), "email-accounts-" + Guid.NewGuid().ToString("N"));
        public AgentInstance A = NewInstance(), B = NewInstance(); public IAgentInstanceStore Owners = null!;
        public ICredentialStore Store = null!; public IAgentCredentialBindingStore Bindings = null!;
        public CredentialService Credentials = null!; public IEmailStore EmailStore = null!;
        public EmailAccountService Email = null!; public CountingFactory Factory = null!; private bool sqlite;
        public static async Task<Fixture> Create(bool sqlite)
        {
            var f = new Fixture { sqlite = sqlite }; Directory.CreateDirectory(f.Root);
            if (sqlite) { await using var db = new DbFactory(f.Root).CreateDbContext(); await db.Database.MigrateAsync(); }
            f.Configure(); await f.Owners.InsertAsync(f.A); await f.Owners.InsertAsync(f.B); return f;
        }
        private void Configure()
        {
            var ids = new SystemIdGenerator(TimeProvider.System); var protector = new LocalCredentialProtector(Root + "/keys");
            if (sqlite)
            {
                var db = new DbFactory(Root); Owners = new SqliteAgentInstanceStore(db, ids);
                var store = new SqliteCredentialStore(db); Store = store; Bindings = store; EmailStore = new SqliteEmailStore(db);
            }
            else if (Store is null)
            {
                var owners = new InMemoryAgentInstanceStore(); Owners = owners;
                var store = new InMemoryCredentialStore(owners); Store = store; Bindings = store; EmailStore = new InMemoryEmailStore();
            }
            Credentials = new(Store, Bindings, protector, Owners, ids, TimeProvider.System);
            Factory ??= new(new AccountEmailProviderFactory(Store, Bindings, protector, Owners, new(), TimeProvider.System, true));
            Email = new(Store, Bindings, Owners, Factory, EmailStore, ids, TimeProvider.System);
        }
        public void Reopen() => Configure();
        public async Task<ToolExecutionAdmission> Bind(AgentInstance owner, CredentialView c, string alias)
        {
            await Credentials.BindAsync(owner.InstanceId, c.CredentialId, alias, (await Owners.FindAsync(owner.InstanceId))!.Revision);
            return new(false, TriggerKind.UserTurn, AgentInstanceId: owner.InstanceId, PinnedCredentialBindings: await Bindings.ListBindingsAsync(owner.InstanceId));
        }
        public ValueTask DisposeAsync() { SqliteConnection.ClearAllPools(); Directory.Delete(Root, true); return ValueTask.CompletedTask; }
        private static AgentInstance NewInstance() => new(Guid.NewGuid(), "general-assistant", 25, new("Test", "Assistant", "Test", "neutral"), AgentInstanceLifecycle.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }
    private sealed class CountingFactory(IEmailProviderFactory inner) : IEmailProviderFactory
    {
        public int Dispatches; public EmailSearchRequest? LastSearchRequest; public bool IsEnabled => inner.IsEnabled;
        public bool IsSendEnabled { get; set; } = inner.IsSendEnabled;
        public IEmailProvider Create(EmailAccountContext account) => new CountingProvider(inner.Create(account), this);
        public ValueTask<EmailConnectionTest> TestAsync(Guid id, long revision, string protocol, CancellationToken ct = default) => inner.TestAsync(id, revision, protocol, ct);
    }
    private sealed class CountingProvider(IEmailProvider inner, CountingFactory owner) : IEmailProvider
    {
        public bool IsAvailable => true;
        public ValueTask<EmailSearchResult> SearchAsync(EmailSearchRequest r, CancellationToken ct = default) { owner.LastSearchRequest = r; return inner.SearchAsync(r, ct); }
        public ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest r, CancellationToken ct = default) => inner.ReadAsync(r, ct);
        public ValueTask<EmailDraftResult> CreateDraftAsync(EmailCreateDraftRequest r, CancellationToken ct = default) => inner.CreateDraftAsync(r, ct);
        public ValueTask<EmailDraftSnapshot?> GetDraftAsync(string id, CancellationToken ct = default) => inner.GetDraftAsync(id, ct);
        public ValueTask<EmailSendResult> SendDraftAsync(EmailSendDraftRequest r, CancellationToken ct = default) { Interlocked.Increment(ref owner.Dispatches); return inner.SendDraftAsync(r, ct); }
    }
}
