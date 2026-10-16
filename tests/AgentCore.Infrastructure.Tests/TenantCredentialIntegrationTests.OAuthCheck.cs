using System.Net;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false, CredentialPurpose.EmailAccount)]
    [InlineData(true, CredentialPurpose.EmailAccount)]
    [InlineData(false, CredentialPurpose.GoogleDriveAccount)]
    [InlineData(true, CredentialPurpose.GoogleDriveAccount)]
    public async Task OAuth_check_refreshes_only_owned_grants_and_preserves_logical_identity(bool sqlite, CredentialPurpose purpose)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var session = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var initiator = new CredentialOAuthInitiator(owner.AccountId, session.SessionId, owner.OwnerPrincipalId);
        var id = await ConnectForCheck(f, owner.TenantId, initiator, purpose);
        var mode = "rotate"; var requests = 0;
        using var http = new OAuthHttp(async request =>
        {
            requests++;
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                var form = await request.Content!.ReadAsStringAsync();
                Assert.Contains("grant_type=refresh_token", form); Assert.Contains("client_id=tenant-client", form);
                Assert.DoesNotContain("legacy", form);
                if (mode == "transient") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("private-provider-body") };
                if (mode == "invalid") return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"private-provider-body\"}") };
                return OAuthJson(new { access_token = "private-check-access", refresh_token = "rotated-private-refresh", token_type = "Bearer", expires_in = 3600 });
            }
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("private-check-access", request.Headers.Authorization!.Parameter);
            Assert.Equal(purpose == CredentialPurpose.EmailAccount ? "https://gmail.googleapis.com/gmail/v1/users/me/profile" : "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)", request.RequestUri.AbsoluteUri);
            return purpose == CredentialPurpose.EmailAccount ? OAuthJson(new { emailAddress = "fixture@example.test" }) : OAuthJson(new { user = new { emailAddress = "fixture@example.test" } });
        });
        var provider = OAuthProvider(f, http, false, SharedOAuth()); var service = CheckService(f, provider);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var before = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        var result = await service.CheckAsync(owner.TenantId, id, initiator, await Revision(), before.Revision);
        Assert.False(result.ReconnectRequired); AssertSafe(result, "private-check-access", "rotated-private-refresh");
        var rotated = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        Assert.Equal(before.Revision, rotated.Revision); Assert.Equal(TenantCredentialOAuthRules.Epoch(before), TenantCredentialOAuthRules.Epoch(rotated));
        Assert.Equal(before.SecretRevision + 1, rotated.SecretRevision);
        Assert.Equal("rotated-private-refresh", GoogleOAuthRefreshGrant.Read(f.Protector.Unprotect(id, 1, rotated.ProtectedPayload)).Token);
        mode = "transient";
        var revision = await Revision();
        var failure = await Assert.ThrowsAsync<AgentCoreException>(() => service.CheckAsync(owner.TenantId, id, initiator, revision, rotated.Revision).AsTask());
        Assert.Equal("oauth_check_unavailable", failure.Code); Assert.DoesNotContain("private-provider-body", failure.Message);
        var unchanged = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        Assert.Equal(rotated.ProtectedPayload, unchanged.ProtectedPayload); Assert.Equal(rotated.SecretRevision, unchanged.SecretRevision);
        Assert.Equal(rotated.Revision, unchanged.Revision); Assert.Equal(revision, await Revision());
        mode = "invalid";
        var invalid = await service.CheckAsync(owner.TenantId, id, initiator, await Revision(), rotated.Revision);
        Assert.True(invalid.ReconnectRequired);
        var cleared = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        Assert.Empty(cleared.ProtectedPayload); Assert.Equal(rotated.Revision + 1, cleared.Revision); Assert.Equal(rotated.SecretRevision + 1, cleared.SecretRevision);
        Assert.Equal(TenantCredentialOAuthRules.Epoch(rotated) + 1, TenantCredentialOAuthRules.Epoch(cleared));
        Assert.Equal("ReconnectRequired", cleared.EmailAccount?.ConnectionStatus.ToString() ?? cleared.DriveAccount!.ConnectionStatus.ToString());
        Assert.Equal(4, requests); Assert.Null(await f.Legacy.GetAsync(id));
        AssertSafe(await f.Iam.ReadAuditAsync(owner.TenantId, 100), "private-check-access", "rotated-private-refresh", "private-provider-body");
        if (sqlite)
        {
            var reopened = (await new SqliteIamStore(f.Contexts!).FindTenantCredentialAsync(owner.TenantId, id))!.Value;
            Assert.Equal(cleared.Revision, reopened.Revision); Assert.Empty(reopened.ProtectedPayload);
        }
    }

    [Theory]
    [InlineData(false, "permission")]
    [InlineData(true, "permission")]
    [InlineData(false, "logout")]
    [InlineData(true, "logout")]
    [InlineData(false, "disable")]
    [InlineData(true, "disable")]
    [InlineData(false, "rotation")]
    [InlineData(true, "rotation")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "host")]
    [InlineData(true, "host")]
    [InlineData(false, "deadline")]
    [InlineData(true, "deadline")]
    public async Task OAuth_check_revalidates_live_authority_and_generations_after_IO(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T06:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var session = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var ownerInitiator = new CredentialOAuthInitiator(owner.AccountId, session.SessionId, owner.OwnerPrincipalId);
        var id = await ConnectForCheck(f, owner.TenantId, ownerInitiator, CredentialPurpose.GoogleDriveAccount, true);
        var writer = await AddUser(f, owner.TenantId, owner.OwnerPrincipalId, "agent-operator");
        var writerSession = (await f.Identity.SignInAsync("writer@example.test", "strong-writer-password"))!.Session;
        var initiator = new CredentialOAuthInitiator(writer.Account.AccountId, writerSession.SessionId, writer.User.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var scope = TenantCredentialRules.Scope(owner.TenantId, id);
        var role = await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, owner.TenantId, null, "Credential manager", ["credentials.read", "credentials.manage"], await Revision());
        await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, writer.User.PrincipalId, role.RoleId, scope, await Revision());
        var gate = new GatedCheck(f.Protector.Protect(id, 1, "replacement-not-committed"), boundary == "deadline"); var service = CheckService(f, gate);
        var startRevision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.CheckAsync(owner.TenantId, id, initiator, startRevision, 2).AsTask());
        Assert.False(gate.Entered.Task.IsCompleted); // manage/read and binding configuration never imply use.
        var useRole = await f.Access.SaveRoleAsync(owner.OwnerPrincipalId, owner.TenantId, null, "Use this credential", ["credentials.use"], await Revision());
        var assignment = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, writer.User.PrincipalId, useRole.RoleId, scope, await Revision());
        var before = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        using var cancellation = new CancellationTokenSource();
        var task = service.CheckAsync(owner.TenantId, id, initiator, await Revision(), 2, cancellation.Token).AsTask();
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var parallelRevision = await Revision();
            var parallel = await Assert.ThrowsAsync<SecurityConflictException>(() => service.CheckAsync(owner.TenantId, id, initiator, parallelRevision, 2).AsTask());
            Assert.Equal("oauth_check_in_progress", parallel.ReasonCode);
            if (boundary == "permission") await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, assignment.AssignmentId, await Revision());
            if (boundary == "logout") await f.Identity.SignOutAsync(writerSession.SessionId);
            if (boundary == "disable") await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, id, new("Disabled", CredentialStatus.Disabled, null, null), await Revision(), 2);
            if (boundary == "rotation")
            {
                var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
                Assert.True(await ((ITenantCredentialOAuthStore)f.Iam).TryCheckCredentialOAuthAsync(Next(state), state.Tenant.Revision, id, ownerInitiator,
                    before.Revision, before.SecretRevision, new(f.Protector.Protect(id, 1, "winning-rotation")), f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)));
            }
            if (boundary == "cancel") cancellation.Cancel();
            if (boundary == "host") gate.HostEnabled = false;
            if (boundary == "deadline") clock.Advance(TimeSpan.FromSeconds(30));
        }
        finally { gate.Proceed.TrySetResult(); }
        if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else if (boundary == "deadline") Assert.Equal("oauth_check_unavailable", (await Assert.ThrowsAsync<AgentCoreException>(() => task)).Code);
        else if (boundary is "disable" or "rotation" or "host") await Assert.ThrowsAsync<SecurityConflictException>(() => task);
        else await Assert.ThrowsAsync<AccessDeniedException>(() => task);
        var retained = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        Assert.Equal(boundary == "disable" ? 3 : 2, retained.Revision);
        Assert.Equal(before.SecretRevision + (boundary == "rotation" ? 1 : 0), retained.SecretRevision);
        Assert.DoesNotContain("replacement-not-committed", f.Protector.Unprotect(id, 1, retained.ProtectedPayload));
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(owner.TenantId, 200), a => a.Action == "credentials.oauth.check");
    }

    private sealed class GatedCheck(string payload, bool ignoreCancellation = false) : ITenantCredentialOAuthVerifier
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HostEnabled = true;
        public CredentialOAuthDescriptor Describe(CredentialPurpose purpose) => new("fixture", "https://app.example.test", true, true, HostEnabled);
        public async ValueTask<CredentialOAuthCheck> CheckAsync(Credential current, CancellationToken ct)
        { Entered.TrySetResult(); await Proceed.Task.WaitAsync(ignoreCancellation ? CancellationToken.None : ct); return new(payload); }
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("malformed")]
    [InlineData("token-type")]
    [InlineData("expiry")]
    [InlineData("scope", true)]
    [InlineData("identity", true)]
    [InlineData("client")]
    [InlineData("mode")]
    [InlineData("profile-unavailable")]
    public async Task OAuth_check_provider_bounds_refresh_and_never_exposes_provider_values(string failure, bool reconnect = false)
    {
        await using var f = await Fixture.Create(false);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var session = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var id = await ConnectForCheck(f, owner.TenantId, new(owner.AccountId, session.SessionId, owner.OwnerPrincipalId), CredentialPurpose.GoogleDriveAccount);
        var current = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        if (failure == "client") current = current with { ProtectedPayload = f.Protector.Protect(id, 1, GoogleOAuthRefreshGrant.Write("private-refresh", "legacy-client")) };
        if (failure == "mode") current = current with { DriveAccount = current.DriveAccount! with { Synthetic = true } };
        var calls = 0;
        using var http = new OAuthHttp(request =>
        {
            calls++;
            if (failure == "profile-unavailable" && request.RequestUri!.Host != "oauth2.googleapis.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("private-profile-body") });
            if (request.RequestUri!.Host != "oauth2.googleapis.com") return Task.FromResult(OAuthJson(new { user = new { emailAddress = "different@example.test" } }));
            if (failure == "oversized") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 65537)) });
            if (failure == "malformed") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("private-malformed-provider-body") });
            return Task.FromResult(OAuthJson(new { access_token = "private-check-access", token_type = failure == "token-type" ? "Basic" : "Bearer", expires_in = failure == "expiry" ? 0 : 3600,
                scope = failure == "scope" ? "openid email" : "openid email " + DriveAccountProfile.Scope }));
        });
        var provider = OAuthProvider(f, http, false, SharedOAuth());
        if (reconnect) { var result = await provider.CheckAsync(current, default); Assert.True(result.ReconnectRequired); Assert.Null(result.RotatedProtectedPayload); }
        else
        {
            var error = await Assert.ThrowsAsync<AgentCoreException>(() => provider.CheckAsync(current, default).AsTask());
            Assert.Equal(failure == "client" ? "oauth_client_changed" : "oauth_check_unavailable", error.Code);
            Assert.DoesNotContain("private-refresh", error.Message); Assert.DoesNotContain("private-check-access", error.Message); Assert.DoesNotContain("private-malformed", error.Message);
        }
        Assert.Equal(failure is "client" or "mode" ? 0 : failure is "identity" or "profile-unavailable" ? 2 : 1, calls);
    }

    private static TenantCredentialOAuthCheckService CheckService(Fixture f, ITenantCredentialOAuthVerifier verifier) => new(f.Iam, f.Credentials,
        (ITenantCredentialOAuthStore)f.Iam, verifier, f.Identity, f.Authorizer, f.Service, f.Ids, f.Clock);
    private static async Task<Guid> ConnectForCheck(Fixture f, Guid tenant, CredentialOAuthInitiator initiator, CredentialPurpose purpose, bool synthetic = false)
    {
        var state = (await f.Iam.ReadTenantAsync(tenant))!;
        var view = await f.Service.CreateAsync(initiator.PrincipalId, tenant, OAuthInput(purpose), state.Tenant.Revision);
        var id = view.Credential.CredentialId; var current = (await f.Credentials.FindTenantCredentialAsync(tenant, id))!.Value;
        var attempt = OAuthAttempt(f, tenant, current, initiator) with { Provider = new("tenant-client", "https://app.example.test/api/v2/integrations/tenant-google/oauth/callback", synthetic, true) };
        Assert.True(await BeginOAuth(f, attempt)); state = (await f.Iam.ReadTenantAsync(tenant))!;
        var store = (ITenantCredentialOAuthStore)f.Iam;
        var claim = (await store.TryClaimCredentialOAuthAsync(attempt.AttemptId, attempt.StateHash, attempt.CorrelationHash, state.Tenant.Revision))!;
        var value = TenantCredentialOAuthRules.Connect(current, new("fixture-subject", "fixture@example.test",
            purpose == CredentialPurpose.EmailAccount ? ["openid", "email", EmailAccountProfile.ReadScope, EmailAccountProfile.SendScope] : ["openid", "email", DriveAccountProfile.Scope],
            f.Protector.Protect(id, 1, GoogleOAuthRefreshGrant.Write("original-private-refresh", "tenant-client")), synthetic), f.Clock.GetUtcNow());
        Assert.True(await store.TryCommitCredentialOAuthAsync(Next(state), state.Tenant.Revision, attempt.AttemptId, claim.Attempt.Revision, value, current.SecretRevision, f.Audit(tenant, initiator.PrincipalId, id)));
        return id;
    }
}
