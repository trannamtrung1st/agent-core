using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantCredentialAccessTests
{
    public static IEnumerable<object[]> AccountFinalResultAuthorityCases() =>
        from sqlite in new[] { false, true }
        from action in new[] { "inventory", "mail", "drive", "oauth-setup", "oauth-check", "oauth-disconnect" }
        from boundary in new[] { "logout", "relogin", "password", "security" }
        select new object[] { sqlite, action, boundary };

    [Theory]
    [MemberData(nameof(AccountFinalResultAuthorityCases))]
    public async Task Account_HTTP_result_rechecks_original_human_after_the_final_source_lookup(bool sqlite, string action, string boundary)
    {
        await using var source = new OAuthFactory(sqlite: sqlite);
        var race = new AccountFinalReadRace();
        await using var factory = source.WithWebHostBuilder(b => b.ConfigureTestServices(services => {
            AccountFinalReadStore Wrap(IServiceProvider p) => new(p.GetRequiredService<IIamStore>(), race);
            services.Replace(ServiceDescriptor.Singleton<ITenantCredentialBindingStore>(Wrap));
            services.Replace(ServiceDescriptor.Singleton<ITenantCredentialStore>(Wrap));
            services.Replace(ServiceDescriptor.Singleton<ITenantCredentialOAuthStore>(Wrap));
            services.Replace(ServiceDescriptor.Singleton<ITenantConnectedAccountReadStore>(Wrap));
        }));
        using var owner = ConnectedClient(factory);
        var s = await ConnectedReadAccount(factory, owner, action == "drive" ? "GoogleDriveAccount" : "EmailAccount");
        var api = $"/api/v2/tenants/{s.Tenant}";
        var bytes = "Private account body sentinel"u8.ToArray();
        if (action == "drive")
            await factory.Services.GetRequiredService<ISyntheticDriveStore>().SaveAsync(s.Credential,
                new("exact-source", "Private account file sentinel.txt", "text/plain", bytes.Length, "1", Convert.ToHexStringLower(MD5.HashData(bytes)), ["root"], false), bytes, null, default);
        var path = action switch {
            "inventory" => s.Root,
            "mail" => s.Root + "/" + s.Alias + "/email/messages?query=harness",
            "drive" => s.Root + "/" + s.Alias + "/drive/files/exact-source/content?expectedVersion=1",
            _ => api + $"/credentials/{s.Credential}/oauth" + (action == "oauth-setup" ? "" : action == "oauth-check" ? "/check" : "/disconnect")
        };
        var revision = await Revision(owner, s.Tenant);
        race.BindingReads = action == "inventory" ? 2 : 0;
        race.CredentialNext = action == "oauth-setup";
        race.AfterRead = async () => {
            if (boundary == "security")
                Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync(api + "/roles", new { name = "Concurrent account reader", permissions = new[] { "credentials.read" }, expectedRevision = await Revision(owner, s.Tenant) })).StatusCode);
            else if (boundary == "password")
                Assert.True((await owner.PostAsJsonAsync("/api/v2/account/password", new { currentPassword = "explicit-synthetic-test-password", newPassword = "replacement-synthetic-password" })).IsSuccessStatusCode);
            else {
                Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsync("/api/v2/auth/logout", null)).StatusCode);
                if (boundary == "relogin") await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
            }
        };
        using var refused = action is "oauth-check" or "oauth-disconnect"
            ? await owner.PostAsJsonAsync(path, new TenantCredentialOAuthRequest(revision, 2)) : await owner.GetAsync(path);
        Assert.Null(race.AfterRead);
        Assert.Equal(boundary == "security" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden, refused.StatusCode);
        var error = await Json(refused); Safe(error);
        Assert.Equal(boundary == "security" ? action.StartsWith("oauth-", StringComparison.Ordinal) ? "stale_revision" : "account_source_changed" : "initiator_unavailable", error.GetProperty("reasonCode").GetString());
        foreach (var name in new[] { "items", "mailbox", "credential", "configured", "bytes" }) Assert.False(error.TryGetProperty(name, out _));
        foreach (var sentinel in new[] { "Exact connected account", "Private account body sentinel", "Private account file sentinel" }) Assert.DoesNotContain(sentinel, error.GetRawText());
        Assert.Equal(action is "oauth-check" or "oauth-disconnect" ? 1 : 0, race.OAuthCommits);
        Assert.Equal(action is "mail" or "drive" ? 1 : 0, race.ReadCommits);
        if (boundary is "logout" or "password")
            await Login(owner, "owner@example.test", boundary == "password" ? "replacement-synthetic-password" : "explicit-synthetic-test-password");
        var expectedRevision = revision + (action is "oauth-check" or "oauth-disconnect" ? 1 : 0) + (boundary == "security" ? 1 : 0);
        Assert.Equal(expectedRevision, await Revision(owner, s.Tenant));
        // Recover committed metadata with a fresh authorized GET; never repeat OAuth check/disconnect.
        using var recovered = await owner.GetAsync(action.StartsWith("oauth-", StringComparison.Ordinal) ? api + $"/credentials/{s.Credential}" : path);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        if (action == "drive") Assert.Equal(bytes, await recovered.Content.ReadAsByteArrayAsync());
        else {
            var data = await Json(recovered); Safe(data);
            if (action == "mail") Assert.NotEmpty(data.GetProperty("mailbox").GetProperty("results").EnumerateArray());
            else Assert.Contains("Exact connected account", data.GetRawText());
        }
        Assert.Equal(action is "oauth-check" or "oauth-disconnect" ? 1 : 0, race.OAuthCommits);
        var current = (await ((ITenantCredentialStore)factory.Services.GetRequiredService<IIamStore>()).FindTenantCredentialAsync(s.Tenant, s.Credential))!.Value;
        Assert.Equal(action == "oauth-disconnect" ? "Disconnected" : "Connected", current.EmailAccount?.ConnectionStatus.ToString() ?? current.DriveAccount!.ConnectionStatus.ToString());
        Assert.Equal(0, source.Outbound);
    }

    private sealed class AccountFinalReadRace
    {
        public Func<Task>? AfterRead;
        public int BindingReads;
        public bool BindingNext;
        public bool CredentialNext;
        public int ReadCommits;
        public int OAuthCommits;
        public async ValueTask Captured() { if (Interlocked.Exchange(ref AfterRead, null) is { } action) await action(); }
    }
    private sealed class AccountFinalReadStore(IIamStore store, AccountFinalReadRace race)
        : ITenantCredentialStore, ITenantCredentialBindingStore, ITenantCredentialOAuthStore, ITenantConnectedAccountReadStore
    {
        private ITenantCredentialStore Credentials => (ITenantCredentialStore)store;
        private ITenantCredentialBindingStore Bindings => (ITenantCredentialBindingStore)store;
        private ITenantCredentialOAuthStore OAuth => (ITenantCredentialOAuthStore)store;
        private ITenantConnectedAccountReadStore Reads => (ITenantConnectedAccountReadStore)store;
        public async ValueTask<TenantCredential?> FindTenantCredentialAsync(Guid tenant, Guid id, CancellationToken ct = default)
        { var value = await Credentials.FindTenantCredentialAsync(tenant, id, ct); if (race.CredentialNext) { race.CredentialNext = false; await race.Captured(); } return value; }
        public ValueTask<TenantCredentialPage> ListTenantCredentialsAsync(Guid tenant, IReadOnlyList<Guid>? ids, int page, int size, CancellationToken ct = default) => Credentials.ListTenantCredentialsAsync(tenant, ids, page, size, ct);
        public ValueTask<bool> TrySaveTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, TenantCredentialRoot owner, Credential value, long expectedRevision, long expectedSecretRevision, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
            => Credentials.TrySaveTenantCredentialAsync(next, expectedSecurityRevision, owner, value, expectedRevision, expectedSecretRevision, audit, ct, initiator);
        public ValueTask<bool> TryDeleteTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, Guid id, long expectedRevision, long expectedSecretRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
            => Credentials.TryDeleteTenantCredentialAsync(next, expectedSecurityRevision, id, expectedRevision, expectedSecretRevision, at, audit, ct, initiator);
        public async ValueTask<TenantCredentialBindingSnapshot?> ReadTenantCredentialBindingsAsync(Guid tenant, Guid instance, CancellationToken ct = default)
        {
            var value = await Bindings.ReadTenantCredentialBindingsAsync(tenant, instance, ct);
            var captured = race.BindingNext || race.BindingReads > 0 && Interlocked.Decrement(ref race.BindingReads) == 0;
            if (captured) { race.BindingNext = false; await race.Captured(); } return value;
        }
        public ValueTask<TenantAgent?> TryBindTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, AgentCredentialBinding binding, long expectedInstanceRevision, long expectedCredentialRevision, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
            => Bindings.TryBindTenantCredentialAsync(next, expectedSecurityRevision, binding, expectedInstanceRevision, expectedCredentialRevision, audit, ct, initiator);
        public ValueTask<TenantAgent?> TryUnbindTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, Guid instance, Guid binding, long expectedInstanceRevision, long expectedBindingRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
            => Bindings.TryUnbindTenantCredentialAsync(next, expectedSecurityRevision, instance, binding, expectedInstanceRevision, expectedBindingRevision, at, audit, ct, initiator);
        public async ValueTask<bool> TryRecordConnectedAccountReadAsync(TenantConnectedAccountReadProof proof, CancellationToken ct = default)
        { var value = await Reads.TryRecordConnectedAccountReadAsync(proof, ct); if (value) { race.ReadCommits++; race.BindingNext = true; } return value; }
        public ValueTask<bool> TryCommitRuntimeAccountReadAsync(TenantSecurityState next, long expectedSecurityRevision, TenantRuntimeAccountReadProof proof, CredentialOAuthCheck? maintenance, AuthorizationAudit audit, CancellationToken ct = default)
            => Reads.TryCommitRuntimeAccountReadAsync(next, expectedSecurityRevision, proof, maintenance, audit, ct);
        public async ValueTask<bool> TryCheckCredentialOAuthAsync(TenantSecurityState next, long expectedSecurityRevision, Guid credential, CredentialOAuthInitiator initiator, long expectedCredentialRevision, long expectedSecretRevision, CredentialOAuthCheck check, AuthorizationAudit audit, CancellationToken ct = default)
        { var value = await OAuth.TryCheckCredentialOAuthAsync(next, expectedSecurityRevision, credential, initiator, expectedCredentialRevision, expectedSecretRevision, check, audit, ct); if (value) { race.OAuthCommits++; race.CredentialNext = true; } return value; }
        public async ValueTask<bool> TryDisconnectCredentialOAuthAsync(TenantSecurityState next, long expectedSecurityRevision, Guid credential, CredentialOAuthInitiator initiator, long expectedCredentialRevision, long expectedSecretRevision, AuthorizationAudit audit, CancellationToken ct = default)
        { var value = await OAuth.TryDisconnectCredentialOAuthAsync(next, expectedSecurityRevision, credential, initiator, expectedCredentialRevision, expectedSecretRevision, audit, ct); if (value) { race.OAuthCommits++; race.CredentialNext = true; } return value; }
        public ValueTask<CredentialOAuthAttempt?> ReadCredentialOAuthAttemptAsync(Guid id, CancellationToken ct = default) => OAuth.ReadCredentialOAuthAttemptAsync(id, ct);
        public ValueTask<bool> TryBeginCredentialOAuthAsync(TenantSecurityState next, long expectedSecurityRevision, CredentialOAuthAttempt attempt, AuthorizationAudit audit, CancellationToken ct = default) => OAuth.TryBeginCredentialOAuthAsync(next, expectedSecurityRevision, attempt, audit, ct);
        public ValueTask<CredentialOAuthClaim?> TryClaimCredentialOAuthAsync(Guid id, string stateHash, string correlationHash, long expectedSecurityRevision, CancellationToken ct = default) => OAuth.TryClaimCredentialOAuthAsync(id, stateHash, correlationHash, expectedSecurityRevision, ct);
        public ValueTask<bool> TryCommitCredentialOAuthAsync(TenantSecurityState next, long expectedSecurityRevision, Guid id, long claimRevision, Credential value, long expectedSecretRevision, AuthorizationAudit audit, CancellationToken ct = default) => OAuth.TryCommitCredentialOAuthAsync(next, expectedSecurityRevision, id, claimRevision, value, expectedSecretRevision, audit, ct);
        public ValueTask FinishCredentialOAuthAsync(Guid id, long revision, CredentialOAuthOutcome outcome, CancellationToken ct = default) => OAuth.FinishCredentialOAuthAsync(id, revision, outcome, ct);
        public ValueTask<int> ExpireCredentialOAuthAttemptsAsync(int limit, CancellationToken ct = default) => OAuth.ExpireCredentialOAuthAttemptsAsync(limit, ct);
    }
}
