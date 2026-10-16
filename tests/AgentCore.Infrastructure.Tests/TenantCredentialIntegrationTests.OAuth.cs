using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false, CredentialPurpose.EmailAccount)]
    [InlineData(true, CredentialPurpose.EmailAccount)]
    [InlineData(false, CredentialPurpose.GoogleDriveAccount)]
    [InlineData(true, CredentialPurpose.GoogleDriveAccount)]
    public async Task OAuth_store_claim_connect_reopen_and_disconnect_are_atomic(bool sqlite, CredentialPurpose purpose)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var signedIn = await f.Identity.SignInAsync("owner@example.test", "strong-owner-password");
        Assert.NotNull(signedIn);
        var initiator = new CredentialOAuthInitiator(owner.AccountId, signedIn.Session.SessionId, owner.OwnerPrincipalId);
        var created = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, OAuthInput(purpose), 1);
        var id = created.Credential.CredentialId;
        var current = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        var oauth = (ITenantCredentialOAuthStore)f.Iam;
        var attempt = OAuthAttempt(f, owner.TenantId, current, initiator);
        Assert.True(await BeginOAuth(f, attempt));
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        Assert.Null(await oauth.TryClaimCredentialOAuthAsync(attempt.AttemptId, attempt.StateHash, Digest("wrong-browser"), state.Tenant.Revision));
        var claim = (await oauth.TryClaimCredentialOAuthAsync(attempt.AttemptId, attempt.StateHash, attempt.CorrelationHash, state.Tenant.Revision))!;
        Assert.Equal(attempt.ProtectedProof, claim.ProtectedProof);
        Assert.Empty(claim.Attempt.ProtectedProof);
        Assert.Empty((await oauth.ReadCredentialOAuthAttemptAsync(attempt.AttemptId))!.ProtectedProof);
        Assert.Null(await oauth.TryClaimCredentialOAuthAsync(attempt.AttemptId, attempt.StateHash, attempt.CorrelationHash, state.Tenant.Revision));
        var connected = OAuthConnected(f, current, "google-subject");
        Assert.False(await oauth.TryCommitCredentialOAuthAsync(state, state.Tenant.Revision - 1, attempt.AttemptId, claim.Attempt.Revision,
            connected, current.SecretRevision, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)));
        Assert.True(await oauth.TryCommitCredentialOAuthAsync(Next(state), state.Tenant.Revision, attempt.AttemptId, claim.Attempt.Revision,
            connected, current.SecretRevision, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)));
        var saved = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        Assert.Equal("refresh-private-value", f.Protector.Unprotect(id, saved.ProtectionVersion, saved.ProtectedPayload));
        Assert.Equal(1, saved.SecretRevision); Assert.Equal(1, TenantCredentialOAuthRules.Epoch(saved)); Assert.Equal(2, saved.Revision);
        Assert.Equal(CredentialOAuthOutcome.Connected, (await oauth.ReadCredentialOAuthAttemptAsync(attempt.AttemptId))!.Outcome);
        AssertSafe(await f.Service.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, id), "refresh-private-value", attempt.ProtectedProof, attempt.StateHash);
        Assert.Null(await f.Legacy.GetAsync(id));
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, clock: f.Clock);
            var retained = (await reopened.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
            Assert.Equal(saved, retained with { Metadata = saved.Metadata, AllowedOrigins = saved.AllowedOrigins, EmailAccount = saved.EmailAccount, DriveAccount = saved.DriveAccount });
            Assert.Equal(CredentialOAuthOutcome.Connected, (await reopened.ReadCredentialOAuthAttemptAsync(attempt.AttemptId))!.Outcome);
            Assert.Empty((await reopened.ReadCredentialOAuthAttemptAsync(attempt.AttemptId))!.ProtectedProof);
        }
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, id, new("Renamed", CredentialStatus.Disabled, null, null), state.Tenant.Revision, saved.Revision);
        var disabled = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        Assert.Equal(saved.ProtectedPayload, disabled.ProtectedPayload); Assert.Equal(saved.SecretRevision, disabled.SecretRevision);
        Assert.Equal(2, TenantCredentialOAuthRules.Epoch(disabled));
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        Assert.True(await oauth.TryDisconnectCredentialOAuthAsync(Next(state), state.Tenant.Revision, id, initiator, disabled.Revision, disabled.SecretRevision,
            f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)));
        var disconnected = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        Assert.Empty(disconnected.ProtectedPayload); Assert.Equal(2, disconnected.SecretRevision); Assert.Equal(3, TenantCredentialOAuthRules.Epoch(disconnected));
        Assert.Equal("google-subject", disconnected.EmailAccount?.GoogleSubject ?? disconnected.DriveAccount?.GoogleSubject);
        AssertSafe(await f.Iam.ReadAuditAsync(owner.TenantId, 200), "refresh-private-value", attempt.ProtectedProof, attempt.StateHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OAuth_store_supersession_failure_and_expiry_preserve_the_current_grant(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T03:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var session = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var initiator = new CredentialOAuthInitiator(owner.AccountId, session.SessionId, owner.OwnerPrincipalId);
        var view = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, OAuthInput(CredentialPurpose.GoogleDriveAccount), 1);
        var id = view.Credential.CredentialId; var current = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        var oauth = (ITenantCredentialOAuthStore)f.Iam;
        var first = OAuthAttempt(f, owner.TenantId, current, initiator); Assert.True(await BeginOAuth(f, first));
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var claim = (await oauth.TryClaimCredentialOAuthAsync(first.AttemptId, first.StateHash, first.CorrelationHash, state.Tenant.Revision))!;
        var second = OAuthAttempt(f, owner.TenantId, current, initiator); Assert.True(await BeginOAuth(f, second));
        Assert.Equal(CredentialOAuthOutcome.Superseded, (await oauth.ReadCredentialOAuthAttemptAsync(first.AttemptId))!.Outcome);
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        Assert.False(await oauth.TryCommitCredentialOAuthAsync(Next(state), state.Tenant.Revision, first.AttemptId, claim.Attempt.Revision,
            OAuthConnected(f, current, "old-subject"), current.SecretRevision, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)));
        claim = (await oauth.TryClaimCredentialOAuthAsync(second.AttemptId, second.StateHash, second.CorrelationHash, state.Tenant.Revision))!;
        Assert.True(await oauth.TryCommitCredentialOAuthAsync(Next(state), state.Tenant.Revision, second.AttemptId, claim.Attempt.Revision,
            OAuthConnected(f, current, "current-subject"), current.SecretRevision, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)));
        current = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        var reconnect = OAuthAttempt(f, owner.TenantId, current, initiator); Assert.True(await BeginOAuth(f, reconnect));
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        claim = (await oauth.TryClaimCredentialOAuthAsync(reconnect.AttemptId, reconnect.StateHash, reconnect.CorrelationHash, state.Tenant.Revision))!;
        await oauth.FinishCredentialOAuthAsync(reconnect.AttemptId, claim.Attempt.Revision, CredentialOAuthOutcome.Denied);
        await oauth.FinishCredentialOAuthAsync(reconnect.AttemptId, claim.Attempt.Revision, CredentialOAuthOutcome.Failed);
        Assert.Equal(CredentialOAuthOutcome.Denied, (await oauth.ReadCredentialOAuthAttemptAsync(reconnect.AttemptId))!.Outcome);
        var expiring = OAuthAttempt(f, owner.TenantId, current, initiator); Assert.True(await BeginOAuth(f, expiring));
        clock.Advance(TimeSpan.FromMinutes(5));
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        Assert.Null(await oauth.TryClaimCredentialOAuthAsync(expiring.AttemptId, expiring.StateHash, expiring.CorrelationHash, state.Tenant.Revision));
        Assert.Equal(1, await oauth.ExpireCredentialOAuthAttemptsAsync(1)); Assert.Equal(0, await oauth.ExpireCredentialOAuthAttemptsAsync(1));
        var expired = (await oauth.ReadCredentialOAuthAttemptAsync(expiring.AttemptId))!;
        Assert.Equal(CredentialOAuthOutcome.Expired, expired.Outcome); Assert.Empty(expired.ProtectedProof);
        var retained = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        Assert.Equal(current.ProtectedPayload, retained.ProtectedPayload); Assert.Equal(current.Revision, retained.Revision); Assert.Equal(current.SecretRevision, retained.SecretRevision);
        Assert.Equal(state.Tenant.Revision, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
    }

    private static TenantCredentialCreate OAuthInput(CredentialPurpose purpose) => new("OAuth", CredentialKind.Token, purpose, null, null, null,
        purpose == CredentialPurpose.EmailAccount ? EmailAccountProfile.Gmail() : null);
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static TenantSecurityState Next(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
    private static CredentialOAuthAttempt OAuthAttempt(Fixture f, Guid tenant, Credential current, CredentialOAuthInitiator initiator)
    {
        var now = f.Clock.GetUtcNow();
        return new(f.Ids.NewId(), tenant, current.CredentialId, current.Purpose, initiator.AccountId, initiator.AuthenticationSessionId, initiator.PrincipalId, current.Revision,
            TenantCredentialOAuthRules.Epoch(current), Digest("state"), Digest("browser-proof"), "encrypted-pkce-private-proof",
            new("synthetic-client", "https://app.example.test/api/v2/integrations/tenant-google/oauth/callback", true, true), now, now.AddMinutes(5));
    }
    private static async Task<bool> BeginOAuth(Fixture f, CredentialOAuthAttempt a)
    {
        var state = (await f.Iam.ReadTenantAsync(a.TenantId))!;
        return await ((ITenantCredentialOAuthStore)f.Iam).TryBeginCredentialOAuthAsync(Next(state), state.Tenant.Revision, a, f.Audit(a.TenantId, a.PrincipalId, a.CredentialId));
    }
    private static Credential OAuthConnected(Fixture f, Credential current, string subject) => TenantCredentialOAuthRules.Connect(current,
        new(subject, "fixture@example.test", current.Purpose == CredentialPurpose.EmailAccount ? ["openid", "email", EmailAccountProfile.ReadScope, EmailAccountProfile.SendScope]
            : ["openid", "email", DriveAccountProfile.Scope], f.Protector.Protect(current.CredentialId, current.ProtectionVersion, "refresh-private-value"), true), f.Clock.GetUtcNow());
}
