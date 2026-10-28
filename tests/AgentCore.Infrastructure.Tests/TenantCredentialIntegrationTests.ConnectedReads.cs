using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Drive;
using AgentCore.Infrastructure.Email;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    public static TheoryData<bool, CredentialPurpose, string> ConnectedReadCases
    {
        get
        {
            var cases = new TheoryData<bool, CredentialPurpose, string>();
            foreach (var sqlite in new[] { false, true })
            foreach (var purpose in new[] { CredentialPurpose.EmailAccount, CredentialPurpose.GoogleDriveAccount })
            foreach (var mode in new[] { "rotate", "invalid-grant", "missing-scope", "broad-scope", "transient", "logout-token", "logout-resource", "retry-logout", "rejected-resource" }) cases.Add(sqlite, purpose, mode);
            return cases;
        }
    }

    private sealed record ConnectedSetup(Guid Tenant, Guid Instance, Guid Credential, HumanSessionReference Human);
    private static async Task<ConnectedSetup> ConnectedReadSetup(Fixture f, CredentialPurpose purpose)
    {
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var session = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new HumanSessionReference(owner.AccountId, session.SessionId, owner.OwnerPrincipalId);
        var credential = await ConnectForCheck(f, owner.TenantId, new(human.AccountId, human.AuthenticationSessionId, human.PrincipalId), purpose);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var agent = await f.AgentsAtRelease(35).CreateAsync(human.PrincipalId, owner.TenantId, "general-assistant", 35, await Revision());
        var value = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, credential))!.Value;
        await f.BindingService.BindAsync(human.PrincipalId, owner.TenantId, agent.Instance.InstanceId, credential, "exact-account", await Revision(), agent.Instance.Revision, value.Revision);
        return new(owner.TenantId, agent.Instance.InstanceId, credential, human);
    }
    private static TenantConnectedAccountService ConnectedReads(Fixture f, IHttpClientFactory http)
    {
        var drive = new GoogleDriveOptions { Enabled = true }; var files = new InMemoryDriveStore();
        var google = new TenantGoogleOAuthProvider(SharedOAuth(), drive, new(Path.Combine(f.Root, "keys")), f.Protector, http, false);
        var reader = new TenantConnectedAccountReader(google, new EmailTransportOptions { Enabled = true }, drive, http, f.Protector, files, files,
            f.Ids, f.Clock, false, NullLoggerFactory.Instance, new SystemDiagnosticIdSource());
        return new(f.Iam, (ITenantCredentialBindingStore)f.Iam, (ITenantCredentialOAuthStore)f.Iam, (ITenantConnectedAccountReadStore)f.Iam,
            reader, f.Identity, f.Authorizer, f.Ids, f.Clock);
    }
    private static object ConnectedFile(byte[] bytes, string version = "7") => new { id = "exact-file", name = "Original.bin", mimeType = "application/octet-stream",
        size = bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), version, md5Checksum = Convert.ToHexStringLower(MD5.HashData(bytes)), parents = new[] { "root" }, trashed = false };

    [Theory] [MemberData(nameof(ConnectedReadCases))]
    public async Task Connected_account_real_protocol_fences_refresh_rotation_scope_reconnect_and_each_remote_retry(bool sqlite, CredentialPurpose purpose, string mode)
    {
        await using var f = await Fixture.Create(sqlite); var s = await ConnectedReadSetup(f, purpose);
        var before = (await f.Credentials.FindTenantCredentialAsync(s.Tenant, s.Credential))!.Value;
        var posts = 0; var gets = 0;
        var http = new OAuthHttp(async request => {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                posts++; Assert.Equal(HttpMethod.Post, request.Method);
                var form = OAuthQuery(await request.Content!.ReadAsStringAsync());
                Assert.Equal("tenant-client", form["client_id"]); Assert.Equal("private-client-secret", form["client_secret"]);
                Assert.Equal("original-private-refresh", form["refresh_token"]); Assert.Equal("refresh_token", form["grant_type"]);
                if (mode == "logout-token") await f.Identity.SignOutAsync(s.Human.AuthenticationSessionId);
                if (mode == "invalid-grant") return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"private-provider-body\"}") };
                if (mode == "transient") return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":\"private-provider-body\"}") };
                var token = new Dictionary<string, object> { ["access_token"] = "private-access", ["token_type"] = "Bearer", ["expires_in"] = 3600,
                    ["scope"] = mode == "missing-scope" ? "openid email" : mode == "broad-scope" ? "openid email https://www.googleapis.com/auth/drive"
                        : "openid email " + (purpose == CredentialPurpose.EmailAccount ? EmailAccountProfile.ReadScope : DriveAccountProfile.Scope) };
                if (mode == "rotate") token["refresh_token"] = "rotated-private-refresh";
                return OAuthJson(token);
            }
            gets++; Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("private-access", request.Headers.Authorization!.Parameter);
            if (mode is "logout-resource" or "retry-logout") await f.Identity.SignOutAsync(s.Human.AuthenticationSessionId);
            if (mode == "retry-logout") return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("private-resource-body") };
            if (mode == "rejected-resource") return new(HttpStatusCode.Unauthorized) { Content = new StringContent("private-resource-body") };
            if (purpose == CredentialPurpose.GoogleDriveAccount)
            {
                Assert.Equal("/drive/v3/files", request.RequestUri.AbsolutePath);
                return OAuthJson(new { files = new[] { ConnectedFile([1, 2, 3]) } });
            }
            Assert.Equal("gmail.googleapis.com", request.RequestUri.Host);
            return request.RequestUri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
                ? OAuthJson(new { messages = new[] { new { id = "mail-id" } } })
                : OAuthJson(new { id = "mail-id", threadId = "thread-id", snippet = "private-mailbox-result", payload = new { headers = new[] { new { name = "Subject", value = "Exact original subject" } } } });
        });
        var service = ConnectedReads(f, http);
        var read = new ConnectedAccountRead(purpose == CredentialPurpose.EmailAccount ? ConnectedAccountReadKind.EmailSearch : ConnectedAccountReadKind.DriveList, Query: "exact");
        ConnectedAccountReadResult? result = null;
        var failure = await Record.ExceptionAsync(async () => result = await service.ReadAsync(s.Tenant, s.Instance, "exact-account", s.Human, read));
        var after = (await f.Credentials.FindTenantCredentialAsync(s.Tenant, s.Credential))!.Value;
        var audit = await f.Iam.ReadAuditAsync(s.Tenant, 200);
        if (mode == "rotate")
        {
            Assert.Null(failure); Assert.NotNull(result); Assert.Equal(purpose == CredentialPurpose.EmailAccount ? 2 : 1, gets);
            if (purpose == CredentialPurpose.EmailAccount) Assert.Equal("Exact original subject", Assert.Single(result.Mailbox!.Results).Subject);
            else Assert.Equal("exact-file", Assert.Single(result.Files!.Items).Id);
            Assert.Equal(before.SecretRevision + 1, after.SecretRevision);
            var grant = GoogleOAuthRefreshGrant.Read(f.Protector.Unprotect(s.Credential, after.ProtectionVersion, after.ProtectedPayload));
            Assert.Equal("rotated-private-refresh", grant.Token); Assert.Equal("tenant-client", grant.ClientId);
            Assert.Contains(audit, a => a.ReasonCode == "oauth_grant_rotated"); Assert.Single(audit, a => a.ReasonCode == "connected_account_read");
        }
        else
        {
            Assert.NotNull(failure); Assert.Null(result); Assert.DoesNotContain("private-", failure.Message);
            if (mode is "logout-token" or "logout-resource" or "retry-logout") Assert.IsType<AccessDeniedException>(failure);
            Assert.DoesNotContain(audit, a => a.ReasonCode == "connected_account_read");
            Assert.Equal(mode == "rejected-resource" ? 2 : 1, posts);
            Assert.Equal(mode == "rejected-resource" ? 2 : mode is "logout-resource" or "retry-logout" ? 1 : 0, gets);
            if (mode is "invalid-grant" or "missing-scope" or "rejected-resource")
            {
                Assert.Empty(after.ProtectedPayload); Assert.Equal(before.SecretRevision + 1, after.SecretRevision);
                Assert.Contains(audit, a => a.ReasonCode == "oauth_reconnect_required");
            }
            else { Assert.Equal(before.ProtectedPayload, after.ProtectedPayload); Assert.Equal(before.SecretRevision, after.SecretRevision); }
        }
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(audit));
    }

    [Theory] [InlineData(false, "original")] [InlineData(true, "original")]
    [InlineData(false, "version")] [InlineData(true, "version")]
    [InlineData(false, "checksum")] [InlineData(true, "checksum")]
    public async Task Connected_Drive_real_protocol_returns_exact_original_bytes_and_withholds_changed_sources(bool sqlite, string mode)
    {
        await using var f = await Fixture.Create(sqlite); var s = await ConnectedReadSetup(f, CredentialPurpose.GoogleDriveAccount);
        var original = Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray(); var metadata = 0; var transfers = 0;
        var http = new OAuthHttp(request => {
            if (request.RequestUri!.AbsolutePath == "/token") return Task.FromResult(OAuthJson(new { access_token = "private-access", token_type = "Bearer", expires_in = 3600 }));
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("private-access", request.Headers.Authorization!.Parameter);
            Assert.Equal("/drive/v3/files/exact-file", request.RequestUri.AbsolutePath);
            if (request.RequestUri.Query == "?alt=media")
            {
                transfers++; var sent = original.ToArray(); if (mode == "checksum") sent[0] ^= 1;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(sent) });
            }
            metadata++; return Task.FromResult(OAuthJson(ConnectedFile(original, metadata > 1 && mode == "version" ? "8" : "7")));
        });
        var service = ConnectedReads(f, http); var request = new ConnectedAccountRead(ConnectedAccountReadKind.DriveContent, Id: "exact-file", ExpectedVersion: "7", MaxBytes: 65536);
        if (mode == "original")
        {
            var read = await service.ReadAsync(s.Tenant, s.Instance, "exact-account", s.Human, request);
            Assert.Equal(original, read.Bytes); Assert.Equal("7", read.File!.Version); Assert.Equal(2, metadata);
            Assert.Single(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.ReasonCode == "connected_account_read");
        }
        else
        {
            var failure = await Assert.ThrowsAsync<SecurityConflictException>(() => service.ReadAsync(s.Tenant, s.Instance, "exact-account", s.Human, request).AsTask());
            Assert.Equal("drive_remote_conflict", failure.ReasonCode); Assert.Equal(mode == "version" ? 2 : 1, metadata);
            Assert.DoesNotContain(await f.Iam.ReadAuditAsync(s.Tenant, 200), a => a.ReasonCode == "connected_account_read");
        }
        Assert.Equal(1, transfers);
    }

    [Theory] [InlineData(false, CredentialPurpose.EmailAccount)] [InlineData(true, CredentialPurpose.EmailAccount)]
    [InlineData(false, CredentialPurpose.GoogleDriveAccount)] [InlineData(true, CredentialPurpose.GoogleDriveAccount)]
    public async Task Connected_account_read_audit_owner_reopens_exact_witnesses_and_rejects_stale_or_relogged_authentication(bool sqlite, CredentialPurpose purpose)
    {
        await using var f = await Fixture.Create(sqlite); var s = await ConnectedReadSetup(f, purpose);
        IIamStore security = sqlite ? new SqliteIamStore(f.Contexts!, clock: f.Clock) : f.Iam;
        var snapshot = (await ((ITenantCredentialBindingStore)security).ReadTenantCredentialBindingsAsync(s.Tenant, s.Instance))!;
        var bound = Assert.Single(snapshot.Bindings); var credential = bound.Credential.Value;
        var state = (await security.ReadTenantAsync(s.Tenant))!;
        var audit = new AuthorizationAudit(f.Ids.NewId(), s.Tenant, s.Human.PrincipalId, purpose == CredentialPurpose.EmailAccount ? "email.read" : "drive.read",
            s.Credential.ToString("D"), "allowed", "connected_account_read", f.Clock.GetUtcNow());
        var proof = new TenantConnectedAccountReadProof(s.Tenant, s.Instance, snapshot.Agent.Instance.Revision, bound.Binding.BindingId, bound.Binding.Revision,
            s.Credential, credential.Revision, credential.SecretRevision, purpose, s.Human, state.Tenant.Revision, audit);
        var store = (ITenantConnectedAccountReadStore)security;
        Assert.True(await store.TryRecordConnectedAccountReadAsync(proof)); Assert.True(await store.TryRecordConnectedAccountReadAsync(proof));
        Assert.Single(await security.ReadAuditAsync(s.Tenant, 200), a => a.ReasonCode == "connected_account_read");
        TenantConnectedAccountReadProof NextProof() => proof with { Audit = audit with { AuditId = f.Ids.NewId() } };
        Assert.False(await store.TryRecordConnectedAccountReadAsync(NextProof() with { InstanceRevision = proof.InstanceRevision + 1 }));
        Assert.False(await store.TryRecordConnectedAccountReadAsync(NextProof() with { BindingRevision = proof.BindingRevision + 1 }));
        Assert.False(await store.TryRecordConnectedAccountReadAsync(NextProof() with { CredentialRevision = proof.CredentialRevision + 1 }));
        Assert.False(await store.TryRecordConnectedAccountReadAsync(NextProof() with { SecretRevision = proof.SecretRevision + 1 }));
        Assert.False(await store.TryRecordConnectedAccountReadAsync(NextProof() with { Human = s.Human with { AuthenticationSessionId = Guid.NewGuid() } }));
        await f.Identity.SignOutAsync(s.Human.AuthenticationSessionId);
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        Assert.NotEqual(s.Human.AuthenticationSessionId, login.SessionId);
        Assert.False(await store.TryRecordConnectedAccountReadAsync(NextProof()));
        Assert.True(await store.TryRecordConnectedAccountReadAsync(NextProof() with { Human = s.Human with { AuthenticationSessionId = login.SessionId } }));
        Assert.Equal(2, (await security.ReadAuditAsync(s.Tenant, 200)).Count(a => a.ReasonCode == "connected_account_read"));
    }
}
